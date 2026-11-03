"""Offline checks for the release gate; the GitHub API is mocked."""

import contextlib
import importlib.util
import io
import json
import os
import pathlib
import sys
import unittest
import urllib.error
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts" / "ci" / "release_gate.py"

_spec = importlib.util.spec_from_file_location("release_gate_under_test", SCRIPT)
release_gate = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(release_gate)

SHA = "a" * 40
ENVIRONMENT = {"GITHUB_TOKEN": "test-token", "GITHUB_REPOSITORY": "owner/repo"}


class FakeResponse:
    def __init__(self, body):
        self._body = body

    def __enter__(self):
        return self

    def __exit__(self, *excinfo):
        return False

    def read(self, *args):
        return self._body


class Recorder:
    def __init__(self, body):
        self.body = body
        self.requests = []

    def __call__(self, request, timeout=None):
        self.requests.append(request)
        return FakeResponse(self.body)


def check_runs_payload(*runs):
    return json.dumps({"total_count": len(runs), "check_runs": list(runs)}).encode("utf-8")


def check_run(**overrides):
    run = {
        "name": "CI required",
        "status": "completed",
        "conclusion": "success",
        "id": 42,
        "started_at": "2026-11-11T17:00:00Z",
        "completed_at": "2026-11-11T17:10:00Z",
        "html_url": "https://github.com/owner/repo/actions/runs/42",
        "app": {"slug": "github-actions"},
    }
    run.update(overrides)
    return run


def run_gate(body=None, *, argv=None, environment=None, error=None):
    arguments = argv or ["release_gate.py", "--commit", SHA]
    out, err = io.StringIO(), io.StringIO()
    recorder = Recorder(body if body is not None else check_runs_payload(check_run()))
    with contextlib.ExitStack() as stack:
        stack.enter_context(
            patch.dict(os.environ, ENVIRONMENT if environment is None else environment, clear=True)
        )
        stack.enter_context(patch.object(sys, "argv", arguments))
        stack.enter_context(contextlib.redirect_stdout(out))
        stack.enter_context(contextlib.redirect_stderr(err))
        if error is not None:
            stack.enter_context(
                patch.object(release_gate.urllib.request, "urlopen", side_effect=error)
            )
        else:
            stack.enter_context(
                patch.object(release_gate.urllib.request, "urlopen", side_effect=recorder)
            )
        return release_gate.main(), out.getvalue(), err.getvalue(), recorder


class ReleaseGateTests(unittest.TestCase):
    def test_successful_required_check_passes_with_evidence_details(self):
        code, out, _, _ = run_gate()
        self.assertEqual(0, code)
        self.assertIn("OK: CI required succeeded", out)
        self.assertIn("run 42", out)
        self.assertIn("https://github.com/owner/repo/actions/runs/42", out)

    def test_request_targets_check_runs_with_latest_filter_and_auth(self):
        _, _, _, recorder = run_gate()
        request = recorder.requests[0]
        self.assertIn(f"/repos/owner/repo/commits/{SHA}/check-runs", request.full_url)
        self.assertIn("check_name=CI+required", request.full_url)
        self.assertIn("filter=latest", request.full_url)
        self.assertEqual("Bearer test-token", request.get_header("Authorization"))
        self.assertEqual("2022-11-28", request.get_header("X-github-api-version"))

    def test_failed_required_check_blocks(self):
        code, _, err, _ = run_gate(check_runs_payload(check_run(conclusion="failure")))
        self.assertEqual(1, code)
        self.assertIn("did not succeed", err)
        self.assertIn("do not tag", err)

    def test_cancelled_check_blocks(self):
        code, _, err, _ = run_gate(check_runs_payload(check_run(conclusion="cancelled")))
        self.assertEqual(1, code)
        self.assertIn("conclusion=cancelled", err)

    def test_missing_required_check_blocks(self):
        code, _, err, _ = run_gate(check_runs_payload())
        self.assertEqual(1, code)
        self.assertIn("is missing", err)

    def test_pending_required_check_blocks(self):
        code, _, err, _ = run_gate(
            check_runs_payload(check_run(status="in_progress", conclusion=None))
        )
        self.assertEqual(1, code)
        self.assertIn("is not completed", err)

    def test_check_from_another_app_blocks(self):
        code, _, err, _ = run_gate(check_runs_payload(check_run(app={"slug": "evil-app"})))
        self.assertEqual(1, code)
        self.assertIn("not published by GitHub Actions", err)

    def test_latest_run_wins_over_an_older_failure(self):
        older = check_run(id=1, conclusion="failure", started_at="2026-11-11T16:00:00Z")
        newer = check_run(id=2, started_at="2026-11-11T17:00:00Z")
        code, out, _, _ = run_gate(check_runs_payload(older, newer))
        self.assertEqual(0, code, out)
        self.assertIn("run 2", out)

    def test_custom_required_name_is_queried(self):
        argv = ["release_gate.py", "--commit", SHA, "--required", "custom gate"]
        body = check_runs_payload(check_run(name="custom gate"))
        code, out, _, recorder = run_gate(body, argv=argv)
        self.assertEqual(0, code)
        self.assertIn("custom gate", out)
        self.assertIn("check_name=custom+gate", recorder.requests[0].full_url)

    def test_http_404_blocks_tagging(self):
        error = urllib.error.HTTPError("https://api.github.com", 404, "not found", None, None)
        code, _, err, _ = run_gate(error=error)
        self.assertEqual(1, code)
        self.assertIn("not found", err)
        self.assertIn("do not tag", err)

    def test_http_auth_error_is_an_environment_error(self):
        error = urllib.error.HTTPError("https://api.github.com", 401, "bad credentials", None, None)
        code, _, err, _ = run_gate(error=error)
        self.assertEqual(2, code)
        self.assertIn("responded 401", err)

    def test_network_error_is_an_environment_error(self):
        error = urllib.error.URLError("name resolution failed")
        code, _, err, _ = run_gate(error=error)
        self.assertEqual(2, code)
        self.assertIn("cannot read checks", err)

    def test_malformed_json_is_an_environment_error(self):
        code, _, err, _ = run_gate(b"not json")
        self.assertEqual(2, code)
        self.assertIn("cannot read checks", err)

    def test_unexpected_payload_shape_is_an_environment_error(self):
        code, _, err, _ = run_gate(json.dumps({"check_runs": None}).encode("utf-8"))
        self.assertEqual(2, code)
        self.assertIn("unexpected check-runs payload", err)

    def test_missing_repository_is_an_environment_error(self):
        code, _, err, _ = run_gate(environment={"GITHUB_TOKEN": "test-token"})
        self.assertEqual(2, code)
        self.assertIn("repository must be owner/name", err)

    def test_short_or_ref_commit_is_rejected(self):
        argv = ["release_gate.py", "--commit", "master"]
        code, _, err, _ = run_gate(argv=argv)
        self.assertEqual(2, code)
        self.assertIn("40-hex commit SHA", err)

    def test_missing_token_is_an_environment_error(self):
        code, _, err, _ = run_gate(environment={"GITHUB_REPOSITORY": "owner/repo"})
        self.assertEqual(2, code)
        self.assertIn("GITHUB_TOKEN is not set", err)

    def test_token_is_never_printed(self):
        cases = (
            (None, None, 0),
            (check_runs_payload(check_run(conclusion="failure")), None, 1),
            (None, urllib.error.URLError("boom"), 2),
        )
        for body, error, expected in cases:
            code, out, err, _ = run_gate(body, error=error)
            self.assertEqual(expected, code)
            self.assertNotIn("test-token", out + err)


if __name__ == "__main__":
    unittest.main()
