namespace Ora2PgVerify.Model;

/// <summary>A column as read from ALL_TAB_COLUMNS or information_schema.columns.</summary>
/// <param name="Name">Name exactly as stored in the catalog.</param>
/// <param name="DataType">Catalog type name, e.g. <c>NUMBER</c>, <c>TIMESTAMP(6) WITH TIME ZONE</c>, <c>character varying</c>.</param>
/// <param name="CharLength">Character length for text types.</param>
/// <param name="ByteLength">Byte length (Oracle DATA_LENGTH) for text types.</param>
/// <param name="Precision">Numeric precision, or fractional-second precision for timestamps.</param>
/// <param name="Scale">Numeric scale.</param>
/// <param name="Nullable">Whether NULL is allowed.</param>
public sealed record CatalogColumn(
    string Name,
    string DataType,
    int? CharLength,
    int? ByteLength,
    int? Precision,
    int? Scale,
    bool Nullable);

/// <summary>Table metadata from one side.</summary>
public sealed record CatalogTable(string Schema, string Name, IReadOnlyList<CatalogColumn> Columns, IReadOnlyList<string> PrimaryKey);
