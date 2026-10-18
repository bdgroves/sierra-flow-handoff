using System.Text;
using static SierraFlow.Cobol;

namespace SierraFlow;

/// <summary>
/// SIERRA-FLOW.cob V3.0, paragraph for paragraph. Each method names the COBOL
/// paragraph it replaces, so the two programs can be read side by side.
/// The output (report, results.csv, console messages, return code) must match
/// the COBOL byte for byte; the parallel run checks that every night.
/// </summary>
public sealed class FlowJob
{
    // --- limits written into the COBOL tables (OCCURS) ---
    public const int MaxGages = 12;      // WS-SITE OCCURS 12
    public const int MaxDays = 70;       // S-DAY OCCURS 70
    public const int MaxBasins = 5;      // WS-BASIN OCCURS 5

    // Days before each month on a leap-year calendar, so Feb 29 is day 60 (WS-CUM-TABLE)
    static readonly int[] Cum = { 0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335 };

    sealed class Day { public string Date = ""; public int DayNum; public int Slot; public decimal Flow; }
    sealed class Normal { public int Years; public decimal P10, P25, P50, P75, P90; }

    sealed class Gage
    {
        public string Id = "", Name = "", Basin = "", Reg = "";
        public int BasinSeq, Seq;
        public readonly List<Day> Days = new();
        public readonly Normal[] Norm = Enumerable.Range(0, 367).Select(_ => new Normal()).ToArray();
        // results (R- fields)
        public string LastDate = "", Class = "", Trend = "";
        public int LastSlot, N30, LowDays, HighDays, Age;
        public decimal LastFlow, PctMed, Mean30, Min30, Max30, Pct30, TrendPct;
        public bool HasMed, HasPct30, HasTrend, Stale;
    }

    sealed class Basin { public string Name = ""; public int Seq, Gages, Outlet, OutletSeq; public readonly int[] ClassN = new int[6]; }

    readonly string dir;
    readonly TextWriter sysout;
    readonly Clock clock = Clock.Now();
    readonly List<Gage> gages = new();
    readonly List<Basin> basins = new();
    List<Gage> order = new();
    int rc, normsRead, flowsRead, skipped, stale;

    public FlowJob(string dir, TextWriter sysout) { this.dir = dir; this.sysout = sysout; }

    string PathOf(string file) => Path.Combine(dir, file);

    /// <summary>0000-MAIN. Returns the job's return code: 0, 4 (stale or missing gage) or 12 (file error).</summary>
    public int Run()
    {
        Initialize();
        if (!LoadSites()) return 12;
        if (!LoadNormals()) return 12;
        if (!LoadFlows()) return 12;
        foreach (var g in gages) AnalyzeGage(g);
        SortGages();
        if (!BuildBasins()) return 12;
        if (!WriteReport()) return 12;
        if (!WriteResults()) return 12;
        return Terminate();
    }

    // 1000-INITIALIZE
    void Initialize() =>
        sysout.WriteLine($"SFLW000I SIERRA-FLOW V3.0 STARTED {clock.Date} {clock.Time} {clock.Zone}");

    // 2000-LOAD-SITES / 2100-ONE-SITE
    bool LoadSites()
    {
        if (!File.Exists(PathOf("sites.csv")))
        {
            sysout.WriteLine("SFLW901E CANNOT OPEN sites.csv");
            return false;
        }
        bool first = true;
        foreach (var rec in ReadRecords(PathOf("sites.csv"), 200))
        {
            if (first) { first = false; continue; }      // header
            if (gages.Count >= MaxGages) continue;         // a 13th gage is ignored, silently
            var f = Unstring(rec, 10, 60);
            gages.Add(new Gage
            {
                Id = X(f[0], 8),
                Name = X(f[1], 26),
                Basin = X(f[2], 12),
                BasinSeq = (int)Pic(NumVal(f[3]), 1, 0),
                Seq = (int)Pic(NumVal(f[4]), 2, 0),
                Reg = X(f[6], 10),
            });
        }
        sysout.WriteLine($"SFLW001I GAGES LOADED ..............{Edit(gages.Count, 5, 0)}");
        return true;
    }

    // 8100-FIND-SITE: compares only the first 8 characters of the id
    Gage? FindSite(string field) => gages.FirstOrDefault(g => g.Id == X(field, 8));

