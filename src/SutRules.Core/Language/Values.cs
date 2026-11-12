using System.Globalization;

namespace SutRules.Language;

/// <summary>
/// Run-time value helpers. Values are boxed CLR objects (bool, int, decimal, string, DateOnly, records, lists);
/// enums travel as their member name. Booleans and small integers come from caches, so the hot path of a rule
/// (comparisons, counters, line numbers) does not allocate.
/// </summary>
public static class Values
{
    public static readonly object True = true;
    public static readonly object False = false;

    private const int CacheMin = -128;
    private const int CacheMax = 1024;
    private static readonly object[] SmallInts = Enumerable.Range(CacheMin, CacheMax - CacheMin + 1).Select(i => (object)i).ToArray();

    public static object Bool(bool value) => value ? True : False;

    public static object Int(int value) =>
        value is >= CacheMin and <= CacheMax ? SmallInts[value - CacheMin] : value;

    public static object? Int(int? value) => value is { } v ? Int(v) : null;

    /// <summary>Only an actual <c>true</c> passes a condition; null and false do not (two-valued at the boolean level).</summary>
    public static bool IsTrue(object? value) => value is true;

    /// <summary>
    /// The text form used by <c>str()</c>, report fields and templates: invariant culture, ISO dates, decimals with
    /// their scale (120.00 stays 120.00), null as the empty string.
    /// </summary>
    public static string Display(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? string.Empty,
    };

    public static decimal ToDecimal(object value) => value is int i ? i : (decimal)value;

    /// <summary>Null-aware equality with int/decimal widening.</summary>
    public static bool AreEqual(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a is int ia && b is int ib)
        {
            return ia == ib;
        }

        if (a is decimal || b is decimal)
        {
            return (a is int or decimal) && (b is int or decimal) && ToDecimal(a) == ToDecimal(b);
        }

        if (a is string sa && b is string sb)
        {
            return string.Equals(sa, sb, StringComparison.Ordinal);
        }

        return a.Equals(b);
    }

    /// <summary>Ordering for ordered types; null when either side is null (so relational operators yield false).</summary>
    public static int? Compare(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return null;
        }

        return (a, b) switch
        {
            (int x, int y) => x.CompareTo(y),
            (string x, string y) => string.CompareOrdinal(x, y),
            (DateOnly x, DateOnly y) => x.CompareTo(y),
            _ => ToDecimal(a).CompareTo(ToDecimal(b)),
        };
    }
}
