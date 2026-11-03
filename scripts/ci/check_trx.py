#!/usr/bin/env python3
"""Fail when a TRX test report contains no executed tests (and optionally skips).

Usage:
    python3 scripts/ci/check_trx.py artifacts/test-results/unit.trx
    python3 scripts/ci/check_trx.py --forbid-skipped artifacts/test-results/integration.trx

The CI gates use this so a broken test filter cannot silently turn a test job
into a no-op, and so the integration job can prove that no test was skipped.
VSTest writes skipped tests as `UnitTestResult outcome="NotExecuted"` and keeps
`Counters@notExecuted` at 0, so this script counts the results themselves rather
than trusting the counters.

Exit codes: 0 when the report is acceptable, 1 when no test executed or a skip
is forbidden and present, 2 when the TRX cannot be read or parsed.
"""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET

_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
_NOT_EXECUTED = "NotExecuted"


def outcomes(root: ET.Element) -> list[str]:
    return [
        element.get("outcome") or ""
        for element in root.findall(".//t:UnitTestResult", _NS)
    ]


def executed_count(root: ET.Element) -> int:
    return sum(1 for outcome in outcomes(root) if outcome != _NOT_EXECUTED)


def skipped_count(root: ET.Element) -> int:
    return sum(1 for outcome in outcomes(root) if outcome == _NOT_EXECUTED)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", metavar="TRX")
    parser.add_argument(
        "--forbid-skipped",
        action="store_true",
        help="fail when the report contains skipped tests",
    )
    args = parser.parse_args(argv[1:])

    try:
        root = ET.parse(args.report).getroot()
    except (OSError, ET.ParseError) as error:
        print(f"error: cannot read TRX {args.report}: {error}", file=sys.stderr)
        return 2

    executed = executed_count(root)
    skipped = skipped_count(root)
    if executed == 0:
        print(f"FAIL: TRX {args.report} contains no executed tests", file=sys.stderr)
        return 1
    if args.forbid_skipped and skipped > 0:
        print(
            f"FAIL: TRX {args.report} reports {skipped} skipped test(s); skips are forbidden here",
            file=sys.stderr,
        )
        return 1

    print(f"OK: {executed} test(s) executed, {skipped} skipped ({args.report})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