    // 2500-LOAD-NORMALS / 2600-ONE-NORMAL
    bool LoadNormals()
    {
        if (!File.Exists(PathOf("normals.csv")))
        {
            sysout.WriteLine("SFLW902E CANNOT OPEN normals.csv");
            sysout.WriteLine("SFLW902E RUN NORMALS FIRST");
            return false;
        }
        bool first = true;
        foreach (var rec in ReadRecords(PathOf("normals.csv"), 120))
        {
            if (first) { first = false; continue; }
            var f = Unstring(rec, 11, 60);
            var g = FindSite(f[0]);
            if (g is null) continue;
            int slot = (int)Pic(NumVal(f[1]), 3, 0);
            if (slot < 1 || slot > 366) continue;
            var n = g.Norm[slot];
            n.Years = (int)Pic(NumVal(f[5]), 2, 0);
            n.P10 = Pic(NumVal(f[6]), 7, 2);
            n.P25 = Pic(NumVal(f[7]), 7, 2);
            n.P50 = Pic(NumVal(f[8]), 7, 2);
            n.P75 = Pic(NumVal(f[9]), 7, 2);
            n.P90 = Pic(NumVal(f[10]), 7, 2);
            normsRead++;
        }
        sysout.WriteLine($"SFLW002I DAILY NORMALS LOADED ......{Edit(normsRead, 5, 0)}");
        return true;
    }

    // 3000-LOAD-FLOWS / 3100-ONE-FLOW
    bool LoadFlows()
    {
        if (!File.Exists(PathOf("streamflow.csv")))
        {
            sysout.WriteLine("SFLW903E CANNOT OPEN streamflow.csv");
            return false;
        }
        bool first = true;
        foreach (var rec in ReadRecords(PathOf("streamflow.csv"), 120))
        {
            if (first) { first = false; continue; }
            var f = Unstring(rec, 4, 60);
            var g = FindSite(f[0]);
            if (g is null || f[2].Trim().Length == 0 || f[1][4] != '-')
            {
                skipped++;
                continue;
            }
            if (g.Days.Count >= MaxDays) { skipped++; continue; }
            string date = f[1][..10];
            int y = int.Parse(date[..4]), m = int.Parse(date.Substring(5, 2)), d = int.Parse(date.Substring(8, 2));
            g.Days.Add(new Day
            {
                Date = date,
                DayNum = new DateOnly(y, m, d).DayNumber,
                Slot = Cum[m - 1] + d,
                Flow = Pic(NumVal(f[2]), 7, 2),
            });
            flowsRead++;
        }
        sysout.WriteLine($"SFLW003I DAILY MEANS LOADED ........{Edit(flowsRead, 5, 0)}");
        sysout.WriteLine($"SFLW004I RECORDS SKIPPED ...........{Edit(skipped, 5, 0)}");
        return true;
    }

    // 4000-ANALYZE-GAGE: today vs normal, 30 days vs normal, 7-day trend
    void AnalyzeGage(Gage g)
    {
        if (g.Days.Count == 0)
        {
            g.Class = "NO DATA";
            g.Stale = true;
            stale++;
            rc = 4;
            sysout.WriteLine($"SFLW010W NO DATA FOR {g.Id}");
            return;
        }
        int last = g.Days.Count;                 // 1-based, as in the COBOL
        Day Dy(int i) => g.Days[i - 1];
        g.LastDate = Dy(last).Date;
        g.LastSlot = Dy(last).Slot;
        g.LastFlow = Dy(last).Flow;

        // How old is the latest day? R-AGE is unsigned: a date in the future reads as positive.
        g.Age = (int)Pic(clock.DayNumber - Dy(last).DayNum, 4, 0);
        if (g.Age > 3)
        {
            g.Stale = true;
            stale++;
            rc = 4;
            sysout.WriteLine($"SFLW011W STALE DATA FOR {g.Id} LAST DAY {g.LastDate}");
        }

        // Latest day against the normal for its date
        var nl = g.Norm[g.LastSlot];
        g.Class = Classify(g.LastFlow, nl).Name;
        if (nl.Years >= 10 && nl.P50 > 0)
        {
            var v = Rounded(g.LastFlow / nl.P50 * 100m, 1);
            g.PctMed = SizeError(v, 5) ? 99999.9m : v;
            g.HasMed = true;
        }

        // Last 30 days: the last 30 records, which is 30 calendar days only when none are missing
        int from = last > 30 ? last - 29 : 1;
        decimal sum = 0, sumMed = 0;
        int medDays = 0;
        g.Min30 = 9999999.99m;
        for (int i = from; i <= last; i++)
        {
            var dy = Dy(i);
            var n = g.Norm[dy.Slot];
            g.N30++;
            sum += dy.Flow;
            if (dy.Flow < g.Min30) g.Min30 = dy.Flow;
            if (dy.Flow > g.Max30) g.Max30 = dy.Flow;
            if (n.Years >= 10)
            {
                sumMed += n.P50;
                medDays++;
                if (dy.Flow < n.P10) g.LowDays++;
                if (dy.Flow > n.P90) g.HighDays++;
            }
        }
        g.Mean30 = Pic(Rounded(sum / g.N30, 2), 7, 2);
        if (medDays == g.N30 && sumMed > 0)
        {
            var v = Rounded(sum / sumMed * 100m, 1);
            g.Pct30 = SizeError(v, 5) ? 99999.9m : v;
            g.HasPct30 = true;
        }

        // Last 7 days against the 7 before
        g.Trend = "STEADY";
        if (last >= 14)
        {
            decimal a = 0, b = 0;
            for (int i = last - 6; i <= last; i++) a += Dy(i).Flow;
            for (int i = last - 13; i <= last - 7; i++) b += Dy(i).Flow;
            decimal meanA = Pic(Rounded(a / 7m, 2), 7, 2);   // means are rounded to cents first
            decimal meanB = Pic(Rounded(b / 7m, 2), 7, 2);
            g.HasTrend = true;
            if (meanB > 0)
            {
                decimal change = meanA - meanB;
                var v = Rounded(change / meanB * 100m, 1);
                g.TrendPct = SizeError(v, 5) ? 9999.9m : v;  // the COBOL writes 9999.9, not 99999.9
            }
            g.Trend =
                meanA < 1 && meanB < 1 ? "STEADY" :
                meanB == 0 ? "RISING" :
                g.TrendPct >= 10 ? "RISING" :
                g.TrendPct <= -10 ? "FALLING" : "STEADY";
        }
    }

