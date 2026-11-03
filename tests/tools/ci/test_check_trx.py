"""Offline checks for the TRX executed/skipped counter."""

import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts" / "ci" / "check_trx.py"
_NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


def result(name, outcome="Passed"):
    return f'<UnitTestResult testId="1" testName="{name}" outcome="{outcome}" />'


def trx(*entries, counters=None):
    """Build a TRX. entries are (name, outcome) tuples; counters overrides the
    ResultSummary/Counters block (VSTest reports notExecuted=0 even for skips)."""
    results = "".join(result(name, outcome) for name, outcome in entries)
    executed = sum(1 for _, outcome in entries if outcome != "NotExecuted")
    skipped = sum(1 for _, outcome in entries if outcome == "NotExecuted")
    if counters is None:
        counters = (
            f'<ResultSummary><Counters total="{executed + skipped}" executed="{executed}" '
            f'notExecuted="{skipped}" /></ResultSummary>'
        )
    return (
        f'<TestRun xmlns="{_NS}">{counters}<Results>{results}</Results></TestRun>'
    )


class CheckTrxTests(unittest.TestCase):
    def with_fixture(self, content):
        handle = tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", suffix=".trx", delete=False
        )
        handle.write(content)
        handle.close()
        return handle.name

    def run_gate(self, *arguments):
        return subprocess.run(
            [sys.executable, str(SCRIPT), *arguments],
            capture_output=True,
            text=True,
            encoding="utf-8",
        )

    def run_on(self, content, *arguments):
        path = self.with_fixture(content)
        try:
            return self.run_gate(*arguments, path)
        finally:
            os.unlink(path)

    def test_executed_tests_pass_and_are_counted(self):
        result = self.run_on(trx(("One", "Passed"), ("Two", "Passed")))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("OK: 2 test(s) executed, 0 skipped", result.stdout)

    def test_empty_report_fails(self):
        result = self.run_on(trx())
        self.assertEqual(1, result.returncode)
        self.assertIn("contains no executed tests", result.stderr)

    def test_real_format_skip_fails_under_forbid_skipped(self):
        # VSTest writes skipped tests as outcome=NotExecuted and notExecuted=0 in Counters.
        content = trx(("One", "Passed"), ("Two", "NotExecuted"), counters='<ResultSummary><Counters total="2" executed="1" notExecuted="0" /></ResultSummary>')
        result = self.run_on(content, "--forbid-skipped")
        self.assertEqual(1, result.returncode)
        self.assertIn("1 skipped test(s); skips are forbidden", result.stderr)

    def test_real_format_skip_passes_without_flag(self):
        content = trx(("One", "Passed"), ("Two", "NotExecuted"), counters='<ResultSummary><Counters total="2" executed="1" notExecuted="0" /></ResultSummary>')
        result = self.run_on(content)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("1 test(s) executed, 1 skipped", result.stdout)

    def test_all_skipped_report_fails_as_no_execution(self):
        result = self.run_on(trx(("One", "NotExecuted"), ("Two", "NotExecuted")))
        self.assertEqual(1, result.returncode)
        self.assertIn("contains no executed tests", result.stderr)

    def test_skip_detection_does_not_rely_on_counters(self):
        # No ResultSummary at all: the skip must still be detected from the results.
        content = trx(("One", "Passed"), ("Two", "NotExecuted"), counters="")
        result = self.run_on(content, "--forbid-skipped")
        self.assertEqual(1, result.returncode)
        self.assertIn("1 skipped test(s)", result.stderr)

    def test_zero_skip_with_flag_passes(self):
        result = self.run_on(trx(("One", "Passed"), ("Two", "Passed")), "--forbid-skipped")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("0 skipped", result.stdout)

    def test_missing_file_is_an_environment_error(self):
        result = self.run_gate(str(ROOT / "definitely-missing.trx"))
        self.assertEqual(2, result.returncode)
        self.assertIn("cannot read TRX", result.stderr)

    def test_malformed_xml_is_an_environment_error(self):
        result = self.run_on("<TestRun><broken>")
        self.assertEqual(2, result.returncode)
        self.assertIn("cannot read TRX", result.stderr)


if __name__ == "__main__":
    unittest.main()
