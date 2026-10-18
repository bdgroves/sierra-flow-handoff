# SIERRA-FLOW: handoff notes

Everything someone taking over [SIERRA-FLOW](https://github.com/bdgroves/sierra-flow-cobol) needs to know: what the job does, how it is run, the rules it applies, and what it does that nobody wrote down.

I wrote these notes from the COBOL source and from running it, before porting a line of it to C#. Each finding below has a parallel-run case that shows it happening (see `parallel/parallel_run.py`). The C# port reproduces every one of them on purpose, because a port that quietly "fixes" things while it is being proved can never be compared with the original. Fixing them is phase two.

---

## 1. What the job is for

Each morning it answers one question for eight USGS river gages on the Tuolumne, Merced and Stanislaus: **is this river running normal for this date?**

"Normal" means the 10th to 90th percentiles of daily flow for that date over water years 1996–2025, with each date's normal taken from a 7-day window across those 30 years. The classes are USGS WaterWatch's:

| Percentile of today's flow | Class |
|---|---|
| below 10th | MUCH BELOW NORMAL |
| 10th–24th | BELOW NORMAL |
| 25th–75th | NORMAL |
| 76th–90th | ABOVE NORMAL |
| above 90th | MUCH ABOVE NORMAL |
| fewer than 10 years of record for the date | NO NORMAL |

## 2. How it runs

```
GitHub Actions, daily 14:17 UTC (bdgroves/sierra-flow-cobol, .github/workflows/sierra-flow-cobol.yml)
  run_job.sh
    STEP FETCH     fetch_usgs.py        USGS Water Data API -> streamflow.csv (last 60 complete days)
    STEP COMPILE   cobc -x              SIERRA-FLOW.cob -> ./sierra-flow
    STEP HISTORY   fetch_usgs.py --history   only when normals.csv is missing or a rebuild is asked for
    STEP NORMALS   ./normals            history.csv -> normals.csv (30 years, ~584,000 sorted records)
    STEP SIERRAFL  ./sierra-flow        -> streamflow-report.txt, results.csv
  commit the outputs back to the repo
```

**Files**

| File | Written by | Read by | What it is |
|---|---|---|---|
| `sites.csv` | a person | both programs, fetcher | the 8 gages: id, short name, basin, basin order, order down the river, regulation, lat/lon |
| `history.csv` | fetcher `--history` | NORMALS | 30 years of daily means; deleted after NORMALS runs |
| `normals.csv` | NORMALS | SIERRA-FLOW | p10–p90 for every gage and every day of the year (8 × 366 rows) |
| `streamflow.csv` | fetcher | SIERRA-FLOW | the last 60 daily means per gage |
| `streamflow-report.txt` | SIERRA-FLOW | people | the 132-column report |
| `results.csv` | SIERRA-FLOW | the web page | the same numbers, one line per gage |
| `job-log.txt` | run_job.sh | people | each step's output, return code and time |

**Return codes**

| RC | Meaning | What to do |
|---|---|---|
| 0 | every gage reported in the last 3 days | nothing |
| 4 | a gage is stale (latest day more than 3 days old) or sent nothing | look at the gage on waterdata.usgs.gov; USGS outages usually clear in a day. The fetcher keeps the last good rows, so the report still prints, with `*` next to the stale date |
| 12 | an input file could not be opened | read the job log; usually `normals.csv` is missing: run the workflow by hand with *rebuild normals* ticked |
| 4 from NORMALS | some day of the year has under 10 years of record | expected for a gage with a short history (Grand Canyon starts in 2006); those days read NO NORMAL |

The job stops at the first step with RC 8 or more, like a mainframe step with `COND=(8,LE)`.

## 3. The rules it applies (SIERRA-FLOW.cob)

| Paragraph | Rule |
|---|---|
| `2100-ONE-SITE` | up to 12 gages; the first line of every file is a header and is skipped |
| `3100-ONE-FLOW` | a row is skipped if its gage is unknown, its flow is blank, or its date is not `YYYY-...`; up to 70 days per gage |
| `3100-ONE-FLOW` | day of year on a **leap-year calendar**: Feb 29 is day 60 in every year, so Mar 1 is always day 61 and every date lines up with the same normal |
| `4000-ANALYZE-GAGE` | **latest day** = the last row read for the gage. **Stale** = more than 3 days before the run date |
| `4000` | **% of median** only when the date has ≥ 10 years of normals and a median above zero |
| `4000` | **30 days** = the last 30 rows. **% normal** = total flow / total of the daily medians, only when every one of those days has a normal |
| `4000` | **trend** = mean of the last 7 rows vs the 7 before, each mean rounded to cents first. ±10% or more is RISING or FALLING; both means under 1 cfs is STEADY |
| `4100-CLASSIFY` | the class table above. Below p10 and below p25 are tested before above p90 and above p75 |
| `5000-SORT-GAGES` | basin order, then down the river. The sort carries only the table index, so each gage keeps its own numbers (the V2 bug, fixed in V3) |
| `5500-BUILD-BASINS` | a basin reports its **lowest gage**, never a sum: gages on one river measure the same water (another V2 bug) |
| `NORMALS 2300` | percentile at Weibull rank p × (n + 1), interpolated between neighbours, rounded to cents |

**Numbers.** Every flow is `PIC 9(7)V99`: unsigned, two decimals. Two COBOL habits matter for anyone porting it:

- `MOVE` and plain `COMPUTE` **truncate**. `COMPUTE ... ROUNDED` rounds **half away from zero**. C#'s `Math.Round` defaults to half-to-even, which turns 52.25 into 52.2 where the COBOL prints 52.3; the port uses `decimal` and `MidpointRounding.AwayFromZero` everywhere.
- Moving a number into a smaller picture drops digits **from the left**, without an error (finding 2).

## 4. Findings: what it does that nobody wrote down

None of these has caused a wrong number in production yet. Each is a trap for whoever changes the job next.

| # | Finding | Shown by | Why it hasn't bitten |
|---|---|---|---|
| 1 | **The validation lives in the fetcher, not the job.** `NUMVAL` turns `Ice` into 0.00 cfs, and an unsigned field turns −12.50 into 12.50. Neither is rejected. | `garbage-values` | `fetch_usgs.py` drops non-numbers and negatives before the COBOL sees them. Replace the fetcher and that protection goes with it. |
| 2 | **Section I prints at most 99,999.99 cfs.** A 468,000 cfs flood prints as `68000.00`; Sections II and III, with wider fields, print it correctly. | `flood` | The highest 90th percentile in the normals is 8,544 cfs (Modesto, late April), a long way under 100,000. |
| 3 | **Over 70 days, it keeps the oldest 70 and drops the newest.** A gage with current data is then reported stale. | `too-many-days` | The fetcher asks for 60 days. Set `DAYS_BACK` above 70 and every gage goes stale. |
| 4 | **File order is trusted.** "Latest day" is the last row read. Rows out of date order give a wrong latest day, 30-day mean and trend, with no warning. | `out-of-order` | The fetcher sorts by date. |
| 5 | **A date in the future is accepted.** `R-AGE` is unsigned, so a date 5 days ahead counts as 5 days old: the gage is flagged stale, for the wrong reason, and the future value is classified as today's. | `future-date` | USGS doesn't publish tomorrow. |
| 6 | **The trend's overflow value is 9,999.9%, not 99,999.9%.** A rise bigger than the field can hold prints a smaller number than a real 50,000% rise would. | `huge-rise` | It takes a creek going from nearly dry to running within a week. |
| 7 | **Fixed table sizes, mostly unchecked.** 12 gages (a 13th is ignored without a word), 70 days, 5 basins (a 6th writes past the table: COBOL does not check bounds), 9 gages per basin (`PIC 9` wraps to 0). | `thirteen-gages` | Eight gages, three basins. |
| 8 | **Gages are matched on the first 8 characters of the id.** USGS site ids can be 15 digits; two such gages would be the same gage to this job. | (reading) | All eight ids are 8 digits. |
| 9 | **"Last 30 days" is the last 30 rows.** With a missing day it reaches back 31 calendar days, and the % normal compares against 30 medians that aren't quite the last 30 dates. | (reading) | USGS daily means rarely have gaps. |
| 10 | **NORMALS counts calendar years, not water years.** The six days around New Year read 31 years from a 30-year record, because the 7-day window takes in a December from one water year and a January from the next. | `normals.csv` (84 rows) | Harmless: the threshold is 10. Worth knowing before someone "fixes" the count. |
| 11 | **Equal sort keys have no promised order.** Two gages with the same basin and position could print in either order. | (reading) | The keys are unique. The SQL schema now enforces it (`uq_gage_order`). |
| 12 | **NORMALS keeps at most 400 values per day-of-year**, silently. | (reading) | 30 years × 7 days is 210. |

## 5. COBOL → C#

The port lives in `src/SierraFlow`. Every method names the paragraph it replaces.

| COBOL | C# | Note |
|---|---|---|
| `PIC 9(7)V99` | `decimal` | never `double`: binary floating point can't hold 0.1 exactly |
| `MOVE` to a numeric picture | `Cobol.Pic(v, int, dec)` | truncates; unsigned keeps the absolute value |
| `COMPUTE ... ROUNDED` | `Cobol.Rounded(v, dec)` | half away from zero |
| `ON SIZE ERROR` | `Cobol.SizeError(v, int)` | |
| `Z(4)9.99`, `-(5)9.9` | `Cobol.Edit`, `Cobol.EditFloatingMinus` | the report's columns |
| `UNSTRING ... DELIMITED BY ','` | `Cobol.Unstring` | |
| `FUNCTION NUMVAL` | `Cobol.NumVal` | non-numbers become 0, as in COBOL |
| `FUNCTION CURRENT-DATE` | `Clock` | honours `COB_CURRENT_DATE`, so both programs can be pinned to one second |
| `SORT ... INPUT/OUTPUT PROCEDURE` | `OrderBy(...).ThenBy(...)` | stable, where COBOL makes no promise (finding 11) |
| NORMALS' 584,000-record `SORT` | a dictionary of lists, each sorted | same groups, same order of values |
| `OCCURS 12` / `70` / `5` | `MaxGages`, `MaxDays`, `MaxBasins` | the 6th basin stops with `SFLW906E` instead of overwriting memory: the one place the port refuses to copy the old behaviour, because the old behaviour is undefined |
| `RETURN-CODE` | `Main`'s return value | 0, 4, 12 |

## 6. Proving the port: the parallel run

`parallel/parallel_run.py` runs the old and new jobs side by side and requires the same bytes out:

- **Every production night since V3**, replayed from the COBOL repo's git history, with both clocks pinned to the second that night ran. Each replay of the COBOL is also checked against the report production actually printed that night, which proves the replay itself is faithful.
- **Edge cases**: the latest night bent on purpose, one case per finding above.
- **NORMALS** on a synthetic 30-year history with gaps, ties, zeros, a blank and a gage that starts in 2006; CI also rebuilds the real normals from USGS and compares.
- **The database**: `sql/03_checks.sql` re-derives the 30-day mean, min, max and trend in T-SQL and throws if they differ from the batch job. Three independent implementations, one answer.

It compares the report, `results.csv` or `normals.csv`, every console message and the return code.

## 7. Phase two

Once the parallel run has passed every night for long enough to trust, the C# job becomes the system of record and the findings can be fixed, one at a time, each as its own change with its own expected difference in the parallel run:

1. Reject non-numeric and negative flows in the job, not just the fetcher (finding 1)
2. Widen Section I's flow column (2)
3. Sort each gage's days by date and keep the newest 70 (3, 4)
4. Reject dates after the run date (5)
5. Use real limits or none: lists instead of fixed tables (7), full-length site ids (8)
6. Make "30 days" mean 30 calendar days (9)
7. Load straight to SQL Server / Azure SQL and let the web page read from there

---

*Notes by Brooks Groves, October 2026, with Claude as a second reader of the COBOL. Every finding was checked by running both programs, not taken from the reading alone.*
