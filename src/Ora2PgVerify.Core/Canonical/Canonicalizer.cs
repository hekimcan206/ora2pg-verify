using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ora2PgVerify.Model;

namespace Ora2PgVerify.Canonical;

/// <summary>
/// The reference (C#) implementation of the canonical row encoding. Oracle SQL and PostgreSQL SQL implement the
/// same function (see <see cref="Sql.OracleDialect"/>, <see cref="Sql.PostgresDialect"/>); integration tests prove the
/// three agree value for value.
/// <para>
/// Encoding of one field: <c>~</c> for NULL, otherwise <c>&lt;UTF-8 byte length&gt;:&lt;value text&gt;</c>. The length
/// prefix makes the concatenation of fields injective without escaping: no two different rows produce the same
/// string, and NULL is distinct from the empty string.
/// </para>
/// </summary>
public static class Canonicalizer
{
    public const string NullToken = "~";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Encodes one value. <paramref name="value"/> is what an ADO.NET reader returned (or a test value).</summary>
    public static string Field(CanonicalKind kind, object? value, CanonicalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Encode(Value(kind, value, options));
    }

    /// <summary>The canonical text of a value before the length prefix, or null.</summary>
    public static string? Value(CanonicalKind kind, object? value, CanonicalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (value is null or DBNull)
        {
            return null;
        }

        return kind switch
        {
            CanonicalKind.Number => DecimalText(value),
            CanonicalKind.Date => DateText(ToDateTime(value)),
            CanonicalKind.Timestamp => ToDateTime(value).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            CanonicalKind.TimestampTz => ToUtc(value).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            CanonicalKind.Text => EmptyAware((string)value, options),
            CanonicalKind.FixedChar => FixedChar((string)value),
            CanonicalKind.LargeText => EmptyAware((string)value, options) is { } s ? HexDigest(s, options.Hash) : null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    public static string Encode(string? canonicalValue) =>
        canonicalValue is null
            ? NullToken
            : string.Create(CultureInfo.InvariantCulture, $"{Utf8.GetByteCount(canonicalValue)}:{canonicalValue}");

    /// <summary>Concatenates already-encoded fields in column order.</summary>
    public static string Row(IEnumerable<string> encodedFields) => string.Concat(encodedFields);

    /// <summary>Lower-case hex digest of the UTF-8 bytes of <paramref name="text"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "MD5 is an integrity fingerprint that must match Oracle STANDARD_HASH and PostgreSQL md5(); not used for security. SHA-256 is selectable.")]
    public static string HexDigest(string text, RowHashAlgorithm algorithm)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Utf8.GetBytes(text);
        var hash = algorithm == RowHashAlgorithm.Md5 ? MD5.HashData(bytes) : SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// The first 60 bits of a hex digest as a non-negative integer. Chunk fingerprints are SUM(h60) and COUNT(*):
    /// order-independent, sensitive to duplicates (unlike XOR), and exact in Oracle NUMBER and PostgreSQL numeric.
    /// </summary>
    public static long Hash60(string hexDigest)
    {
        ArgumentNullException.ThrowIfNull(hexDigest);
        return long.Parse(hexDigest.AsSpan(0, 15), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    /// <summary>Bucket of a canonical key for hash-bucket chunking: first 32 bits of its MD5, modulo the bucket count.</summary>
    public static long Bucket(string canonicalKey, long buckets)
    {
        var hex = HexDigest(canonicalKey, RowHashAlgorithm.Md5);
        return long.Parse(hex.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) % buckets;
    }

    /// <summary>Decodes a canonical key back to readable parts for reports (<c>12</c>, <c>C-0042</c>, <c>17|2</c>).</summary>
    public static string DisplayKey(string canonicalKey)
    {
        ArgumentNullException.ThrowIfNull(canonicalKey);
        var parts = new List<string>();
        var bytes = Utf8.GetBytes(canonicalKey);
        var i = 0;
        while (i < bytes.Length)
        {
            if (bytes[i] == (byte)'~')
            {
                parts.Add("NULL");
                i++;
                continue;
            }

            var colon = Array.IndexOf(bytes, (byte)':', i);
            if (colon < 0)
            {
                return canonicalKey;
            }

            if (!int.TryParse(Encoding.ASCII.GetString(bytes, i, colon - i), NumberStyles.None, CultureInfo.InvariantCulture, out var len) || colon + 1 + len > bytes.Length)
            {
                return canonicalKey;
            }

            parts.Add(Utf8.GetString(bytes, colon + 1, len));
            i = colon + 1 + len;
        }

        return string.Join("|", parts);
    }

    /// <summary>
    /// Canonical decimal text: invariant culture, no exponent, no leading '+', no trailing fractional zeros, no
    /// trailing point, a leading "0" before the point, and no negative zero. Works on text so values beyond
    /// System.Decimal's 28-29 digits (Oracle NUMBER has 38) are handled.
    /// </summary>
    public static string NormalizeDecimalText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        if (s.Length == 0 || s.Contains('E', StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Not a plain decimal: '{text}'.");
        }

        var negative = s[0] == '-';
        if (s[0] is '-' or '+')
        {
            s = s[1..];
        }

        var point = s.IndexOf('.', StringComparison.Ordinal);
        var intPart = point < 0 ? s : s[..point];
        var fracPart = point < 0 ? string.Empty : s[(point + 1)..];
        if (!intPart.All(char.IsAsciiDigit) || !fracPart.All(char.IsAsciiDigit) || intPart.Length + fracPart.Length == 0)
        {
            throw new FormatException($"Not a plain decimal: '{text}'.");
        }

        intPart = intPart.TrimStart('0');
        fracPart = fracPart.TrimEnd('0');
        if (intPart.Length == 0)
        {
            intPart = "0";
        }

        var body = fracPart.Length == 0 ? intPart : intPart + "." + fracPart;
        return negative && body != "0" ? "-" + body : body;
    }

    private static string DecimalText(object value) => value switch
    {
        decimal d => NormalizeDecimalText(d.ToString(CultureInfo.InvariantCulture)),
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        short s => s.ToString(CultureInfo.InvariantCulture),
        string s => NormalizeDecimalText(s),
        // Floating point on the target: render through decimal with 15 significant digits, the same rule
        // PostgreSQL applies in float8::numeric, so precision loss becomes visible instead of being hidden.
        double f => NormalizeDecimalText(decimal.Parse(f.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
        float f => NormalizeDecimalText(decimal.Parse(((double)f).ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
        _ => throw new ArgumentException($"Unsupported decimal value type {value.GetType().Name}.", nameof(value)),
    };

    /// <summary>Second precision; a non-zero fraction (possible only on the target) is kept so it is not silently hidden.</summary>
    private static string DateText(DateTime dt)
    {
        var micros = dt.Ticks % TimeSpan.TicksPerSecond / 10;
        var head = dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return micros == 0 ? head : string.Create(CultureInfo.InvariantCulture, $"{head}.{micros:D6}");
    }

    private static DateTime ToDateTime(object value) => value switch
    {
        DateTime dt => TruncateToMicroseconds(dt),
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset dto => TruncateToMicroseconds(dto.UtcDateTime),
        _ => throw new ArgumentException($"Unsupported date value type {value.GetType().Name}.", nameof(value)),
    };

    private static DateTime ToUtc(object value) => value switch
    {
        DateTimeOffset dto => TruncateToMicroseconds(dto.UtcDateTime),
        // A DateTime here is already UTC (Npgsql returns timestamptz as Kind=Utc; Oracle values are converted by the reader).
        DateTime dt => TruncateToMicroseconds(dt),
        _ => throw new ArgumentException($"Unsupported instant value type {value.GetType().Name}.", nameof(value)),
    };

    private static DateTime TruncateToMicroseconds(DateTime dt) => new(dt.Ticks - (dt.Ticks % 10), dt.Kind);

    private static string? EmptyAware(string s, CanonicalOptions options) =>
        s.Length == 0 && options.EmptyStrings == EmptyStringPolicy.AsNull ? null : s;

    /// <summary>Oracle blank-padded semantics: trailing blanks are not data, and an all-blank CHAR trims to NULL in Oracle.</summary>
    private static string? FixedChar(string s)
    {
        var trimmed = s.TrimEnd(' ');
        return trimmed.Length == 0 ? null : trimmed;
    }
}
