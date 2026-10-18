// sierraflow: the SIERRA-FLOW batch job in C#.
//
//   sierraflow run     [dir]   sites.csv + normals.csv + streamflow.csv -> streamflow-report.txt, results.csv
//   sierraflow normals [dir]   history.csv -> normals.csv
//   sierraflow sql     [dir]   sites.csv + results.csv + streamflow.csv -> load.sql (T-SQL, SQL Server / Azure SQL)
//
// Like the COBOL it replaces, it works on the files in one folder (the current
// one unless you name another), writes the same console messages, and exits
// with the same return codes: 0 normal, 4 warning, 12 a file could not be opened.

using SierraFlow;

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
string dir = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();

int rc = cmd switch
{
    "run" => new FlowJob(dir, Console.Out).Run(),
    "normals" => new NormalsJob(dir, Console.Out).Run(),
    "sql" => SqlExport.Run(dir, Console.Out),
    _ => Usage(),
};
return rc;

static int Usage()
{
    Console.Error.WriteLine("usage: sierraflow run|normals|sql [dir]");
    return 16;
}
