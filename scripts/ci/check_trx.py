#!/usr/bin/env python3
"""Fail when a TRX test report contains no executed tests.

Usage:
    python3 scripts/ci/check_trx.py artifacts/test-results/unit.trx

The CI gate uses this so a broken test filter cannot silently turn the unit job
into a no-op: a report with zero executed tests is a failure, not a pass.

Exit codes: 0 at least one test ran, 1 no executed tests, 2 unreadable/invalid TRX.
"""

from __future__ import annotations

import sys
import xml.etree.ElementTree as ET

_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def count_executed(path: str) -> int:
    root = ET.parse(path).getroot()
    return len(root.findall(".//t:UnitTestResult", _NS))


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(f"usage: {argv[0]} <report.trx>", file=sys.stderr)
        return 2
    try:
        executed = count_executed(argv[1])
    except (OSError, ET.ParseError) as error:
        print(f"error: cannot read TRX {argv[1]}: {error}", file=sys.stderr)
        return 2
    if executed == 0:
        print(f"FAIL: TRX {argv[1]} contains no executed tests", file=sys.stderr)
        return 1
    print(f"OK: {executed} tests executed ({argv[1]})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
