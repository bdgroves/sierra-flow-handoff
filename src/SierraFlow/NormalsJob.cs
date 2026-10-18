using System.Text;
using static SierraFlow.Cobol;

namespace SierraFlow;

/// <summary>
/// NORMALS.cob V1.0: the normal flow for every gage and day of the year.
/// Each daily value counts toward the 7 days around its date (a 7-day window
/// across 30 years, about 210 values), and the 10th to 90th percentiles are
/// interpolated at Weibull rank p * (n + 1), the plotting position USGS uses.
///
/// The COBOL does this with a SORT of ~584,000 records; here it is a dictionary
/// of lists. Same answer, and the parallel run proves it.
/// </summary>
public sealed class NormalsJob
{
    static readonly int[] Cum = { 0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335 };
    static readonly decimal[] Percentiles = { 0.10m, 0.25m, 0.50m, 0.75m, 0.90m };
    const int MaxPerGroup = 400;     // WS-V OCCURS 400

    readonly string dir;
    readonly TextWriter sysout;

    public NormalsJob(string dir, TextWriter sysout) { this.dir = dir; this.sysout = sysout; }

    public int Run()
    {
        sysout.WriteLine("NRML000I NORMALS V1.0 STARTED");
        string hist = Path.Combine(dir, "history.csv");
        if (!File.Exists(hist))
        {
            // The COBOL opens normals.csv for output first, so it is already empty by now.
            File.WriteAllText(Path.Combine(dir, "normals.csv"),
                "site_id,slot,month,day,n_values,n_years,p10,p25,p50,p75,p90\n");
            sysout.WriteLine("NRML902E CANNOT OPEN history.csv");
            return 12;
        }

        // 1000-RELEASE-WINDOWS: every value goes to the 7 day-slots around its date
        var groups = new SortedDictionary<(string Site, int Slot), List<(decimal Flow, int Year)>>(
            Comparer<(string Site, int Slot)>.Create((a, b) =>
            {
                int c = string.CompareOrdinal(a.Site, b.Site);
                return c != 0 ? c : a.Slot.CompareTo(b.Slot);
            }));
        int read = 0, skipped = 0, released = 0;
        bool first = true;
        foreach (var rec in ReadRecords(hist, 80))
        {
            if (first) { first = false; continue; }
            read++;
            var f = Unstring(rec, 3, 20);
            if (f[2].Trim().Length == 0 || f[1][4] != '-') { skipped++; continue; }
            int year = int.Parse(f[1][..4]);
            int month = int.Parse(f[1].Substring(5, 2));
            int day = int.Parse(f[1].Substring(8, 2));
            int slot = Cum[month - 1] + day;
            decimal flow = Pic(NumVal(f[2]), 7, 2);
            string site = X(f[0], 8);
            for (int k = -3; k <= 3; k++)
            {
                int target = slot + k;
                if (target < 1) target += 366;
                if (target > 366) target -= 366;       // the window wraps Dec 31 -> Jan 1
                var key = (site, target);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new();
                list.Add((flow, year));
                released++;
            }
        }

        // 2000-BUILD-PERCENTILES: one line per gage and day of year
        var sb = new StringBuilder("site_id,slot,month,day,n_values,n_years,p10,p25,p50,p75,p90\n");
        int written = 0, thin = 0;
        foreach (var ((site, slot), list) in groups)
        {
            var values = list.Select(x => x.Flow).OrderBy(v => v).Take(MaxPerGroup).ToArray();
            // Years are counted by MOD(year, 100) in a 100-slot table.
            int years = list.Select(x => x.Year % 100).Distinct().Count();
            int n = values.Length;
            if (years < 10) thin++;

            int month = 1;
            for (int mi = 2; mi <= 12; mi++) if (slot > Cum[mi - 1]) month = mi;
            int day = slot - Cum[month - 1];

            sb.Append(site.TrimEnd(' ').Split(' ')[0])
              .Append(',').Append(slot).Append(',').Append(month).Append(',').Append(day)
              .Append(',').Append(n).Append(',').Append(years);
            foreach (var p in Percentiles)
                sb.Append(',').Append(Trim(Edit(Percentile(values, p), 6, 2)));
            sb.Append('\n');
            written++;
        }
        File.WriteAllText(Path.Combine(dir, "normals.csv"), sb.ToString());

        sysout.WriteLine($"NRML010I DAILY VALUES READ ......{Edit(read, 7, 0)}");
        sysout.WriteLine($"NRML011I SKIPPED (NO VALUE) .....{Edit(skipped, 7, 0)}");
        sysout.WriteLine($"NRML012I RECORDS SORTED .........{Edit(released, 7, 0)}");
        sysout.WriteLine($"NRML013I NORMALS WRITTEN ........{Edit(written, 7, 0)}");
        int rc = 0;
        if (thin > 0)
        {
            sysout.WriteLine($"NRML020W DAYS WITH UNDER 10 YEARS{Edit(thin, 7, 0)}");
            rc = 4;
        }
        sysout.WriteLine("NRML999I NORMALS ENDED");
        return rc;
    }

    // 2300-PERCENTILE: Weibull plotting position, interpolated between neighbours
    static decimal Percentile(decimal[] v, decimal p)
    {
        int n = v.Length;
        decimal rank = p * (n + 1);
        if (rank <= 1) return v[0];
        if (rank >= n) return v[n - 1];
        int ri = (int)Math.Truncate(rank);
        decimal rf = Pic(rank - ri, 0, 4);
        return Pic(Rounded(v[ri - 1] + rf * (v[ri] - v[ri - 1]), 2), 7, 2);
    }
}
