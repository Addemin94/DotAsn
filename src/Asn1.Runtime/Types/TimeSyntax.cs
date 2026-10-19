using System.Text.RegularExpressions;

namespace Asn1.Runtime;

/// <summary>Syntax validation of time values (X.680 46-47 for UTCTime/GeneralizedTime, X.680 38 for the ISO 8601 time types).</summary>
public static partial class TimeSyntax
{
    [GeneratedRegex(@"^\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])([01]\d|2[0-3])[0-5]\d([0-5]\d)?(Z|[+-]([01]\d|2[0-3])[0-5]\d)$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcTimeRegex();

    [GeneratedRegex(@"^\d{4}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])([01]\d|2[0-3])([0-5]\d([0-5]\d|60)?)?([.,]\d+)?(Z|[+-]([01]\d|2[0-3])([0-5]\d)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneralizedTimeRegex();

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$", RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d:([0-5]\d|60)$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeOfDayRegex();

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])T([01]\d|2[0-3]):[0-5]\d:([0-5]\d|60)$", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeRegex();

    [GeneratedRegex(@"^P(?!$)(\d+([.,]\d+)?Y)?(\d+([.,]\d+)?M)?(\d+([.,]\d+)?W)?(\d+([.,]\d+)?D)?(T(?=\d)(\d+([.,]\d+)?H)?(\d+([.,]\d+)?M)?(\d+([.,]\d+)?S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^[0-9TZPYMWDHSR:.,+\-/C]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeRegex();

    /// <summary>Returns an error message if <paramref name="value"/> is not valid notation for <paramref name="kind"/>, otherwise null.</summary>
    public static string? Validate(TimeKind kind, string value)
    {
        var ok = kind switch
        {
            TimeKind.UtcTime => UtcTimeRegex().IsMatch(value),
            TimeKind.GeneralizedTime => GeneralizedTimeRegex().IsMatch(value),
            TimeKind.Date => DateRegex().IsMatch(value),
            TimeKind.TimeOfDay => TimeOfDayRegex().IsMatch(value),
            TimeKind.DateTime => DateTimeRegex().IsMatch(value),
            TimeKind.Duration => DurationRegex().IsMatch(value),
            _ => value.Length > 0 && TimeRegex().IsMatch(value),
        };
        return ok ? null : kind switch
        {
            TimeKind.UtcTime => "expected YYMMDDhhmm[ss] followed by Z or +hhmm/-hhmm",
            TimeKind.GeneralizedTime => "expected YYYYMMDDHH[MM[SS]][.fff][Z|+-hh[mm]]",
            TimeKind.Date => "expected YYYY-MM-DD",
            TimeKind.TimeOfDay => "expected HH:MM:SS",
            TimeKind.DateTime => "expected YYYY-MM-DDTHH:MM:SS",
            TimeKind.Duration => "expected an ISO 8601 duration such as P1DT2H",
            _ => "expected ISO 8601 time notation",
        };
    }

    /// <summary>
    /// True if a UTCTime/GeneralizedTime value is in the canonical DER/CER form (X.690 11.7, 11.8): UTC with
    /// trailing 'Z', seconds present, '.' as decimal mark and no trailing zeros in the fraction.
    /// </summary>
    public static bool IsCanonical(TimeKind kind, string value)
    {
        if (!value.EndsWith('Z'))
        {
            return false;
        }

        if (kind == TimeKind.UtcTime)
        {
            return value.Length == 13;
        }

        if (kind != TimeKind.GeneralizedTime)
        {
            return true;
        }

        if (value.Length < 15 || value.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return value.Length == 15;
        }

        return dot == 14 && value.Length > 16 && value[^2] != '0';
    }
}
