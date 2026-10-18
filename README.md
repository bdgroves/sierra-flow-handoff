```
╔══════════════════════════════════════════════════════════════════════════╗
║                                                                          ║
║   ████████╗██╗  ██╗███████╗    ██╗  ██╗ █████╗ ███╗   ██╗██████╗         ║
║   ╚══██╔══╝██║  ██║██╔════╝    ██║  ██║██╔══██╗████╗  ██║██╔══██╗        ║
║      ██║   ███████║█████╗      ███████║███████║██╔██╗ ██║██║  ██║        ║
║      ██║   ██╔══██║██╔══╝      ██╔══██║██╔══██║██║╚██╗██║██║  ██║        ║
║      ██║   ██║  ██║███████╗    ██║  ██║██║  ██║██║ ╚████║██████╔╝ OFF    ║
║      ╚═╝   ╚═╝  ╚═╝╚══════╝    ╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝╚═════╝        ║
║                                                                          ║
║   SIERRA-FLOW, HANDED FROM COBOL TO C#  ·  .NET 8  ·  SQL SERVER         ║
║   READ IT · WRITE IT DOWN · PORT IT · RUN BOTH UNTIL THEY AGREE          ║
║                                                                          ║
╚══════════════════════════════════════════════════════════════════════════╝
```

**[→ The live page: brooksgroves.com/sierra-flow-handoff](https://brooksgroves.com/sierra-flow-handoff/)** · **[→ The story](https://brooksgroves.com/blog/sierra-flow-handoff-post.html)** · **[→ The handoff notes](HANDOFF.md)**

[SIERRA-FLOW](https://github.com/bdgroves/sierra-flow-cobol) is a GnuCOBOL batch job that checks eight Sierra Nevada river gages against 30 years of normal flow every morning. This repo is its handoff to C#, done the way you'd take over any system someone is leaving behind:

1. **Read it** and write down what it really does: [HANDOFF.md](HANDOFF.md), with twelve findings nobody had written down.
2. **Port it** to C# on .NET 8, paragraph for paragraph, quirks included.
3. **Run both side by side** on every night the job has ever run, and require the same bytes out.
4. **Load it into SQL Server**, and have T-SQL recompute the answers a third time.

It is the fourth in a family with [SIERRA-FLOW](https://github.com/bdgroves/sierra-flow-cobol) (COBOL), [PUGET-TIDES](https://github.com/bdgroves/puget-tides) and [CASCADIA-WX](https://github.com/bdgroves/cascadia-wx) (FORTRAN).

---

## The parallel run

```
JOB PARALLEL   2026-10-18 20:59:29 UTC
OLD            cobc (GnuCOBOL) 3.1.2.0 · bdgroves/sierra-flow-cobol @ fae7ab1
NEW            sierraflow (C#) · .NET 8.0.131

CASE night    2026-10-03       RC=0000  MATCH     76 lines  replay=prod
  ... 17 production nights ...
CASE edge     flood            RC=0000  MATCH     76 lines
CASE edge     garbage-values   RC=0000  MATCH     76 lines
CASE edge     too-many-days    RC=0004  MATCH     77 lines
  ... 10 edge cases ...
CASE normals  synthetic-30yr   RC=0000  MATCH   1105 lines

JOB PARALLEL   ENDED  29 OF 29 CASES MATCH  3,176 LINES COMPARED
```

`parallel/parallel_run.py` pulls each production night out of the COBOL repo's git history and runs both programs on it, with both clocks pinned to the second that night ran (`COB_CURRENT_DATE`, which GnuCOBOL reads and the C# honours too). It compares the report, `results.csv`, every console message and the return code, and checks that the COBOL replay reproduces what production really printed. The edge cases bend the latest night on purpose, one per finding. Full log: [docs/data/parallel-log.txt](docs/data/parallel-log.txt).

**The sabotage test.** Switching the port to C#'s default rounding (half to even) fails 3 of 29 cases: two real nights (Oct 4: 11.81 vs 11.80 cfs; Oct 15: 61.3% vs 61.2%) and 374 of 1,098 normals rows. Evidence: [docs/data/sabotage.json](docs/data/sabotage.json).

## Layout

```
src/SierraFlow/         the C# job: no NuGet packages, .NET 8 only
  FlowJob.cs              SIERRA-FLOW.cob, method per paragraph
  NormalsJob.cs           NORMALS.cob
  Cobol.cs                PIC truncation, ROUNDED, edit pictures, NUMVAL, UNSTRING
  Clock.cs                CURRENT-DATE, honouring COB_CURRENT_DATE
  SqlExport.cs            writes load.sql for SQL Server / Azure SQL
parallel/
  parallel_run.py         COBOL vs C#, every night + edge cases + NORMALS
  revisions.py            values USGS changed after first publishing them
sql/
  01_schema.sql           gage (geography + spatial index), daily_flow, flow_revision, run_result
  02_agreement.sql        T-SQL recomputes each night; THROW on any difference
  03_checks.sql           agreement summary, gage distances, gaps, revisions
  04_export.sql           one JSON document for the page
docs/                   the page (GitHub Pages) and the evidence it shows
blog/                   the blog post, for brooksgroves.com/blog
HANDOFF.md              the knowledge-transfer notes
```

## Run it

```bash
# the C# job, on the same three files as the COBOL
dotnet build src/SierraFlow -c Release -o bin
cd /path/to/sierra-flow-cobol && /path/to/bin/sierraflow run      # streamflow-report.txt, results.csv
/path/to/bin/sierraflow sql .                                      # load.sql

# the parallel run (needs GnuCOBOL: sudo apt install gnucobol3)
git clone https://github.com/bdgroves/sierra-flow-cobol legacy
python3 parallel/parallel_run.py legacy bin/sierraflow parallel/out
```

On Windows: `winget install Microsoft.DotNet.SDK.8`, then the same `dotnet build`. The parallel run needs GnuCOBOL, so use WSL or let CI do it.

## Nightly CI

[`.github/workflows/handoff.yml`](.github/workflows/handoff.yml) runs at 15:43 UTC, after the COBOL job: build, parallel run, start SQL Server 2022 in a container, load every night in order with `02_agreement.sql` after each, run the checks, rebuild the page, commit the evidence. Sundays it also fetches 30 years of USGS history and compares NORMALS on the real thing. Any difference anywhere fails the run.

## Phase two

Once the parallel run has passed long enough to trust, fix the findings one at a time, each with its own expected difference: validate flows in the job (finding 1), widen Section I (2), sort by date and keep the newest 70 days (3, 4), reject future dates (5), drop the fixed tables (7, 8), make 30 days mean 30 calendar days (9). See [HANDOFF.md §7](HANDOFF.md#7-phase-two).

---

Data: [USGS Water Data API](https://api.waterdata.usgs.gov/), free and public; recent values are [provisional](https://waterdata.usgs.gov/provisional-data-statement/). Code by Brooks Groves, with Claude as a second reader of the COBOL.
