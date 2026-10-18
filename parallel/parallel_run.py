#!/usr/bin/env python3
"""
parallel_run.py: run the old SIERRA-FLOW (GnuCOBOL) and the new one (C#)
on the same inputs and require identical output.

    python3 parallel/parallel_run.py LEGACY_REPO SIERRAFLOW_BIN [OUT_DIR]

LEGACY_REPO     a full clone of bdgroves/sierra-flow-cobol (the system of record)
SIERRAFLOW_BIN  the built C# job (bin/sierraflow)
OUT_DIR         where the evidence goes (default parallel/out)

Three kinds of case:

  night    every production night since COBOL V3, replayed from git history
           with both clocks pinned to the second that night ran
  edge     the latest night bent on purpose: a stale gage, a dead gage, a
           flood, a garbage value, a short record, a date in the future ...
  normals  NORMALS on a synthetic 30-year history, and on the real one when
           HISTORY_CSV names a history.csv fetched from USGS (CI does this weekly)

For each case it compares the report, results.csv (or normals.csv), every
console message and the return code. One differing byte fails the case.
Standard library only.
"""

import difflib
import json
import os
import random
import re
import shutil
import subprocess
import sys
import time
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

V3_COMMIT = "202f82f"          # SIERRA-FLOW V3 / NORMALS V1, 2026-10-02


def sh(cmd, cwd=None, env=None):
    r = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True)
    return r.returncode, r.stdout, r.stderr


def git_show(repo, rev, path):
    rc, out, _ = sh(["git", "show", f"{rev}:{path}"], cwd=repo)
    return out if rc == 0 else None


def compile_cobol(repo, build):
    build.mkdir(parents=True, exist_ok=True)
    for prog, exe in (("SIERRA-FLOW.cob", "sierra-flow"), ("NORMALS.cob", "normals")):
        rc, out, err = sh(["cobc", "-x", "-o", str(build / exe), str(Path(repo) / prog)])
        if rc != 0:
            sys.exit(f"cobc failed on {prog}:\n{err}")
    return build / "sierra-flow", build / "normals"


def run_pair(case_dir, files, old_cmd, new_cmd, outputs, clock=None):
    """Run old and new in sibling folders on identical copies of `files`."""
    env = dict(os.environ, TZ="UTC")
    if clock:
        env["COB_CURRENT_DATE"] = clock
    res = {}
    for side, cmd in (("cobol", old_cmd), ("csharp", new_cmd)):
        d = case_dir / side
        if d.exists():
            shutil.rmtree(d)
        d.mkdir(parents=True)
        for name, text in files.items():
            (d / name).write_text(text)
        t0 = time.perf_counter()
        rc, out, err = sh(cmd, cwd=d, env=env)
        ms = (time.perf_counter() - t0) * 1000
        (d / "SYSOUT.txt").write_text(out)
        res[side] = {"rc": rc, "ms": round(ms, 1),
                     "files": {o: (d / o).read_text() if (d / o).exists() else None for o in outputs + ["SYSOUT.txt"]}}
    diffs = []
    for o in outputs + ["SYSOUT.txt"]:
        a, b = res["cobol"]["files"][o], res["csharp"]["files"][o]
        if a != b:
            diff = "".join(difflib.unified_diff((a or "").splitlines(True), (b or "").splitlines(True),
                                                f"cobol/{o}", f"csharp/{o}", n=0))
            diffs.append({"file": o, "diff": diff[:4000]})
    if res["cobol"]["rc"] != res["csharp"]["rc"]:
        diffs.append({"file": "RETURN-CODE", "diff": f"cobol {res['cobol']['rc']} vs csharp {res['csharp']['rc']}"})
    lines = sum(len((res["cobol"]["files"][o] or "").splitlines()) for o in outputs + ["SYSOUT.txt"])
    return {"match": not diffs, "rc": res["cobol"]["rc"], "rc_csharp": res["csharp"]["rc"],
            "lines_compared": lines, "ms_cobol": res["cobol"]["ms"], "ms_csharp": res["csharp"]["ms"],
            "diffs": diffs}


# ---------------------------------------------------------------- nights

def production_nights(repo):
    rc, out, _ = sh(["git", "log", "--reverse", "--format=%H %h", f"{V3_COMMIT}^..HEAD", "--", "streamflow.csv"], cwd=repo)
    nights = []
    for line in out.split("\n"):
        if not line.strip():
            continue
        full, short = line.split()
        report = git_show(repo, full, "streamflow-report.txt") or ""
        m = re.search(r"RUN (\d{4})-(\d\d)-(\d\d) (\d\d:\d\d:\d\d) UTC", report)
        if not m:
            continue
        nights.append({
            "commit": short,
            "run": f"{m[1]}-{m[2]}-{m[3]} {m[4]}",
            "clock": f"{m[1]}/{m[2]}/{m[3]} {m[4]}",
            "files": {f: git_show(repo, full, f) for f in ("sites.csv", "normals.csv", "streamflow.csv")},
            "prod": {"streamflow-report.txt": report, "results.csv": git_show(repo, full, "results.csv")},
        })
    return nights


