#!/usr/bin/env python3
"""Fail when a JUnit XML report executed no tests (and optionally skips).

Usage:
    python3 scripts/ci/check_junit.py artifacts/python-offline.xml
    python3 scripts/ci/check_junit.py --forbid-skipped artifacts/python-offline.xml

The Python CI gate uses this so a broken collection path cannot silently turn
the offline check into a no-op: pytest exits 0 even when every test was
skipped, so the guard reads the JUnit report and fails on zero executed tests
or, with --forbid-skipped, on any skipped test.

Exit codes: 0 when the report is acceptable, 1 when no test executed or a skip
is forbidden and present, 2 when the report cannot be read or parsed.
"""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET


def totals(root: ET.Element) -> tuple[int, int]:
    tests = 0
    skipped = 0
    for suite in root.findall(".//testsuite"):
        try:
            tests += int(suite.get("tests") or 0)
        except ValueError:
            pass
        try:
            skipped += int(suite.get("skipped") or 0)
        except ValueError:
            pass
    return tests, skipped


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", metavar="XML")
    parser.add_argument(
        "--forbid-skipped",
        action="store_true",
        help="fail when the report contains skipped tests",
    )
    args = parser.parse_args(argv[1:])

    try:
        root = ET.parse(args.report).getroot()
    except (OSError, ET.ParseError) as error:
        print(f"error: cannot read JUnit {args.report}: {error}", file=sys.stderr)
        return 2

    tests, skipped = totals(root)
    if tests == 0:
        print(f"FAIL: JUnit {args.report} executed no tests", file=sys.stderr)
        return 1
    if args.forbid_skipped and skipped > 0:
        print(
            f"FAIL: JUnit {args.report} reports {skipped} skipped test(s); skips are forbidden here",
            file=sys.stderr,
        )
        return 1

    print(f"OK: {tests} test(s) executed, {skipped} skipped ({args.report})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
