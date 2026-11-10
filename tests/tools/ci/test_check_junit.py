"""Offline checks for the JUnit executed/skipped counter."""

import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts" / "ci" / "check_junit.py"


def junit(tests, skipped=0, failures=0, errors=0):
    cases = "".join('<testcase name="t{i}" />' for i in range(tests + skipped))
    return (
        '<testsuites><testsuite name="pytest" tests="{tests}" failures="{failures}" '
        'errors="{errors}" skipped="{skipped}">{cases}</testsuite></testsuites>'
    ).format(tests=tests, failures=failures, errors=errors, skipped=skipped, cases=cases)


class CheckJunitTests(unittest.TestCase):
    def run_on(self, content, *arguments):
        handle = tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", suffix=".xml", delete=False
        )
        handle.write(content)
        handle.close()
        try:
            return subprocess.run(
                [sys.executable, str(SCRIPT), *arguments, handle.name],
                capture_output=True,
                text=True,
                encoding="utf-8",
            )
        finally:
            os.unlink(handle.name)

    def test_executed_tests_pass(self):
        result = self.run_on(junit(2))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("OK: 2 test(s) executed, 0 skipped", result.stdout)

    def test_zero_tests_fail(self):
        result = self.run_on(junit(0))
        self.assertEqual(1, result.returncode)
        self.assertIn("executed no tests", result.stderr)

    def test_skips_fail_under_forbid_skipped(self):
        result = self.run_on(junit(1, skipped=2), "--forbid-skipped")
        self.assertEqual(1, result.returncode)
        self.assertIn("2 skipped test(s); skips are forbidden", result.stderr)

    def test_skips_pass_without_flag(self):
        result = self.run_on(junit(1, skipped=2))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("1 test(s) executed, 2 skipped", result.stdout)

    def test_missing_file_is_an_environment_error(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), str(ROOT / "missing.xml")],
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("cannot read JUnit", result.stderr)

    def test_malformed_xml_is_an_environment_error(self):
        result = self.run_on("<testsuites><broken>")
        self.assertEqual(2, result.returncode)
        self.assertIn("cannot read JUnit", result.stderr)


if __name__ == "__main__":
    unittest.main()
