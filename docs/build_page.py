#!/usr/bin/env python3
"""
build_page.py: bake the evidence in docs/data into docs/index.html.

    python3 docs/build_page.py

Reads the parallel run (summary.json), both reports, the sabotage test, the
USGS revisions, sites.csv and, once CI has run SQL Server, sql.json. Writes one
self-contained page: no fetches, so it works anywhere it is copied.
Standard library only.
"""
import csv
import json
import math
import sys
from pathlib import Path

DOCS = Path(__file__).resolve().parent
DATA = DOCS / "data"


def vincenty_km(lat1, lon1, lat2, lon2):
    """Distance on the WGS 84 ellipsoid (Vincenty's inverse formula), in km."""
    a, f = 6378137.0, 1 / 298.257223563
    b = a * (1 - f)
    L = math.radians(lon2 - lon1)
    U1, U2 = math.atan((1 - f) * math.tan(math.radians(lat1))), math.atan((1 - f) * math.tan(math.radians(lat2)))
    sinU1, cosU1, sinU2, cosU2 = math.sin(U1), math.cos(U1), math.sin(U2), math.cos(U2)
    lam = L
    for _ in range(200):
        sinl, cosl = math.sin(lam), math.cos(lam)
        sins = math.hypot(cosU2 * sinl, cosU1 * sinU2 - sinU1 * cosU2 * cosl)
        coss = sinU1 * sinU2 + cosU1 * cosU2 * cosl
        sig = math.atan2(sins, coss)
        sina = cosU1 * cosU2 * sinl / sins
        cos2a = 1 - sina ** 2
        cos2sm = coss - 2 * sinU1 * sinU2 / cos2a if cos2a else 0
        C = f / 16 * cos2a * (4 + f * (4 - 3 * cos2a))
        prev = lam
        lam = L + (1 - C) * f * sina * (sig + C * sins * (cos2sm + C * coss * (-1 + 2 * cos2sm ** 2)))
        if abs(lam - prev) < 1e-12:
            break
    u2 = cos2a * (a * a - b * b) / (b * b)
    A = 1 + u2 / 16384 * (4096 + u2 * (-768 + u2 * (320 - 175 * u2)))
    B = u2 / 1024 * (256 + u2 * (-128 + u2 * (74 - 47 * u2)))
    ds = B * sins * (cos2sm + B / 4 * (coss * (-1 + 2 * cos2sm ** 2) - B / 6 * cos2sm * (-3 + 4 * sins ** 2) * (-3 + 4 * cos2sm ** 2)))
    return b * A * (sig - ds) / 1000


def read(name, default=None):
    p = DATA / name
    if not p.exists():
        return default
    return json.loads(p.read_text()) if name.endswith(".json") else p.read_text()


def main():
    summary = read("summary.json")
    for r in summary["results"]:
        r.pop("diffs", None)
    sql = read("sql.json")

    sites = list(csv.DictReader((DATA / "sites.csv").open()))
    sites.sort(key=lambda s: (int(s["basin_seq"]), int(s["seq"])))
    gages = []
    for i, s in enumerate(sites):
        nxt = next((t for t in sites[i + 1:] if t["basin"] == s["basin"]), None)
        gages.append({
            "site_id": s["site_id"], "name": s["short_name"], "basin": s["basin"], "seq": int(s["seq"]),
            "regulation": s["regulation"], "lat": float(s["lat"]), "lon": float(s["lon"]),
            "next": nxt["short_name"] if nxt else None,
            "km_to_next": round(vincenty_km(float(s["lat"]), float(s["lon"]), float(nxt["lat"]), float(nxt["lon"])), 1) if nxt else None,
        })
    distance_source = "computed at build on the WGS 84 ellipsoid (Vincenty)"
    if sql and sql.get("gages"):
        by_id = {g["site_id"]: g for g in sql["gages"]}
        for g in gages:
            if g["site_id"] in by_id and by_id[g["site_id"]].get("km_to_next") is not None:
                g["km_to_next"] = by_id[g["site_id"]]["km_to_next"]
        distance_source = "SQL Server geography STDistance"

    sab = read("sabotage.json", {})
    for f in sab.get("failed", []):
        f["diff"] = f.get("diff", "")[:1500]

    data = {
        "summary": summary,
        "report": read("report-cobol.txt"),
        "report_csharp": read("report-csharp.txt"),
        "sabotage": {"cobol": read("sabotage-cobol.txt"), "csharp": read("sabotage-csharp.txt"), "summary": sab},
        "revisions": read("revisions.json"),
        "gages": gages,
        "distance_source": distance_source,
        "sql": sql,
    }
    blob = json.dumps(data, separators=(",", ":")).replace("</", "<\\/")
    tpl = (DOCS / "page.template.html").read_text()
    frag = tpl.replace("/*__DATA__*/null", blob)
    # docs/index.html: a full document for GitHub Pages
    cut = frag.index('<div class="wrap">')
    page = ('<!doctype html>\n<html lang="en">\n<head>\n<meta charset="utf-8">\n'
            '<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">\n'
            + frag[:cut] + '</head>\n<body>\n' + frag[cut:] + '\n</body>\n</html>\n')
    (DOCS / "index.html").write_text(page)
    # the same page as a fragment, for hosts that supply their own <html> skeleton
    if len(sys.argv) > 1:
        Path(sys.argv[1]).write_text(frag)
    print(f"docs/index.html written, {len(page):,} bytes")


if __name__ == "__main__":
    main()
