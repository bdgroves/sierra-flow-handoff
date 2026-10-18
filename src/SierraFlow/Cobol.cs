using System.Globalization;
using System.Text;

namespace SierraFlow;

/// <summary>
/// The handful of COBOL rules the old programs lean on without saying so.
/// A port that gets these wrong produces numbers that are close, and close
/// is the hardest kind of wrong to notice. Every helper names the COBOL
/// behaviour it copies.
/// </summary>
public static class Cobol
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// MOVE a number into PIC 9(i)V9(d) or S9(i)V9(d).
    /// COBOL truncates, it does not round: extra decimals are dropped, digits
    /// above the picture are lost from the left, and an unsigned field keeps
    /// the absolute value (a -3 day age becomes 3).
    /// </summary>
    public static decimal Pic(decimal v, int intDigits, int decDigits, bool signed = false)
    {
        if (!signed) v = Math.Abs(v);
        v = Math.Truncate(v * Pow10(decDigits)) / Pow10(decDigits);
        decimal limit = Pow10(intDigits);
        decimal whole = Math.Truncate(v);
        decimal frac = v - whole;
        whole %= limit;                       // high-order truncation
        return whole + frac;
    }

    /// <summary>
    /// COMPUTE ... ROUNDED. COBOL rounds half away from zero.
    /// C#'s Math.Round defaults to banker's rounding (half to even), which
    /// turns 0.25 into 0.2 where COBOL prints 0.3.
    /// </summary>
    public static decimal Rounded(decimal v, int decDigits) =>
        Math.Round(v, decDigits, MidpointRounding.AwayFromZero);

    /// <summary>
    /// COMPUTE target ROUNDED = expr ON SIZE ERROR ... : true when the rounded
    /// value has more integer digits than the receiving picture holds.
    /// </summary>
    public static bool SizeError(decimal rounded, int intDigits) =>
        Math.Abs(rounded) >= Pow10(intDigits);

    /// <summary>
    /// FUNCTION NUMVAL. Leading and trailing spaces are ignored; anything that
    /// is not a number comes back as zero rather than an error.
    /// </summary>
    public static decimal NumVal(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0m;
        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                                Inv, out var v) ? v : 0m;
    }

    /// <summary>MOVE an alphanumeric into PIC X(n): pad with spaces or cut on the right.</summary>
    public static string X(string s, int n) => s.Length >= n ? s[..n] : s.PadRight(n);

    /// <summary>
    /// Zero-suppressed edit picture: Z(z)9 or Z(z)9.9(d), e.g. Z(4)9.99 is
    /// Edit(v, 4, 2). Width is z + 1 integer positions, then the decimals.
    /// MOVE into an edit picture truncates too, so a flow of 123456.78 into
    /// Z(4)9.99 prints as 23456.78.
    /// </summary>
    public static string Edit(decimal v, int z, int decimals)
    {
        v = Pic(v, z + 1, decimals);
        decimal whole = Math.Truncate(v);
        string s = whole.ToString("0", Inv).PadLeft(z + 1);
        if (decimals == 0) return s;
        decimal frac = v - whole;
        string f = Math.Truncate(frac * Pow10(decimals)).ToString("0", Inv).PadLeft(decimals, '0');
        return s + "." + f;
    }

    /// <summary>
    /// Floating minus sign, -(n)9.9: the minus sits just left of the first
    /// digit, n - 1 of the dashes can hold digits, and a positive value gets a
    /// space where the sign would be.
    /// </summary>
    public static string EditFloatingMinus(decimal v, int dashes, int decimals)
    {
        bool neg = v < 0;
        v = Pic(v, dashes, decimals);       // n-1 floating digits + the 9
        decimal whole = Math.Truncate(v);
        string digits = whole.ToString("0", Inv);
        string head = (neg ? "-" : "") + digits;
        string s = head.PadLeft(dashes + 1);
        decimal frac = v - whole;
        string f = Math.Truncate(frac * Pow10(decimals)).ToString("0", Inv).PadLeft(decimals, '0');
        return s + "." + f;
    }

    /// <summary>
    /// UNSTRING rec DELIMITED BY ',' INTO f1 ... fn, each PIC X(width).
    /// Missing fields stay spaces; extra fields are ignored.
    /// </summary>
    public static string[] Unstring(string rec, int count, int width)
    {
        var parts = rec.Split(',');
        var f = new string[count];
        for (int i = 0; i < count; i++)
            f[i] = X(i < parts.Length ? parts[i] : "", width);
        return f;
    }

    /// <summary>
    /// A LINE SEQUENTIAL record: GnuCOBOL writes the record with its trailing
    /// spaces removed.
    /// </summary>
    public static string LineOut(string rec) => rec.TrimEnd(' ');

    /// <summary>
    /// READ of a LINE SEQUENTIAL file into a PIC X(n) record: lines longer
    /// than the record are cut.
    /// </summary>
    public static IEnumerable<string> ReadRecords(string path, int recLen)
    {
        foreach (var line in File.ReadLines(path, Encoding.Latin1))
            yield return line.Length > recLen ? line[..recLen] : line;
    }

    /// <summary>FUNCTION TRIM: spaces off both ends.</summary>
    public static string Trim(string s) => s.Trim(' ');

    public static decimal Pow10(int n)
    {
        decimal p = 1m;
        for (int i = 0; i < n; i++) p *= 10m;
        return p;
    }
}
