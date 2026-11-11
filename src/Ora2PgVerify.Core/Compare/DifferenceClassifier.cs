using System.Globalization;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.Compare;

/// <summary>Explains a pair of differing encoded fields in migration terms (empty string vs NULL, time zone shift, ...).</summary>
public static class DifferenceClassifier
{
    public static (ValueDifference Difference, string Detail) Classify(ColumnPair column, string sourceEncoded, string targetEncoded)
    {
        ArgumentNullException.ThrowIfNull(column);
        var s = Decode(sourceEncoded);
        var t = Decode(targetEncoded);

        if (s is null && t is { Length: 0 })
        {
            return (ValueDifference.EmptyStringVsNull, "Oracle NULL, PostgreSQL empty string");
        }

        if (s is null || t is null)
        {
            return (ValueDifference.NullVsValue, s is null ? "Oracle NULL, PostgreSQL has a value" : "Oracle has a value, PostgreSQL NULL");
        }

        switch (column.Kind)
        {
            case CanonicalKind.LargeText:
                return (ValueDifference.DigestDiffers, "content digest differs");

            case CanonicalKind.Number when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var sd)
                                         && decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var td):
                return (ValueDifference.NumericDrift, string.Create(CultureInfo.InvariantCulture, $"{(td - sd >= 0 ? "+" : "")}{td - sd}"));

            case CanonicalKind.Date or CanonicalKind.Timestamp or CanonicalKind.TimestampTz
                when TryInstant(s, out var si) && TryInstant(t, out var ti):
                if (si.Date == ti.Date && ti.TimeOfDay == TimeSpan.Zero && si.TimeOfDay != TimeSpan.Zero)
                {
                    return (ValueDifference.TimeOfDayLost, "time of day dropped (truncated to midnight)");
                }

                var delta = ti - si;
                if (delta.Ticks % TimeSpan.FromMinutes(15).Ticks == 0)
                {
                    return (ValueDifference.TimeShift, string.Create(CultureInfo.InvariantCulture, $"shifted by {(delta >= TimeSpan.Zero ? "+" : "-")}{delta.Duration():g}"));
                }

                return (ValueDifference.Other, string.Create(CultureInfo.InvariantCulture, $"differs by {delta:g}"));

            case CanonicalKind.Text or CanonicalKind.FixedChar:
                if (s.TrimEnd() == t.TrimEnd())
                {
                    return (ValueDifference.TrailingWhitespace, "differs only in trailing whitespace");
                }

                // Dotted/dotless i (Turkish) fold differently per locale; a database upper() may use either rule.
                if (string.Equals(CaseFold(s), CaseFold(t), StringComparison.Ordinal))
                {
                    return (ValueDifference.CaseOnly, "differs only in letter case");
                }

                break;
        }

        return (ValueDifference.Other, "value differs");
    }

    private static string CaseFold(string s) => s.Replace('ı', 'i').Replace('İ', 'I').ToUpperInvariant();

    /// <summary>Strips the length prefix: <c>~</c> → null, <c>3:abc</c> → abc.</summary>
    public static string? Decode(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded == Canonicalizer.NullToken)
        {
            return null;
        }

        var colon = encoded.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? encoded : encoded[(colon + 1)..];
    }

    private static bool TryInstant(string s, out DateTime value) =>
        DateTime.TryParse(s.TrimEnd('Z'), CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}