    // 4100-CLASSIFY: USGS WaterWatch classes
    static (string Name, int Index) Classify(decimal flow, Normal n) =>
        n.Years < 10 ? ("NO NORMAL", 0) :
        flow < n.P10 ? ("MUCH BELOW NORMAL", 1) :
        flow < n.P25 ? ("BELOW NORMAL", 2) :
        flow > n.P90 ? ("MUCH ABOVE NORMAL", 5) :
        flow > n.P75 ? ("ABOVE NORMAL", 4) :
                       ("NORMAL", 3);

    // 5000-SORT-GAGES: by basin, then upstream to downstream
    void SortGages() =>
        order = gages.OrderBy(g => g.BasinSeq).ThenBy(g => g.Seq).ToList();

    // 5500-BUILD-BASINS: gage count, class counts, and the lowest gage
    bool BuildBasins()
    {
        foreach (var g in order)
        {
            var b = basins.FirstOrDefault(x => x.Name == g.Basin);
            if (b is null)
            {
                if (basins.Count >= MaxBasins)
                {
                    // The COBOL table holds 5 basins and does not check; a 6th would
                    // overwrite memory. The port stops instead (see HANDOFF.md, finding 9).
                    sysout.WriteLine($"SFLW906E MORE THAN {MaxBasins} BASINS: {Trim(g.Basin)}");
                    return false;
                }
                b = new Basin { Name = g.Basin, Seq = g.BasinSeq };
                basins.Add(b);
            }
            b.Gages = (b.Gages + 1) % 10;                  // B-GAGES is PIC 9
            if (g.Seq >= b.OutletSeq)
            {
                b.OutletSeq = g.Seq;
                b.Outlet = gages.IndexOf(g);
            }
            if (g.Days.Count > 0)
            {
                var (_, idx) = Classify(g.LastFlow, g.Norm[g.LastSlot]);
                if (idx > 0) b.ClassN[idx] = (b.ClassN[idx] + 1) % 10;   // PIC 9
            }
        }
        return true;
    }

    // ------------------------------------------------------------------
    // 6000-WRITE-REPORT: the 132-column printed report
    // ------------------------------------------------------------------
    static readonly string RuleEq = new('=', 132);
    static readonly string RuleDash = new('-', 132);

    static string At(int col, string text)      // MOVE text TO WS-LINE(col:len)
    {
        var sb = new StringBuilder(new string(' ', col - 1));
        sb.Append(text);
        return sb.ToString();
    }

