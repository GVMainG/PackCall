#!/usr/bin/env python3
"""Verify that the aggregate CI check succeeded for a commit SHA before tagging.

Usage:
    GITHUB_TOKEN=... python3 scripts/ci/release_gate.py --commit <sha>

Exit codes:
    0  every required check succeeded for that commit
    1  a required check failed, is missing, or is still running (do not tag)
    2  environment error (token, repository, network, HTTP, JSON)

The token is read from ``GITHUB_TOKEN`` only and is never printed.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.request

DEFAULT_REQUIRED = ("CI required",)

_API_ROOT = "https://api.github.com"


def fetch_check_runs(repo: str, sha: str, token: str) -> list[dict]:
    url = f"{_API_ROOT}/repos/{repo}/commits/{sha}/check-runs?per_page=100"
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "packcall-release-gate",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        payload = json.load(response)
    return list(payload.get("check_runs", []))


def evaluate(check_runs: list[dict], required: list[str]) -> list[str]:
    problems: list[str] = []
    for name in required:
        matches = [run for run in check_runs if run.get("name") == name]
        if not matches:
            problems.append(f"required check `{name}` is missing")
            continue
        for run in matches:
            if run.get("status") != "completed":
                problems.append(f"check `{name}` is not completed (status={run.get('status')})")
            elif run.get("conclusion") != "success":
                problems.append(
                    f"check `{name}` did not succeed (conclusion={run.get('conclusion')})"
                )
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--commit", required=True, help="commit SHA the tag will point to")
    parser.add_argument(
        "--repo",
        default=os.environ.get("GITHUB_REPOSITORY"),
        help="owner/name (default: GITHUB_REPOSITORY)",
    )
    parser.add_argument(
        "--required",
        action="append",
        metavar="NAME",
        help="required check name (repeatable; default: CI required)",
    )
    args = parser.parse_args()

    required = args.required or list(DEFAULT_REQUIRED)
    repo = args.repo
    token = os.environ.get("GITHUB_TOKEN")

    if not repo:
        print(
            "error: repository is unknown; pass --repo or set GITHUB_REPOSITORY",
            file=sys.stderr,
        )
        return 2
    if not token:
        print("error: GITHUB_TOKEN is not set", file=sys.stderr)
        return 2

    try:
        check_runs = fetch_check_runs(repo, args.commit, token)
    except urllib.error.HTTPError as error:
        print(
            f"error: GitHub API responded {error.code} for {repo}@{args.commit}",
            file=sys.stderr,
        )
        return 2
    except (urllib.error.URLError, OSError, ValueError) as error:
        print(f"error: cannot read checks for {repo}@{args.commit}: {error}", file=sys.stderr)
        return 2

    problems = evaluate(check_runs, required)
    if problems:
        for problem in problems:
            print(f"BLOCKED: {problem}", file=sys.stderr)
        print(
            f"BLOCKED: {len(problems)} problem(s); do not tag {args.commit}",
            file=sys.stderr,
        )
        return 1

    print(f"OK: required checks passed for {args.commit}: {', '.join(required)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
