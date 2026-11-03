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
    def __init__(self, payload):
        self._data = json.dumps(payload).encode("utf-8")

    def __enter__(self):
        return self

    def __exit__(self, *excinfo):
        return False

    def read(self, *args):
        return self._data


def run_gate(payload=None, *, argv=None, environment=None, urlopen_error=None):
    arguments = argv or ["release_gate.py", "--commit", SHA]
    out, err = io.StringIO(), io.StringIO()
    with contextlib.ExitStack() as stack:
        stack.enter_context(patch.dict(os.environ, environment or ENVIRONMENT, clear=True))
        stack.enter_context(patch.object(sys, "argv", arguments))
        stack.enter_context(contextlib.redirect_stdout(out))
        stack.enter_context(contextlib.redirect_stderr(err))
        if urlopen_error is not None:
            stack.enter_context(
                patch.object(release_gate.urllib.request, "urlopen", side_effect=urlopen_error)
            )
        else:
            stack.enter_context(
                patch.object(
                    release_gate.urllib.request,
                    "urlopen",
                    return_value=FakeResponse(payload or {}),
                )
            )
        return release_gate.main(), out.getvalue(), err.getvalue()


def check_run(conclusion="success", status="completed", name="CI required"):
    return {"name": name, "status": status, "conclusion": conclusion}


class ReleaseGateTests(unittest.TestCase):
    def test_successful_required_check_passes(self):
        code, out, _ = run_gate({"check_runs": [check_run()]})
        self.assertEqual(0, code)
        self.assertIn("OK: required checks passed", out)

    def test_failed_required_check_blocks(self):
        code, _, err = run_gate({"check_runs": [check_run(conclusion="failure")]})
        self.assertEqual(1, code)
        self.assertIn("did not succeed", err)
        self.assertIn("do not tag", err)

    def test_missing_required_check_blocks(self):
        code, _, err = run_gate({"check_runs": []})
        self.assertEqual(1, code)
        self.assertIn("is missing", err)

    def test_pending_required_check_blocks(self):
        code, _, err = run_gate({"check_runs": [check_run(status="in_progress", conclusion=None)]})
        self.assertEqual(1, code)
        self.assertIn("is not completed", err)

    def test_custom_required_name_is_honoured(self):
        argv = ["release_gate.py", "--commit", SHA, "--required", "custom gate"]
        payload = {"check_runs": [check_run(name="custom gate")]}
        code, out, _ = run_gate(payload, argv=argv)
        self.assertEqual(0, code)
        self.assertIn("custom gate", out)

    def test_http_error_is_an_environment_error(self):
        error = urllib.error.HTTPError("https://api.github.com", 500, "boom", None, None)
        code, _, err = run_gate(urlopen_error=error)
        self.assertEqual(2, code)
        self.assertIn("GitHub API responded 500", err)

    def test_missing_token_is_an_environment_error(self):
        environment = {"GITHUB_REPOSITORY": "owner/repo"}
        code, _, err = run_gate(environment=environment)
        self.assertEqual(2, code)
        self.assertIn("GITHUB_TOKEN is not set", err)


if __name__ == "__main__":
    unittest.main()