# ---------------------------------------------------------------- edge cases

def edge_cases(latest):
    """Each case is the latest production night with one thing broken on purpose."""
    sites, normals, flow = (latest["files"][f] for f in ("sites.csv", "normals.csv", "streamflow.csv"))
    head, *rows = flow.rstrip("\n").split("\n")
    run_day = datetime.strptime(latest["run"], "%Y-%m-%d %H:%M:%S").date()

    def build(rows_):
        return head + "\n" + "\n".join(rows_) + "\n"

    def site_rows(sid):
        return [r for r in rows if r.startswith(sid + ",")]

    def without(sid):
        return [r for r in rows if not r.startswith(sid + ",")]

    def scale(sid, k, last_n):
        out, mine = [], site_rows(sid)
        cut = set(mine[-last_n:])
        for r in rows:
            if r in cut:
                s, d, c, a = r.split(",")
                r = f"{s},{d},{float(c) * k:.2f},{a}"
            out.append(r)
        return out

    cases = {}
    cases["stale-gage"] = (build([r for r in rows if not (r.startswith("11264500,") and r in site_rows("11264500")[-6:])]),
                           "Merced at Happy Isles stops reporting six days ago")
    cases["dead-gage"] = (build(without("11303000")), "Stanislaus at Ripon sends nothing at all")
    cases["flood"] = (build(scale("11290000", 400, 5)), "Tuolumne at Modesto x400 for five days: over 100,000 cfs")
    def set_flow(sid, rows_to_values):
        out = []
        for r in rows:
            if r in rows_to_values:
                s, d, _, a = r.split(",")
                r = f"{s},{d},{rows_to_values[r]},{a}"
            out.append(r)
        return out

    bc = site_rows("11284400")
    cases["huge-rise"] = (build(set_flow("11284400", {**{r: "0.01" for r in bc[-14:-7]}, **{r: "99.00" for r in bc[-7:]}})),
                          "Big Creek goes from 0.01 to 99 cfs in a week: a 989,900% rise")
    cases["garbage-values"] = (build(rows[:5] + [rows[5].rsplit(",", 2)[0] + ",Ice,P", rows[6].rsplit(",", 2)[0] + ",,P",
                                                 rows[7].rsplit(",", 2)[0] + ",-12.50,P", "99999999,2026-10-01,5.00,P",
                                                 "11274790,10/01/2026,5.00,P"] + rows[8:]),
                               "an 'Ice' flag, a blank, a negative, an unknown gage, a US-style date")
    short = site_rows("11266500")[-10:]
    cases["short-record"] = (build(without("11266500") + short), "Pohono Bridge has only 10 days: no trend")
    extra = []
    d0 = datetime.strptime(site_rows("11274790")[0].split(",")[1], "%Y-%m-%d").date()
    for i in range(1, 16):
        extra.append(f"11274790,{d0 - timedelta(days=i)},9.99,A")
    cases["too-many-days"] = (build(list(reversed(extra)) + rows), "75 days for one gage: the table holds 70")
    fut = (run_day + timedelta(days=5)).isoformat()
    cases["future-date"] = (build(rows + [f"11276500,{fut},60.00,P"]), "a date five days in the future")
    cases["out-of-order"] = (build(rows[:59][::-1] + rows[59:]), "one gage's rows arrive newest-first")

    out = {}
    for name, (flowtext, why) in cases.items():
        out[name] = {"why": why, "files": {"sites.csv": sites, "normals.csv": normals, "streamflow.csv": flowtext},
                     "clock": latest["clock"]}
    # a 13th gage and a 6th basin go in sites.csv
    srows = sites.rstrip("\n").split("\n")
    more = srows + [f"1130{n:04d},Test Gage {n},TESTBASIN,4,{n},1,NATURAL,37.0,-120.0,TEST" for n in range(1, 6)]
    out["thirteen-gages"] = {"why": "13 gages in sites.csv: the table holds 12",
                             "files": {"sites.csv": "\n".join(more) + "\n", "normals.csv": normals, "streamflow.csv": flow},
                             "clock": latest["clock"]}
    return out


# ---------------------------------------------------------------- normals

def synthetic_history(seed=1996):
    """30 water years of plausible daily flow for three gages, with the awkward parts:
    a gage that starts in 2006, leap days, ties, zeros, gaps and a blank value."""
    rnd = random.Random(seed)
    lines = ["site_id,date,cfs"]
    gages = [("11111111", date(1995, 10, 1), 300.0), ("22222222", date(2006, 10, 13), 40.0),
             ("33333333", date(1995, 10, 1), 0.0)]
    for sid, start, base in gages:
        d = start
        while d <= date(2025, 9, 30):
            doy = d.timetuple().tm_yday
            season = 1 + 2.5 * max(0.0, __import__("math").sin((doy - 60) / 365 * 6.283))
            if base == 0.0:
                v = 0.0 if rnd.random() < 0.6 else round(rnd.random() * 2, 2)   # a creek that is mostly dry
            else:
                v = round(base * season * rnd.lognormvariate(0, 0.6), 2)
                if rnd.random() < 0.02:
                    v = round(v)                                               # ties
            if rnd.random() < 0.003:
                d += timedelta(days=1)                                         # gaps
                continue
            cfs = "" if rnd.random() < 0.0005 else f"{v:.2f}"
            lines.append(f"{sid},{d.isoformat()},{cfs}")
            d += timedelta(days=1)
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------- main