    bool WriteReport()
    {
        var lines = new List<string>();
        void W(string s) => lines.Add(LineOut(X(s, 132)));

        // 6100-BANNER
        W(RuleEq); W("");
        W(At(47, "SIERRA NEVADA WATERSHED ANALYSIS SYSTEM"));
        W(At(45, "USGS STREAMFLOW DATA PROCESSING REPORT  V3.0"));
        W(At(51, X($"RUN {clock.Date} {clock.Time} {clock.Zone}", 40)));
        W(""); W(RuleEq); W("");

        // 6200-SECTION-I
        W("SECTION I: LATEST DAILY MEAN VS NORMAL FOR THE DATE");
        W("  NORMAL = PERCENTILES OF DAILY FLOW FOR THE DATE, WATER YEARS 1996-2025, 7-DAY WINDOW. CLASSES AS USGS WATERWATCH.");
        W("");
        W(X("  SITE ID", 11) + X("STATION", 27) + X("DATE", 11) + "      CFS" + "      P10" + "      P25"
          + "   MEDIAN" + "      P75" + "      P90" + "    % MED" + X("  CLASS", 20));
        W(RuleDash);
        foreach (var g in order)
        {
            string date = "", flag = " ", cfs = "", p10 = "", p25 = "", p50 = "", p75 = "", p90 = "", pct = "", cls;
            if (g.Days.Count == 0)
                cls = "NO DATA";
            else
            {
                var n = g.Norm[g.LastSlot];
                date = g.LastDate;
                cfs = Edit(g.LastFlow, 4, 2);
                p10 = Edit(n.P10, 4, 2); p25 = Edit(n.P25, 4, 2); p50 = Edit(n.P50, 4, 2);
                p75 = Edit(n.P75, 4, 2); p90 = Edit(n.P90, 4, 2);
                pct = g.HasMed ? Edit(g.PctMed, 4, 1) + "%" : "     --";
                cls = g.Class;
                if (g.Stale) flag = "*";
            }
            W("  " + X(g.Id, 8) + " " + X(g.Name, 26) + " " + X(date, 10) + flag + " "
              + X(cfs, 8) + " " + X(p10, 8) + " " + X(p25, 8) + " " + X(p50, 8) + " "
              + X(p75, 8) + " " + X(p90, 8) + " " + X(pct, 8) + "  " + X(cls, 17));
        }
        W("");

        // 6300-SECTION-II
        W("SECTION II: LAST 30 DAYS VS NORMAL FOR THOSE DATES");
        W("  % NORMAL = 30-DAY TOTAL FLOW / TOTAL OF THE DAILY MEDIANS. TREND = LAST 7 DAYS VS THE 7 BEFORE, +/-10% OR MORE.");
        W("");
        W(X("  SITE ID", 12) + X("STATION", 27) + X("DAYS", 6) + "  MEAN CFS" + " " + "   MIN CFS" + " "
          + "   MAX CFS" + " " + " % NORMAL" + " DAYS<P10" + " DAYS>P90" + X("  7-DAY TREND", 20));
        W(RuleDash);
        foreach (var g in order.Where(g => g.Days.Count > 0))
        {
            string pct = g.HasPct30 ? " " + Edit(g.Pct30, 4, 1) + "%" : "      --";
            string tpct = g.HasTrend && g.Trend != "STEADY" ? EditFloatingMinus(g.TrendPct, 5, 1) + "%" : "";
            W("  " + X(g.Id, 8) + "  " + X(g.Name, 26) + " " + Edit(g.N30, 3, 0) + "  "
              + Edit(g.Mean30, 6, 2) + " " + Edit(g.Min30, 6, 2) + " " + Edit(g.Max30, 6, 2) + " "
              + X(pct, 9) + new string(' ', 7) + Edit(g.LowDays, 1, 0) + new string(' ', 7)
              + Edit(g.HighDays, 1, 0) + "  " + X(g.Trend, 7) + " " + X(tpct, 9));
        }
        W("");

        // 6400-SECTION-III
        W("SECTION III: WATERSHED BASINS");
        W("  GAGES ON ONE RIVER MEASURE THE SAME WATER, SO THEY ARE NOT ADDED. THE LOWEST GAGE IS WHAT LEAVES THE BASIN.");
        W("");
        W(X("  BASIN", 14) + X("GAGES", 8) + X("LOWEST GAGE (BASIN OUTFLOW)", 28) + "      CFS" + X("  CLASS", 19)
          + " MUCH BELOW" + "  BELOW" + " NORMAL" + "  ABOVE" + " MUCH ABOVE");
        W(RuleDash);
        foreach (var b in basins)
        {
            var o = gages[b.Outlet];
            W("  " + X(b.Name, 12) + " " + Edit(b.Gages, 1, 0) + new string(' ', 5) + X(o.Name, 26) + "  "
              + Edit(o.LastFlow, 5, 2) + "  " + X(o.Class, 17) + new string(' ', 9)
              + Edit(b.ClassN[1], 1, 0) + new string(' ', 5) + Edit(b.ClassN[2], 1, 0) + new string(' ', 5)
              + Edit(b.ClassN[3], 1, 0) + new string(' ', 5) + Edit(b.ClassN[4], 1, 0) + new string(' ', 9)
              + Edit(b.ClassN[5], 1, 0));
        }
        W("");

        // 6500-SECTION-IV
        W("SECTION IV: RUN SUMMARY");
        W("");
        void Sum(string label, int value) => W("    " + X(label, 34) + Edit(value, 5, 0));
        Sum("GAGES PROCESSED ...................", gages.Count);
        Sum("DAILY NORMALS READ ................", normsRead);
        Sum("DAILY MEANS READ ..................", flowsRead);
        Sum("RECORDS SKIPPED (INVALID) .........", skipped);
        Sum("GAGES STALE OR WITHOUT DATA .......", stale);
        Sum("WATERSHED BASINS ..................", basins.Count);
        Sum("RETURN CODE .......................", rc);
        W("");

        W(RuleEq);
        W(At(51, "END OF REPORT - SIERRA-FLOW V3.0"));
        W(RuleEq);

        try { File.WriteAllText(PathOf("streamflow-report.txt"), string.Join("\n", lines) + "\n"); }
        catch (IOException)
        {
            sysout.WriteLine("SFLW904E CANNOT OPEN streamflow-report.txt");
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------
    // 7000-WRITE-RESULTS: the same numbers, one line per gage, for the web
    // ------------------------------------------------------------------
    bool WriteResults()
    {
        var sb = new StringBuilder();
        sb.Append("site_id,basin,seq,last_date,last_cfs,n_years,p10,p25,p50,p75,p90,class,pct_median,days_30,"
                + "mean_30,min_30,max_30,pct_normal_30,days_below_p10,days_above_p90,trend,trend_pct,stale,"
                + "run_date,run_time,rc\n");
        string run = $"{clock.Date},{clock.Time},{Trim(Edit(rc, 1, 0))}";
        foreach (var g in order)
        {
            var r = new StringBuilder();
            r.Append($"{Trim(g.Id)},{Trim(g.Basin)},{Trim(Edit(g.Seq, 1, 0))},{Trim(g.LastDate)},");
            if (g.Days.Count == 0)
            {
                r.Append(",,,,,,,NO DATA,,,,,,,,,,,Y,").Append(run);
                sb.Append(LineOut(r.ToString())).Append('\n');
                continue;
            }
            var n = g.Norm[g.LastSlot];
            string Cfs(decimal v) => Trim(Edit(v, 6, 2)) + ",";
            r.Append(Cfs(g.LastFlow));
            r.Append(Trim(Edit(n.Years, 1, 0))).Append(',');
            r.Append(Cfs(n.P10)).Append(Cfs(n.P25)).Append(Cfs(n.P50)).Append(Cfs(n.P75)).Append(Cfs(n.P90));
            r.Append(Trim(g.Class)).Append(',');
            if (g.HasMed) r.Append(Trim(Edit(g.PctMed, 4, 1)));
            r.Append(',').Append(Trim(Edit(g.N30, 1, 0))).Append(',');
            r.Append(Cfs(g.Mean30)).Append(Cfs(g.Min30)).Append(Cfs(g.Max30));
            if (g.HasPct30) r.Append(Trim(Edit(g.Pct30, 4, 1)));
            r.Append(',').Append(Trim(Edit(g.LowDays, 1, 0))).Append(',');
            r.Append(Trim(Edit(g.HighDays, 1, 0))).Append(',').Append(Trim(g.Trend)).Append(',');
            if (g.HasTrend) r.Append(Trim(EditFloatingMinus(g.TrendPct, 5, 1)));
            r.Append(',').Append(g.Stale ? 'Y' : 'N').Append(',').Append(run);
            sb.Append(LineOut(r.ToString())).Append('\n');
        }
        try { File.WriteAllText(PathOf("results.csv"), sb.ToString()); }
        catch (IOException)
        {
            sysout.WriteLine("SFLW905E CANNOT OPEN results.csv");
            return false;
        }
        return true;
    }

    // 9000-TERMINATE
    int Terminate()
    {
        sysout.WriteLine($"SFLW005I GAGES STALE OR NO DATA ....{Edit(stale, 5, 0)}");
        sysout.WriteLine("SFLW006I REPORT WRITTEN: streamflow-report.txt");
        sysout.WriteLine("SFLW007I RESULTS WRITTEN: results.csv");
        sysout.WriteLine($"SFLW999I SIERRA-FLOW ENDED  RC={Edit(rc, 1, 0)}");
        return rc;
    }
}
