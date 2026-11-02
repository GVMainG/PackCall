"""Offline checks for the strict UTF-8 source gate.

These tests pin the gate's observable contract: a valid file passes, a cp1251
file fails with a file/line/byte message, a lossily decoded file (U+FFFD) fails,
a missing path is an environment error (exit 2), and running outside a git
repository is an environment error as well.
"""

import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid
from contextlib import contextmanager

ROOT = pathlib.Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "scripts" / "ci" / "check_utf8_sources.py"


@contextmanager
def temp_directory():
    """Temporary directory created with normal permissions (mkdtemp is 0700)."""
    directory = os.path.join(tempfile.gettempdir(), f"packcall-utf8-{uuid.uuid4().hex}")
    os.makedirs(directory)
    try:
        yield directory
    finally:
        shutil.rmtree(directory, ignore_errors=True)


def run_gate(*arguments, cwd=None):
    return subprocess.run(
        [sys.executable, str(SCRIPT), *arguments],
        capture_output=True,
        text=True,
        encoding="utf-8",
        cwd=cwd,
    )


class CheckUtf8SourcesTests(unittest.TestCase):
    def test_valid_utf8_file_passes(self):
        with temp_directory() as directory:
            path = pathlib.Path(directory) / "Ok.cs"
            path.write_text("// Захваченная доставка\n", encoding="utf-8")
            result = run_gate("--paths", str(path))
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("OK: 1 files are strict UTF-8", result.stdout)

    def test_cp1251_file_fails_with_position(self):
        with temp_directory() as directory:
            path = pathlib.Path(directory) / "Bad.cs"
            path.write_bytes("// тест\n".encode("cp1251"))
            result = run_gate("--paths", str(path))
            self.assertEqual(1, result.returncode, result.stdout)
            self.assertIn("invalid UTF-8", result.stderr)
            self.assertIn("line 1 (byte 3)", result.stderr)
            self.assertIn("FAIL: 1 of 1 files are not valid UTF-8", result.stderr)

    def test_replacement_character_fails(self):
        with temp_directory() as directory:
            path = pathlib.Path(directory) / "Mojibake.cs"
            path.write_text("// \ufffd\n", encoding="utf-8")
            result = run_gate("--paths", str(path))
            self.assertEqual(1, result.returncode, result.stdout)
            self.assertIn("replacement character U+FFFD", result.stderr)

    def test_cr_only_line_endings_report_the_right_line(self):
        with temp_directory() as directory:
            path = pathlib.Path(directory) / "CrOnly.cs"
            path.write_bytes(b"// ok\r" + "// тест\r".encode("cp1251"))
            result = run_gate("--paths", str(path))
            self.assertEqual(1, result.returncode, result.stdout)
            self.assertIn("line 2 (byte 9)", result.stderr)

    def test_missing_path_is_an_environment_error(self):
        result = run_gate("--paths", "definitely-missing.cs")
        self.assertEqual(2, result.returncode)
        self.assertIn("cannot read", result.stderr)

    def test_outside_a_git_repository_is_an_environment_error(self):
        with temp_directory() as directory:
            result = run_gate(cwd=directory)
            self.assertEqual(2, result.returncode)
            self.assertIn("not a git repository", result.stderr)


if __name__ == "__main__":
    unittest.main()
