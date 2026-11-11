using Ora2PgVerify.Canonical;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.UnitTests.Canonical;

public sealed class CanonicalizerTests
{
    private static readonly CanonicalOptions Strict = CanonicalOptions.Default;
    private static readonly CanonicalOptions Lenient = new(EmptyStringPolicy.AsNull);

    [Theory]
    [InlineData("12.50", "12.5")]
    [InlineData("0.500", "0.5")]
    [InlineData(".5", "0.5")]
    [InlineData("-.5", "-0.5")]
    [InlineData("-0.00", "0")]
    [InlineData("+007", "7")]
    [InlineData("100", "100")]
    [InlineData("100.", "100")]
    [InlineData("12345678901234567890123456789012345678", "12345678901234567890123456789012345678")]
    [InlineData("0.00000000000000000000000000000000000001", "0.00000000000000000000000000000000000001")]
    public void Decimal_text_is_normalised(string input, string expected) =>
        Canonicalizer.NormalizeDecimalText(input).Should().Be(expected);

    [Theory]
    [InlineData("1E+5")]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("abc")]
    public void Not_a_plain_decimal_is_refused(string input) =>
        FluentActions.Invoking(() => Canonicalizer.NormalizeDecimalText(input)).Should().Throw<FormatException>();

    [Fact]
    public void Numbers_from_every_driver_type_agree()
    {
        var expected = Canonicalizer.Field(CanonicalKind.Number, 42m, Strict);
        Canonicalizer.Field(CanonicalKind.Number, 42L, Strict).Should().Be(expected);
        Canonicalizer.Field(CanonicalKind.Number, 42, Strict).Should().Be(expected);
        Canonicalizer.Field(CanonicalKind.Number, (short)42, Strict).Should().Be(expected);
        Canonicalizer.Field(CanonicalKind.Number, "42.000", Strict).Should().Be(expected);
        Canonicalizer.Field(CanonicalKind.Number, 42.0d, Strict).Should().Be(expected);
        expected.Should().Be("2:42");
    }

    [Fact]
    public void A_double_on_the_target_shows_its_lost_digits_like_float8_to_numeric()
    {
        // NUMBER(20,0) value 12345678901234567890 stored as double precision comes back with 15 significant digits.
        Canonicalizer.Value(CanonicalKind.Number, 12345678901234567890d, Strict).Should().Be("12345678901234600000");
        Canonicalizer.Value(CanonicalKind.Number, 123456789.123456789d, Strict).Should().Be("123456789.123457");
    }

    [Fact]
    public void Null_and_empty_string_are_different_fields_unless_the_policy_says_otherwise()
    {
        Canonicalizer.Field(CanonicalKind.Text, null, Strict).Should().Be("~");
        Canonicalizer.Field(CanonicalKind.Text, "", Strict).Should().Be("0:");
        Canonicalizer.Field(CanonicalKind.Text, "", Lenient).Should().Be("~");
        Canonicalizer.Field(CanonicalKind.Text, "~", Strict).Should().Be("1:~");
    }

    [Fact]
    public void Length_prefix_counts_utf8_bytes()
    {
        Canonicalizer.Field(CanonicalKind.Text, "ş", Strict).Should().Be("2:ş");
        Canonicalizer.Field(CanonicalKind.Text, "📦", Strict).Should().Be("4:📦");
        Canonicalizer.Field(CanonicalKind.Text, "İzmir", Strict).Should().Be("6:İzmir");
    }

    [Fact]
    public void Char_follows_blank_padded_semantics()
    {
        Canonicalizer.Field(CanonicalKind.FixedChar, "C0000042  ", Strict).Should().Be(Canonicalizer.Field(CanonicalKind.FixedChar, "C0000042", Strict));
        Canonicalizer.Field(CanonicalKind.FixedChar, "   ", Strict).Should().Be("~", "an all-blank CHAR trims to '' which Oracle treats as NULL");
        Canonicalizer.Field(CanonicalKind.FixedChar, " a", Strict).Should().Be("2: a", "leading blanks are data");
    }

    [Fact]
    public void Varchar_keeps_trailing_spaces()
    {
        Canonicalizer.Field(CanonicalKind.Text, "PAID ", Strict).Should().NotBe(Canonicalizer.Field(CanonicalKind.Text, "PAID", Strict));
    }

    [Fact]
    public void Date_is_rendered_at_seconds_and_keeps_an_unexpected_fraction()
    {
        var dt = new DateTime(2024, 2, 29, 23, 59, 58);
        Canonicalizer.Value(CanonicalKind.Date, dt, Strict).Should().Be("2024-02-29T23:59:58");
        Canonicalizer.Value(CanonicalKind.Date, dt.AddTicks(1234560), Strict).Should().Be("2024-02-29T23:59:58.123456");
        Canonicalizer.Value(CanonicalKind.Date, new DateOnly(2024, 2, 29), Strict).Should().Be("2024-02-29T00:00:00");
    }

    [Fact]
    public void Timestamp_is_rendered_at_microseconds_truncating_ticks()
    {
        var dt = new DateTime(2025, 1, 2, 3, 4, 5).AddTicks(1234567);
        Canonicalizer.Value(CanonicalKind.Timestamp, dt, Strict).Should().Be("2025-01-02T03:04:05.123456");
    }

    [Fact]
    public void Timestamp_with_time_zone_compares_instants_not_offsets()
    {
        var istanbul = new DateTimeOffset(2025, 6, 1, 15, 0, 0, TimeSpan.FromHours(3));
        var utc = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        Canonicalizer.Value(CanonicalKind.TimestampTz, istanbul, Strict).Should().Be("2025-06-01T12:00:00.000000Z");
        Canonicalizer.Value(CanonicalKind.TimestampTz, utc, Strict).Should().Be(Canonicalizer.Value(CanonicalKind.TimestampTz, istanbul, Strict));
        Canonicalizer.Value(CanonicalKind.TimestampTz, DateTime.SpecifyKind(utc.UtcDateTime, DateTimeKind.Utc), Strict)
            .Should().Be("2025-06-01T12:00:00.000000Z");
    }

    [Fact]
    public void Large_text_travels_as_a_digest_of_its_utf8_bytes()
    {
        // Same digests as PostgreSQL md5('abc') / md5('şğü') and Oracle DBMS_CRYPTO.HASH on a CLOB (verified on both engines).
        Canonicalizer.Value(CanonicalKind.LargeText, "abc", Strict).Should().Be("900150983cd24fb0d6963f7d28e17f72");
        Canonicalizer.Value(CanonicalKind.LargeText, "şğü", Strict).Should().Be("0886810d79acb3d40ed1379484c9e32b");
        Canonicalizer.Field(CanonicalKind.LargeText, "", Lenient).Should().Be("~");
        Canonicalizer.Field(CanonicalKind.LargeText, "abc", new CanonicalOptions(Hash: RowHashAlgorithm.Sha256))
            .Should().Be("64:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Hash60_takes_the_first_15_hex_digits()
    {
        // Oracle: TO_NUMBER(SUBSTR(RAWTOHEX(STANDARD_HASH('abc','MD5')),1,15),'XXXXXXXXXXXXXXX'); PostgreSQL: ('x'||substr(md5('abc'),1,15))::bit(60)::bigint
        Canonicalizer.Hash60(Canonicalizer.HexDigest("abc", RowHashAlgorithm.Md5)).Should().Be(648541476951500027L);
    }

    [Fact]
    public void Display_key_decodes_encoded_fields()
    {
        Canonicalizer.DisplayKey("6:1234561:2").Should().Be("123456|2");
        Canonicalizer.DisplayKey("8:C0000042").Should().Be("C0000042");
        Canonicalizer.DisplayKey("~2:ab").Should().Be("NULL|ab");
        Canonicalizer.DisplayKey("3:İz").Should().Be("İz");
        Canonicalizer.DisplayKey("9:short").Should().Be("9:short", "malformed input is shown as is");
    }
}
