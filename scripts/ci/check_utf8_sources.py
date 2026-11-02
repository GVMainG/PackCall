#!/usr/bin/env python3
"""Fail when a tracked C# source is not strict UTF-8 or contains U+FFFD.

Usage:
    python3 scripts/ci/check_utf8_sources.py                 # scan git-tracked *.cs
    python3 scripts/ci/check_utf8_sources.py --paths FILE... # scan the given files

Default scope: git-tracked files whose name ends in ``.cs`` (case-insensitive).
``--paths`` may name any file. Exit codes: 0 when every checked file is valid,
1 when at least one file is invalid, 2 on an environment or usage error (git
missing, not a repository, unreadable file).

Contract notes:
- A UTF-8 BOM is tolerated because it is valid UTF-8 and does not hide text.
- U+FFFD replacement characters are rejected: they mean text was decoded
  lossily somewhere upstream, which is exactly the mojibake this gate guards
  against.
- At most the first invalid offset per file is reported; byte offsets are
  relative to the file bytes on disk. Line numbers are computed for LF, CRLF
  and CR line endings alike.
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path


def _run_git(args: list[str]) -> subprocess.CompletedProcess[bytes] | None:
    try:
        return subprocess.run(["git", *args], capture_output=True, check=True)
    except FileNotFoundError:
        print("error: git is not available", file=sys.stderr)
    except OSError as error:
        print(f"error: cannot run git: {error}", file=sys.stderr)
    except subprocess.CalledProcessError as error:
        detail = error.stderr.decode("utf-8", "replace").strip()
        print(
            f"error: `git {' '.join(args)}` failed (not a git repository?): {detail}",
            file=sys.stderr,
        )
    return None


def _tracked_sources() -> list[Path] | None:
    root_result = _run_git(["rev-parse", "--show-toplevel"])
    if root_result is None:
        return None
    root = Path(os.fsdecode(root_result.stdout.strip()))
    listed = _run_git(["-C", str(root), "ls-files", "-z"])
    if listed is None:
        return None
    names = [os.fsdecode(name) for name in listed.stdout.split(b"\0") if name]
    return [root / name for name in names if name.lower().endswith(".cs")]


def _line_of(data: bytes, offset: int) -> int:
    window = data[:offset]
    return window.count(b"\n") + window.count(b"\r") - window.count(b"\r\n") + 1


def _check_file(path: Path) -> tuple[str | None, str | None]:
    """Return (invalid message, read error). At most one is set."""
    try:
        data = path.read_bytes()
    except OSError as error:
        return None, f"error: cannot read {path}: {error}"
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as error:
        offset = error.start
        message = (
            f"{path}: line {_line_of(data, offset)} (byte {offset}): "
            f"invalid UTF-8: {error.reason}"
        )
        return message, None
    position = text.find("\ufffd")
    if position != -1:
        offset = len(text[:position].encode("utf-8"))
        message = (
            f"{path}: line {_line_of(data, offset)} (byte {offset}): "
            "replacement character U+FFFD (text was decoded lossily)"
        )
        return message, None
    return None, None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--paths",
        nargs="+",
        metavar="FILE",
        help="check these files instead of the git-tracked *.cs sources",
    )
    args = parser.parse_args()

    if args.paths:
        paths = [Path(name) for name in args.paths]
    else:
        tracked = _tracked_sources()
        if tracked is None:
            return 2
        paths = tracked

    if not paths:
        print("error: no C# sources found to check", file=sys.stderr)
        return 2

    invalid = 0
    read_errors = 0
    for path in paths:
        message, read_error = _check_file(path)
        if message is not None:
            print(message, file=sys.stderr)
            invalid += 1
        if read_error is not None:
            print(read_error, file=sys.stderr)
            read_errors += 1

    if invalid:
        print(f"FAIL: {invalid} of {len(paths)} files are not valid UTF-8", file=sys.stderr)
    if read_errors:
        print(f"ERROR: {read_errors} of {len(paths)} files could not be read", file=sys.stderr)
        return 2
    if invalid:
        return 1
    print(f"OK: {len(paths)} files are strict UTF-8 with no U+FFFD")
    return 0


if __name__ == "__main__":
    sys.exit(main())
