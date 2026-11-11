namespace Ora2PgVerify.Model;

/// <summary>Pairs the Oracle and PostgreSQL definitions of a table into a <see cref="TableSpec"/>.</summary>
public static class TableSpecBuilder
{
    public static (TableSpec? Spec, IReadOnlyList<MappingFinding> Findings) Build(string name, CatalogTable oracle, CatalogTable postgres)
    {
        ArgumentNullException.ThrowIfNull(oracle);
        ArgumentNullException.ThrowIfNull(postgres);
        var findings = new List<MappingFinding>();
        var pgByName = postgres.Columns.ToDictionary(c => c.Name.ToLowerInvariant(), StringComparer.Ordinal);
        var oraNames = oracle.Columns.Select(c => c.Name.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var pairs = new List<ColumnPair>();

        foreach (var o in oracle.Columns)
        {
            var key = o.Name.ToLowerInvariant();
            if (!pgByName.TryGetValue(key, out var p))
            {
                findings.Add(new MappingFinding(name, key, Severity.High, "COLUMN_MISSING_IN_TARGET", $"Oracle column {o.Name} has no PostgreSQL counterpart."));
                continue;
            }

            var (pair, f) = TypeMapper.Map(name, o, p);
            findings.AddRange(f);
            if (pair is not null)
            {
                pairs.Add(pair);
            }
        }

        foreach (var p in postgres.Columns.Where(p => !oraNames.Contains(p.Name.ToLowerInvariant())))
        {
            findings.Add(new MappingFinding(name, p.Name.ToLowerInvariant(), Severity.Low, "COLUMN_ONLY_IN_TARGET", $"PostgreSQL column {p.Name} does not exist in Oracle; not compared."));
        }

        var keys = oracle.PrimaryKey.Select(k => k.ToLowerInvariant()).ToList();
        if (keys.Count == 0)
        {
            findings.Add(new MappingFinding(name, "", Severity.High, "NO_PRIMARY_KEY", "No primary key in Oracle: rows cannot be matched one to one, the table is not compared."));
            return (null, findings);
        }

        var pgKeys = postgres.PrimaryKey.Select(k => k.ToLowerInvariant()).ToList();
        if (!keys.SequenceEqual(pgKeys))
        {
            findings.Add(new MappingFinding(name, string.Join(",", keys), Severity.Medium, "PRIMARY_KEY_DIFFERS",
                $"Primary key differs: Oracle ({string.Join(", ", keys)}), PostgreSQL ({string.Join(", ", pgKeys)}). Rows are matched on the Oracle key."));
        }

        if (keys.Any(k => pairs.All(p => p.Name != k)))
        {
            findings.Add(new MappingFinding(name, string.Join(",", keys), Severity.High, "KEY_NOT_COMPARABLE", "A key column is missing or of an unsupported type; the table is not compared."));
            return (null, findings);
        }

        // Key columns first, in key order, so the canonical key prefix of a row string is stable.
        var ordered = keys.Select(k => pairs.First(p => p.Name == k)).Concat(pairs.Where(p => !keys.Contains(p.Name))).ToList();
        return (new TableSpec(name, oracle.Schema, oracle.Name, postgres.Schema, postgres.Name, keys, ordered), findings);
    }
}
