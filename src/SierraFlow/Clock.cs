using System.Globalization;

namespace SierraFlow;

/// <summary>
/// FUNCTION CURRENT-DATE. Honors COB_CURRENT_DATE ("YYYY/MM/DD HH:MM:SS"),
/// the same variable GnuCOBOL reads, so the parallel run can pin both
/// programs to the exact second a past production night ran.
/// </summary>
public sealed class Clock
{
    public DateTime Local { get; }
    public TimeSpan Offset { get; }

    Clock(DateTime local, TimeSpan offset) { Local = local; Offset = offset; }

    public static Clock Now()
    {
        string? pinned = Environment.GetEnvironmentVariable("COB_CURRENT_DATE");
        DateTime local = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(pinned) &&
            DateTime.TryParseExact(pinned.Trim(), "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var p))
            local = p;
        return new Clock(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    public string Date => Local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string Time => Local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"UTC" at offset zero, otherwise "UTC-07:00", as SIERRA-FLOW 1000-INITIALIZE builds it.</summary>
    public string Zone
    {
        get
        {
            if (Offset == TimeSpan.Zero) return "UTC";
            char sign = Offset < TimeSpan.Zero ? '-' : '+';
            var a = Offset.Duration();
            return $"UTC{sign}{a.Hours:00}:{a.Minutes:00}";
        }
    }

    /// <summary>FUNCTION INTEGER-OF-DATE: a day number; only differences matter.</summary>
    public int DayNumber => DateOnly.FromDateTime(Local).DayNumber;
}
