#!/usr/bin/env python3
"""Verify that the aggregate CI check succeeded for a commit SHA before tagging.

Usage:
    GITHUB_TOKEN=... python3 scripts/ci/release_gate.py --commit <sha>

Exit codes:
    0  every required check succeeded for that commit
    1  a required check failed, is missing, is not from GitHub Actions, or is
       still running (do not tag); also used when the commit/checks are absent
    2  environment error (token, repository, malformed input, network, auth,
       server error, unexpected JSON)

The token is read from ``GITHUB_TOKEN`` only and is never printed.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

DEFAULT_REQUIRED = ("CI required",)

_API_ROOT = "https://api.github.com"
_SHA_RE = re.compile(r"^[0-9a-f]{40}$")
_REPO_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
_ACTIONS_APP = "github-actions"


def fetch_check_runs(repo: str, sha: str, name: str, token: str) -> list[dict]:
    """Return the latest check runs published for ``name`` on ``sha``."""
    query = urllib.parse.urlencode(
        {"check_name": name, "filter": "latest", "per_page": 100}
    )
    url = f"{_API_ROOT}/repos/{repo}/commits/{sha}/check-runs?{query}"
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
    if not isinstance(payload, dict) or not isinstance(payload.get("check_runs"), list):
        raise ValueError("unexpected check-runs payload")
    return [run for run in payload["check_runs"] if isinstance(run, dict)]


def latest_run(runs: list[dict]) -> dict | None:
    if not runs:
        return None
    return max(
        runs,
        key=lambda run: (
            str(run.get("started_at") or ""),
            str(run.get("completed_at") or ""),
            int(run.get("id") or 0),
        ),
    )


def evaluate(run: dict | None, name: str) -> str | None:
    if run is None:
        return f"required check `{name}` is missing for this commit"
    app = run.get("app") or {}
    if app.get("slug") != _ACTIONS_APP:
        return f"check `{name}` was not published by GitHub Actions (app={app.get('slug')})"
    if run.get("status") != "completed":
        return f"check `{name}` is not completed (status={run.get('status')})"
    if run.get("conclusion") != "success":
        return f"check `{name}` did not succeed (conclusion={run.get('conclusion')})"
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--commit", required=True, help="40-hex commit SHA the tag will point to")
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

    if not repo or not _REPO_RE.fullmatch(repo):
        print(
            "error: repository must be owner/name; pass --repo or set GITHUB_REPOSITORY",
            file=sys.stderr,
        )
        return 2
    if not _SHA_RE.fullmatch(args.commit):
        print("error: --commit must be a full 40-hex commit SHA", file=sys.stderr)
        return 2
    if not token:
        print("error: GITHUB_TOKEN is not set", file=sys.stderr)
        return 2

    problems: list[str] = []
    summary: list[str] = []
    for name in required:
        try:
            runs = fetch_check_runs(repo, args.commit, name, token)
        except urllib.error.HTTPError as error:
            if error.code in (404, 422):
                print(
                    f"BLOCKED: commit or check `{name}` not found for {repo}@{args.commit} "
                    f"(HTTP {error.code}); do not tag",
                    file=sys.stderr,
                )
                return 1
            print(
                f"error: GitHub API responded {error.code} for {repo}@{args.commit}",
                file=sys.stderr,
            )
            return 2
        except (urllib.error.URLError, OSError, ValueError) as error:
            print(
                f"error: cannot read checks for {repo}@{args.commit}: {error}",
                file=sys.stderr,
            )
            return 2

        run = latest_run(runs)
        problem = evaluate(run, name)
        if problem is not None:
            problems.append(problem)
            continue
        summary.append(
            f"{name} succeeded for {args.commit} "
            f"(run {run.get('id')}, completed_at={run.get('completed_at')}, "
            f"{run.get('html_url')})"
        )

    if problems:
        for problem in problems:
            print(f"BLOCKED: {problem}", file=sys.stderr)
        print(
            f"BLOCKED: {len(problems)} problem(s); do not tag {args.commit}",
            file=sys.stderr,
        )
        return 1

    for line in summary:
        print(f"OK: {line}")
    print(f"OK: required checks passed for {args.commit}; save this output as evidence")
    return 0


if __name__ == "__main__":
    sys.exit(main())