def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    repo, new = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    out = Path(sys.argv[3] if len(sys.argv) > 3 else "parallel/out").resolve()
    out.mkdir(parents=True, exist_ok=True)
    old_flow, old_norm = compile_cobol(repo, out / "build")
    cobc = sh(["cobc", "--version"])[1].split("\n")[0]
    dotnet = sh(["dotnet", "--version"])[1].strip()

    cases = []
    nights = production_nights(repo)
    for n in nights:
        folder = f"{n['run'][:10]}_{n['run'][11:19].replace(':', '')}_{n['commit']}"
        r = run_pair(out / "nights" / folder, n["files"], [str(old_flow)], [str(new), "run"],
                     ["streamflow-report.txt", "results.csv"], n["clock"])
        # Does the COBOL replay reproduce what production actually printed that night?
        replay = (out / "nights" / folder / "cobol" / "streamflow-report.txt").read_text()
        r.update(kind="night", name=n["run"][:10], commit=n["commit"], run=n["run"], why=f"production night, commit {n['commit']}, run {n['run']} UTC",
                 replay_matches_production=(replay == n["prod"]["streamflow-report.txt"]))
        cases.append(r)

    for name, c in edge_cases(nights[-1]).items():
        r = run_pair(out / "edge" / name, c["files"], [str(old_flow)], [str(new), "run"],
                     ["streamflow-report.txt", "results.csv"], c["clock"])
        r.update(kind="edge", name=name, why=c["why"])
        cases.append(r)

    hist = synthetic_history()
    r = run_pair(out / "normals" / "synthetic", {"history.csv": hist}, [str(old_norm)], [str(new), "normals"],
                 ["normals.csv"])
    r.update(kind="normals", name="synthetic-30yr", why=f"{hist.count(chr(10)) - 1:,} daily values, 3 gages, 1995-2025")
    cases.append(r)
    real = os.environ.get("HISTORY_CSV")          # CI sets this after fetching 30 years from USGS
    if real and Path(real).exists():
        text = Path(real).read_text()
        r = run_pair(out / "normals" / "usgs", {"history.csv": text}, [str(old_norm)], [str(new), "normals"],
                     ["normals.csv"])
        r.update(kind="normals", name="usgs-30yr", why=f"{text.count(chr(10)) - 1:,} real USGS daily means, water years 1996-2025")
        cases.append(r)
    r = run_pair(out / "normals" / "missing", {}, [str(old_norm)], [str(new), "normals"], ["normals.csv"])
    r.update(kind="normals", name="no-history", why="history.csv missing: RC 12 and an empty normals.csv")
    cases.append(r)

    summary = {
        "generated_utc": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S"),
        "legacy": {"repo": "bdgroves/sierra-flow-cobol", "head": sh(["git", "rev-parse", "--short", "HEAD"], cwd=repo)[1].strip(),
                   "compiler": cobc},
        "new": {"runtime": f".NET {dotnet}"},
        "cases": len(cases),
        "matched": sum(c["match"] for c in cases),
        "nights": len(nights),
        "lines_compared": sum(c["lines_compared"] for c in cases),
        "replays_match_production": sum(c.get("replay_matches_production", False) for c in cases),
        "results": [{k: v for k, v in c.items()} for c in cases],
    }
    (out / "summary.json").write_text(json.dumps(summary, indent=2))

    # A job log in the house style
    log = [f"JOB PARALLEL   {summary['generated_utc']} UTC",
           f"OLD            {cobc} · bdgroves/sierra-flow-cobol @ {summary['legacy']['head']}",
           f"NEW            sierraflow (C#) · .NET {dotnet}", ""]
    for c in cases:
        flag = "MATCH" if c["match"] else "DIFF "
        extra = ""
        if c["kind"] == "night":
            extra = "  replay=prod" if c["replay_matches_production"] else "  replay<>prod"
        log.append(f"CASE {c['kind']:<8} {c['name']:<16} RC={c['rc']:04d}  {flag}  {c['lines_compared']:>5} lines{extra}")
        for d in c["diffs"]:
            log += ["    " + x for x in d["diff"].splitlines()[:12]]
    log += ["", f"JOB PARALLEL   ENDED  {summary['matched']} OF {summary['cases']} CASES MATCH  "
                f"{summary['lines_compared']:,} LINES COMPARED"]
    (out / "parallel-log.txt").write_text("\n".join(log) + "\n")
    print("\n".join(log))
    sys.exit(0 if summary["matched"] == summary["cases"] else 8)


if __name__ == "__main__":
    main()
