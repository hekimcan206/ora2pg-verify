using System.Globalization;
using FsCheck;
using FsCheck.Fluent;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.UnitTests.Canonical;

/// <summary>
/// Invariants of the canonical encoding. A false negative in the comparison can only come from two different
/// values encoding the same way, and a false positive from equal values encoding differently, so both directions
/// are checked on generated values.
/// </summary>
public sealed class CanonicalPropertyTests
{
    private static readonly CanonicalOptions O = CanonicalOptions.Default;

    private static Gen<decimal> Decimals { get; } =
        from units in Gen.Choose(-999_999_999, 999_999_999)
        from fraction in Gen.Choose(0, 9_999)
        from scale in Gen.Choose(0, 4)
        select decimal.Round(units + (fraction / 10_000m), scale);

    private static Gen<string> Text { get; } =
        Gen.OneOf(
            ArbMap.Default.GeneratorFor<string>().Where(s => s is not null && !s.Any(char.IsSurrogate)),
            Gen.Elements("", " ", "~", ":", "1:", "0:", "~~", "ş", "İzmir", "Işık ", "📦", "a\u0000b", "  x  ", "12:abc"));

    private static Gen<string?> NullableText { get; } = Gen.OneOf(Text.Select(s => (string?)s), Gen.Constant<string?>(null));

    [FsCheck.Xunit.Property(MaxTest = 1000)]
    public Property Trailing_zeros_and_scale_never_change_a_number() =>
        Prop.ForAll(Arb.From(Decimals), Arb.From(Gen.Choose(0, 6)), (d, extra) =>
        {
            var padded = d.ToString(CultureInfo.InvariantCulture) + (d.Scale == 0 && extra > 0 ? "." : "") + new string('0', extra);
            return Canonicalizer.Field(CanonicalKind.Number, d, O) == Canonicalizer.Field(CanonicalKind.Number, padded, O);
        });

    [FsCheck.Xunit.Property(MaxTest = 1000)]
    public Property Normalised_decimal_text_round_trips_and_is_minimal() =>
        Prop.ForAll(Arb.From(Decimals), d =>
        {
            var text = Canonicalizer.NormalizeDecimalText(d.ToString(CultureInfo.InvariantCulture));
            var parsedBack = decimal.Parse(text, CultureInfo.InvariantCulture) == d;
            var noTrailingZero = !text.Contains('.', StringComparison.Ordinal) || !text.EndsWith('0');
            var noNegativeZero = text != "-0";
            return parsedBack && noTrailingZero && noNegativeZero;
        });

    [FsCheck.Xunit.Property(MaxTest = 1000)]
    public Property Different_numbers_never_encode_alike() =>
        Prop.ForAll(Arb.From(Decimals), Arb.From(Decimals), (a, b) =>
            (a == b) == (Canonicalizer.Field(CanonicalKind.Number, a, O) == Canonicalizer.Field(CanonicalKind.Number, b, O)));

    [FsCheck.Xunit.Property(MaxTest = 1000)]
    public Property An_instant_encodes_the_same_whatever_the_offset() =>
        Prop.ForAll(Arb.From(Gen.Choose(0, 400_000_000)), Arb.From(Gen.Choose(-14 * 4, 14 * 4)), Arb.From(Gen.Choose(0, 999_999)), (seconds, quarterHours, micros) =>
        {
            var utc = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds).AddTicks(micros * 10L);
            var shifted = utc.ToOffset(TimeSpan.FromMinutes(quarterHours * 15));
            return Canonicalizer.Field(CanonicalKind.TimestampTz, utc, O) == Canonicalizer.Field(CanonicalKind.TimestampTz, shifted, O);
        });

    [FsCheck.Xunit.Property(MaxTest = 500)]
    public Property Instants_one_microsecond_apart_never_encode_alike() =>
        Prop.ForAll(Arb.From(Gen.Choose(0, 400_000_000)), seconds =>
        {
            var t = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
            return Canonicalizer.Field(CanonicalKind.TimestampTz, t, O) != Canonicalizer.Field(CanonicalKind.TimestampTz, t.AddTicks(10), O);
        });

    [FsCheck.Xunit.Property(MaxTest = 1000)]
    public Property Char_padding_is_not_data() =>
        Prop.ForAll(Arb.From(Text), Arb.From(Gen.Choose(0, 20)), (s, pad) =>
            Canonicalizer.Field(CanonicalKind.FixedChar, s, O) == Canonicalizer.Field(CanonicalKind.FixedChar, s + new string(' ', pad), O));

    [FsCheck.Xunit.Property(MaxTest = 1000)]
    public Property Varchar_trailing_spaces_are_data() =>
        Prop.ForAll(Arb.From(Text), Arb.From(Gen.Choose(1, 5)), (s, pad) =>
            Canonicalizer.Field(CanonicalKind.Text, s, O) != Canonicalizer.Field(CanonicalKind.Text, s + new string(' ', pad), O));

    /// <summary>The row encoding is injective: equal row strings imply equal field lists (NULL ≠ '' ≠ "~").</summary>
    [FsCheck.Xunit.Property(MaxTest = 2000)]
    public Property Row_encoding_is_injective() =>
        Prop.ForAll(Arb.From(NullableText.ListOf(3)), Arb.From(NullableText.ListOf(3)), (a, b) =>
        {
            var ra = Canonicalizer.Row(a.Select(x => Canonicalizer.Field(CanonicalKind.Text, x, O)));
            var rb = Canonicalizer.Row(b.Select(x => Canonicalizer.Field(CanonicalKind.Text, x, O)));
            return (ra == rb) == a.SequenceEqual(b);
        });

    [FsCheck.Xunit.Property(MaxTest = 1000)]
    public Property Encoded_keys_decode_back_for_display() =>
        Prop.ForAll(Arb.From(Text.Where(s => s.Length > 0 && s != "NULL" && !s.Contains('|', StringComparison.Ordinal)).ListOf(2)), parts =>
            Canonicalizer.DisplayKey(string.Concat(parts.Select(p => Canonicalizer.Encode(p)))) == string.Join("|", parts));

    [FsCheck.Xunit.Property(MaxTest = 1000)]
    public Property Hash60_and_bucket_stay_in_range() =>
        Prop.ForAll(Arb.From(Text), Arb.From(Gen.Choose(1, 100_000)), (s, n) =>
        {
            var h = Canonicalizer.Hash60(Canonicalizer.HexDigest(s, RowHashAlgorithm.Md5));
            var b = Canonicalizer.Bucket(s, n);
            return h >= 0 && h < (1L << 60) && b >= 0 && b < n;
        });
}
