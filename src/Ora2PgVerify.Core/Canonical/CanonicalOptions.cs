namespace Ora2PgVerify.Canonical;

/// <summary>What an empty string on the PostgreSQL side means.</summary>
public enum EmptyStringPolicy
{
    /// <summary>
    /// Default. Oracle cannot store '' (it becomes NULL), so a '' on the target is a real behavioural difference:
    /// <c>WHERE col IS NULL</c> no longer finds the row. Reported as a difference.
    /// </summary>
    Distinct,

    /// <summary>Treat '' on the target as NULL (the application has been fixed to cope with both).</summary>
    AsNull,
}

/// <summary>Row digest algorithm. Both are available on both engines without extensions.</summary>
public enum RowHashAlgorithm
{
    /// <summary>MD5: <c>STANDARD_HASH(.., 'MD5')</c> / <c>md5()</c>. Integrity check against accidents, not adversaries.</summary>
    Md5,

    /// <summary>SHA-256: <c>STANDARD_HASH(.., 'SHA256')</c> / <c>sha256()</c> (PostgreSQL 11+).</summary>
    Sha256,
}

public sealed record CanonicalOptions(EmptyStringPolicy EmptyStrings = EmptyStringPolicy.Distinct, RowHashAlgorithm Hash = RowHashAlgorithm.Md5)
{
    public static CanonicalOptions Default { get; } = new();

    /// <summary>Length of a hex digest for the configured algorithm.</summary>
    public int DigestHexLength => Hash == RowHashAlgorithm.Md5 ? 32 : 64;
}
