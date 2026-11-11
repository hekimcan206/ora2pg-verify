namespace Ora2PgVerify.Scan;

/// <summary>A stored PL/SQL unit, re-assembled from ALL_SOURCE / DBA_SOURCE lines.</summary>
public sealed record SourceObject(string Owner, string Name, string Type, string Status, string Text);

public sealed record SequenceFact(string Owner, string Name, decimal MaxValue, bool Cycle, long CacheSize);

public sealed record SynonymFact(string Owner, string Name, string TargetOwner, string TargetName, string? DbLink);

public sealed record DbLinkFact(string Owner, string Name);

public sealed record ColumnFact(string Owner, string Table, string Column, string DataType, int? Precision, int? Scale);

public sealed record TableFact(string Owner, string Name, bool Temporary);

public sealed record MaterializedViewFact(string Owner, string Name, string RefreshMethod, string RefreshMode);

/// <summary>Everything the incompatibility scanner reads from the Oracle dictionary for one schema.</summary>
public sealed record SchemaFacts(
    string Schema,
    IReadOnlyList<SourceObject> Sources,
    IReadOnlyList<SequenceFact> Sequences,
    IReadOnlyList<SynonymFact> Synonyms,
    IReadOnlyList<DbLinkFact> DbLinks,
    IReadOnlyList<ColumnFact> Columns,
    IReadOnlyList<TableFact> Tables,
    IReadOnlyList<MaterializedViewFact> MaterializedViews);
