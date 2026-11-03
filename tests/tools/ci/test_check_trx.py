"""Offline checks for the TRX executed-test counter."""

import pathlib
import subprocess
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts" / "ci" / "check_trx.py"

TRX_HEADER = (
    '<?xml version="1.0" encoding="utf-8"?>'
    '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
    "<Results>{results}</Results></TestRun>"
)
RESULT = '<UnitTestResult testId="1" testName="{name}" outcome="Passed" />'


def trx(*names):
    results = "".join(RESULT.format(name=name) for name in names)
    return TRX_HEADER.format(results=results)


class CheckTrxTests(unittest.TestCase):
    def run_gate(self, path):
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(path)],
            capture_output=True,
            text=True,
            encoding="utf-8",
        )

    def test_executed_tests_pass_and_are_counted(self):
        path = pathlib.Path(__file__).with_suffix(".fixture.trx")
        try:
            path.write_text(trx("One", "Two"), encoding="utf-8")
            result = self.run_gate(path)
        finally:
            path.unlink(missing_ok=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("OK: 2 tests executed", result.stdout)

    def test_empty_report_fails(self):
        path = pathlib.Path(__file__).with_suffix(".fixture.trx")
        try:
            path.write_text(trx(), encoding="utf-8")
            result = self.run_gate(path)
        finally:
            path.unlink(missing_ok=True)
        self.assertEqual(1, result.returncode)
        self.assertIn("contains no executed tests", result.stderr)

    def test_missing_file_is_an_environment_error(self):
        result = self.run_gate(ROOT / "definitely-missing.trx")
        self.assertEqual(2, result.returncode)
        self.assertIn("cannot read TRX", result.stderr)

    def test_malformed_xml_is_an_environment_error(self):
        path = pathlib.Path(__file__).with_suffix(".fixture.trx")
        try:
            path.write_text("<TestRun><broken>", encoding="utf-8")
            result = self.run_gate(path)
        finally:
            path.unlink(missing_ok=True)
        self.assertEqual(2, result.returncode)
        self.assertIn("cannot read TRX", result.stderr)


if __name__ == "__main__":
    unittest.main()
