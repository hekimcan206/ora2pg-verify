namespace Ora2PgVerify.Model;

/// <summary>A schema-level observation about how one Oracle column was mapped to PostgreSQL.</summary>
public sealed record MappingFinding(string Table, string Column, Severity Severity, string Code, string Message);
