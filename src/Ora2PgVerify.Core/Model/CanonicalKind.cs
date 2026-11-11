namespace Ora2PgVerify.Model;

/// <summary>
/// The comparison semantics of a column pair. The kind is decided from the Oracle (source) type, because the
/// question being answered is "does PostgreSQL still hold what Oracle meant?".
/// </summary>
public enum CanonicalKind
{
    /// <summary>NUMBER / numeric / integer types: compared as exact decimals with trailing fractional zeros removed.</summary>
    Number,

    /// <summary>Oracle DATE (second precision, no time zone). A fractional second on the target side is kept, so it shows up.</summary>
    Date,

    /// <summary>TIMESTAMP without time zone, microsecond precision.</summary>
    Timestamp,

    /// <summary>TIMESTAMP WITH (LOCAL) TIME ZONE: compared as UTC instants; the original offset is not part of the value.</summary>
    TimestampTz,

    /// <summary>VARCHAR2 / varchar / text: compared byte for byte (UTF-8), trailing spaces significant.</summary>
    Text,

    /// <summary>CHAR(n): blank-padded semantics, so trailing spaces are removed on both sides before comparing.</summary>
    FixedChar,

    /// <summary>CLOB or very wide VARCHAR2: compared through a digest of the UTF-8 text, because the value cannot travel in a row string.</summary>
    LargeText,
}
