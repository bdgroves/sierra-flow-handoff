using System.Globalization;
using System.Text;

namespace SierraFlow;

/// <summary>
/// Writes load.sql: one transaction that upserts tonight's numbers into
/// SQL Server or Azure SQL (schema in sql/01_schema.sql).
///
///   sierra.gage         the eight gages, with a geography point each
///   sierra.daily_flow   every daily mean ever loaded; MERGE, because USGS revises
///                       provisional values, and each revision is kept in
///   sierra.flow_revision
///   sierra.run_result   one row per gage per night (results.csv)
///
/// It generates plain T-SQL rather than talking to the server, so the job itself
/// needs no database driver and the script can be read, diffed and rerun by hand.
/// </summary>
public static class SqlExport
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string dir, TextWriter sysout)
    {
        string P(string f) => Path.Combine(dir, f);
        foreach (var f in new[] { "sites.csv", "streamflow.csv", "results.csv" })
            if (!File.Exists(P(f))) { sysout.WriteLine($"SFSQ901E CANNOT OPEN {f}"); return 12; }

        var sites = ReadCsv(P("sites.csv"));
        var flows = ReadCsv(P("streamflow.csv"));
        var results = ReadCsv(P("results.csv"));
        var sb = new StringBuilder();
        sb.AppendLine("-- load.sql, written by sierraflow sql. One transaction: all of tonight, or none of it.");
        sb.AppendLine("SET NOCOUNT ON;");
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("BEGIN TRANSACTION;");
        // Stamp changes with the night the job ran, not the moment the script is replayed,
        // so loading old nights in order rebuilds the same history.
        string stamp = results.Count > 0 ? $"{results[0]["run_date"]} {results[0]["run_time"]}" : DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", Inv);
        sb.AppendLine($"DECLARE @loaded datetime2(0) = '{stamp}';");
        sb.AppendLine();

        // --- gages ---
        sb.AppendLine("MERGE sierra.gage AS t");
        sb.AppendLine("USING (VALUES");
        sb.AppendLine(string.Join(",\n", sites.Select(s =>
            $"    ({Str(s["site_id"])}, {Str(s["short_name"])}, {Str(s["basin"])}, {Num(s["basin_seq"])}, " +
            $"{Num(s["seq"])}, {Num(s["drain_sqmi"])}, {Str(s["regulation"])}, {Str(s["usgs_name"])}, " +
            $"{Num(s["lat"])}, {Num(s["lon"])})")));
        sb.AppendLine(") AS s (site_id, short_name, basin, basin_seq, seq, drain_sqmi, regulation, usgs_name, lat, lon)");
        sb.AppendLine("ON t.site_id = s.site_id");
        sb.AppendLine("WHEN MATCHED THEN UPDATE SET short_name = s.short_name, basin = s.basin, basin_seq = s.basin_seq,");
        sb.AppendLine("    seq = s.seq, drain_sqmi = s.drain_sqmi, regulation = s.regulation, usgs_name = s.usgs_name,");
        sb.AppendLine("    location = geography::Point(s.lat, s.lon, 4326)");
        sb.AppendLine("WHEN NOT MATCHED THEN INSERT (site_id, short_name, basin, basin_seq, seq, drain_sqmi, regulation, usgs_name, location)");
        sb.AppendLine("    VALUES (s.site_id, s.short_name, s.basin, s.basin_seq, s.seq, s.drain_sqmi, s.regulation, s.usgs_name,");
        sb.AppendLine("            geography::Point(s.lat, s.lon, 4326));");
        sb.AppendLine();

        // --- daily means: staged, then merged so revisions are caught ---
        sb.AppendLine("CREATE TABLE #flow (site_id varchar(15) NOT NULL, flow_date date NOT NULL, cfs decimal(9,2) NOT NULL, approval char(1) NULL);");
        foreach (var chunk in flows.Chunk(900))                // INSERT ... VALUES takes 1000 rows at most
        {
            sb.AppendLine("INSERT INTO #flow (site_id, flow_date, cfs, approval) VALUES");
            sb.AppendLine(string.Join(",\n", chunk.Select(f =>
                $"    ({Str(f["site_id"])}, {Str(f["date"])}, {Num(f["cfs"])}, {Str(f.GetValueOrDefault("approval", ""))})")) + ";");
        }
        sb.AppendLine();
        sb.AppendLine("MERGE sierra.daily_flow AS t");
        sb.AppendLine("USING #flow AS s ON t.site_id = s.site_id AND t.flow_date = s.flow_date");
        sb.AppendLine("WHEN MATCHED AND (t.cfs <> s.cfs OR ISNULL(t.approval, '') <> ISNULL(s.approval, '')) THEN");
        sb.AppendLine("    UPDATE SET cfs = s.cfs, approval = s.approval, loaded_utc = @loaded");
        sb.AppendLine("WHEN NOT MATCHED THEN");
        sb.AppendLine("    INSERT (site_id, flow_date, cfs, approval, loaded_utc) VALUES (s.site_id, s.flow_date, s.cfs, s.approval, @loaded)");
        sb.AppendLine("OUTPUT $action, inserted.site_id, inserted.flow_date, deleted.cfs, inserted.cfs, deleted.approval, inserted.approval, @loaded");
        sb.AppendLine("    INTO sierra.flow_revision (action, site_id, flow_date, old_cfs, new_cfs, old_approval, new_approval, changed_utc);");
        sb.AppendLine("DROP TABLE #flow;");
        sb.AppendLine();

        // --- tonight's results: replace the night, so a rerun is harmless ---
        if (results.Count > 0)
        {
            string runDate = results[0]["run_date"];
            sb.AppendLine($"DELETE FROM sierra.run_result WHERE run_date = {Str(runDate)};");
            sb.AppendLine("INSERT INTO sierra.run_result (run_date, site_id, last_date, last_cfs, n_years, p10, p25, p50, p75, p90,");
            sb.AppendLine("    class, pct_median, days_30, mean_30, min_30, max_30, pct_normal_30, days_below_p10, days_above_p90,");
            sb.AppendLine("    trend, trend_pct, stale, run_time, rc) VALUES");
            sb.AppendLine(string.Join(",\n", results.Select(r =>
                $"    ({Str(r["run_date"])}, {Str(r["site_id"])}, {Str(r["last_date"])}, {Num(r["last_cfs"])}, {Num(r["n_years"])}, " +
                $"{Num(r["p10"])}, {Num(r["p25"])}, {Num(r["p50"])}, {Num(r["p75"])}, {Num(r["p90"])}, " +
                $"{Str(r["class"])}, {Num(r["pct_median"])}, {Num(r["days_30"])}, {Num(r["mean_30"])}, {Num(r["min_30"])}, " +
                $"{Num(r["max_30"])}, {Num(r["pct_normal_30"])}, {Num(r["days_below_p10"])}, {Num(r["days_above_p90"])}, " +
                $"{Str(r["trend"])}, {Num(r["trend_pct"])}, {Str(r["stale"])}, {Str(r["run_time"])}, {Num(r["rc"])})")) + ";");
        }
        sb.AppendLine();
        sb.AppendLine("COMMIT;");

        File.WriteAllText(P("load.sql"), sb.ToString());
        sysout.WriteLine($"SFSQ001I GAGES ..............{sites.Count,6}");
        sysout.WriteLine($"SFSQ002I DAILY MEANS ........{flows.Count,6}");
        sysout.WriteLine($"SFSQ003I RESULT ROWS ........{results.Count,6}");
        sysout.WriteLine("SFSQ999I load.sql WRITTEN");
        return 0;
    }

    // Plain CSV: these files are written by programs, never contain quotes or embedded commas.
    static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
        var head = lines[0].Split(',');
        return lines.Skip(1).Select(l =>
        {
            var v = l.Split(',');
            return head.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i < v.Length ? v[x.i] : "");
        }).ToList();
    }

    static string Str(string s) => s.Length == 0 ? "NULL" : "'" + s.Replace("'", "''") + "'";

    static string Num(string s)
    {
        s = s.Trim();
        return decimal.TryParse(s, NumberStyles.Number, Inv, out var d) ? d.ToString(Inv) : "NULL";
    }
}
