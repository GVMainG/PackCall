"""Offline structural checks that the CI workflow publishes the expected gate."""

import importlib.util
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"
GATE = ROOT / "scripts" / "ci" / "release_gate.py"

_spec = importlib.util.spec_from_file_location("release_gate_under_test", GATE)
release_gate = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(release_gate)

TEXT = WORKFLOW.read_text(encoding="utf-8")


def job_block(job_id: str) -> str:
    match = re.search(rf"(?ms)^  {job_id}:\n(.*?)(?=^  [a-z0-9_]+:\n|\Z)", TEXT)
    if match is None:
        raise AssertionError(f"job {job_id} is missing from {WORKFLOW}")
    return match.group(1)


class WorkflowContractTests(unittest.TestCase):
    def test_aggregate_check_name_matches_gate_default(self):
        self.assertIn("name: CI required", job_block("ci_required"))
        self.assertEqual(("CI required",), release_gate.DEFAULT_REQUIRED)

    def test_every_job_is_a_dependency_of_the_aggregate(self):
        jobs_section = re.search(r"(?ms)^jobs:\n(.*?)(?=^[A-Za-z]|\Z)", TEXT)
        self.assertIsNotNone(jobs_section)
        jobs = set(re.findall(r"(?m)^  ([a-z0-9_]+):$", jobs_section.group(1))) - {"ci_required"}
        needs = re.search(r"(?ms)^    needs:\s*\[(.*?)\]", job_block("ci_required"))
        self.assertIsNotNone(needs)
        listed = {item.strip() for item in needs.group(1).split(",") if item.strip()}
        self.assertEqual(jobs, listed, "every job must be listed in ci_required.needs")

    def test_aggregate_runs_even_when_a_dependency_fails(self):
        self.assertIn("if: always()", job_block("ci_required"))

    def test_dotnet_pipelines_use_a_pipefail_capable_shell(self):
        # `shell: bash` enables -o pipefail; without it a failing dotnet piped into tee is swallowed.
        self.assertGreaterEqual(TEXT.count("shell: bash"), 2)

    def test_gate_tests_run_in_ci(self):
        self.assertIn("tests/tools/ci", job_block("utf8_sources"))


if __name__ == "__main__":
    unittest.main()
