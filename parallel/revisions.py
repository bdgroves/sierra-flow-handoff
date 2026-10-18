#!/usr/bin/env python3
"""
revisions.py: every value USGS changed after first publishing it, found by
walking the COBOL repo's nightly commits of streamflow.csv.

    python3 parallel/revisions.py LEGACY_REPO > docs/data/revisions.json

The COBOL job never sees this: each night it overwrites streamflow.csv and
moves on. The SQL layer's MERGE ... OUTPUT logs the same changes in
sierra.flow_revision. Standard library only.
"""
import csv
import io
import json
import subprocess
import sys

V3_COMMIT = "202f82f"


def git(repo, *args):
    return subprocess.run(["git", *args], cwd=repo, capture_output=True, text=True).stdout


def main():
    repo = sys.argv[1]
    names = {r["site_id"]: r["short_name"] for r in csv.DictReader(io.StringIO(git(repo, "show", "HEAD:sites.csv")))}
    nights = [l.split() for l in git(repo, "log", "--reverse", "--format=%h %ad", "--date=short",
                                      f"{V3_COMMIT}^..HEAD", "--", "streamflow.csv").splitlines() if l]
    first, seen, changes = {}, {}, []
    for commit, night in nights:
        for r in csv.DictReader(io.StringIO(git(repo, "show", f"{commit}:streamflow.csv"))):
            key = (r["site_id"], r["date"])
            val = float(r["cfs"])
            if key in seen and seen[key] != val:
                changes.append({"night": night, "site_id": key[0], "name": names.get(key[0], key[0]),
                                "date": key[1], "first": first[key], "old": seen[key], "new": val})
            first.setdefault(key, val)
            seen[key] = val
    json.dump({"nights": len(nights), "gage_days": len(seen), "changes": changes}, sys.stdout, indent=1)


if __name__ == "__main__":
    main()
