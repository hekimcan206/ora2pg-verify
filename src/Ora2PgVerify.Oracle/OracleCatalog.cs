using System.Globalization;
using System.Text;
using Ora2PgVerify.Model;
using Ora2PgVerify.Scan;
using Oracle.ManagedDataAccess.Client;

namespace Ora2PgVerify.Oracle;

/// <summary>Reads table definitions and the scan facts from the Oracle data dictionary (read-only queries, bound owner names).</summary>
public sealed class OracleCatalog(string connectionString)
{
    public async Task<IReadOnlyList<string>> ListTablesAsync(string owner, CancellationToken ct)
    {
        await using var c = await OracleEngine.OpenOracleAsync(connectionString, ct);
        return await ListAsync(c, "SELECT TABLE_NAME FROM ALL_TABLES WHERE OWNER = :o AND TEMPORARY = 'N' AND NESTED = 'NO' AND SECONDARY = 'N' " +
                                  "AND TABLE_NAME NOT IN (SELECT MVIEW_NAME FROM ALL_MVIEWS WHERE OWNER = :o) ORDER BY TABLE_NAME", owner, ct);
    }

    public async Task<CatalogTable?> ReadTableAsync(string owner, string table, CancellationToken ct)
    {
        await using var c = await OracleEngine.OpenOracleAsync(connectionString, ct);
        var columns = new List<CatalogColumn>();
        await using (var cmd = Cmd(c, "SELECT COLUMN_NAME, DATA_TYPE, CHAR_LENGTH, DATA_LENGTH, DATA_PRECISION, DATA_SCALE, NULLABLE " +
                                      "FROM ALL_TAB_COLUMNS WHERE OWNER = :o AND TABLE_NAME = :t ORDER BY COLUMN_ID", owner, table))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var type = r.GetString(1);
                var isTimestamp = type.StartsWith("TIMESTAMP", StringComparison.Ordinal);
                int? charLen = r.IsDBNull(2) || r.GetInt32(2) == 0 ? null : r.GetInt32(2);
                columns.Add(new CatalogColumn(
                    r.GetString(0),
                    type,
                    charLen,
                    r.IsDBNull(3) ? null : r.GetInt32(3),
                    // Oracle reports the fractional-second precision of TIMESTAMP(n) in DATA_SCALE.
                    isTimestamp ? (r.IsDBNull(5) ? null : r.GetInt32(5)) : (r.IsDBNull(4) ? null : r.GetInt32(4)),
                    isTimestamp ? null : (r.IsDBNull(5) ? null : r.GetInt32(5)),
                    r.GetString(6) == "Y"));
            }
        }

        if (columns.Count == 0)
        {
            return null;
        }

        var pk = await ListAsync(c, "SELECT cc.COLUMN_NAME FROM ALL_CONSTRAINTS k JOIN ALL_CONS_COLUMNS cc ON cc.OWNER = k.OWNER AND cc.CONSTRAINT_NAME = k.CONSTRAINT_NAME " +
                                    "WHERE k.OWNER = :o AND k.TABLE_NAME = :t AND k.CONSTRAINT_TYPE = 'P' ORDER BY cc.POSITION", owner, ct, table);
        return new CatalogTable(owner, table, columns, pk);
    }

    /// <summary>
    /// Reads stored source and dictionary facts for the scan. DBA_SOURCE is used when visible (SELECT_CATALOG_ROLE),
    /// otherwise ALL_SOURCE, which only shows code the account owns or may execute.
    /// </summary>
    public async Task<(SchemaFacts Facts, string SourceView)> ReadSchemaFactsAsync(string owner, CancellationToken ct)
    {
        await using var c = await OracleEngine.OpenOracleAsync(connectionString, ct);
        var prefix = await CanSeeAsync(c, "DBA_SOURCE", ct) ? "DBA" : "ALL";

        var status = new Dictionary<(string, string), string>();
        await using (var cmd = Cmd(c, $"SELECT OBJECT_NAME, OBJECT_TYPE, STATUS FROM {prefix}_OBJECTS WHERE OWNER = :o", owner))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                status[(r.GetString(0), r.GetString(1))] = r.GetString(2);
            }
        }

        var sources = new List<SourceObject>();
        await using (var cmd = Cmd(c, $"SELECT NAME, TYPE, TEXT FROM {prefix}_SOURCE WHERE OWNER = :o ORDER BY TYPE, NAME, LINE", owner))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            string? name = null, type = null;
            var sb = new StringBuilder();
            void Flush()
            {
                if (name is not null)
                {
                    sources.Add(new SourceObject(owner, name, type!, status.GetValueOrDefault((name, type!), "VALID"), sb.ToString().TrimEnd('\n')));
                }

                sb.Clear();
            }

            while (await r.ReadAsync(ct))
            {
                var n = r.GetString(0);
                var t = r.GetString(1);
                if (n != name || t != type)
                {
                    Flush();
                    name = n;
                    type = t;
                }

                // Each ALL_SOURCE line ends with its own newline; NULL is an empty line.
                var text = r.IsDBNull(2) ? "\n" : r.GetString(2);
                sb.Append(text.EndsWith('\n') ? text : text + "\n");
            }

            Flush();
        }

        // View text lives in a LONG column, outside ALL_SOURCE.
        await using (var cmd = Cmd(c, $"SELECT VIEW_NAME, TEXT FROM {prefix}_VIEWS WHERE OWNER = :o ORDER BY VIEW_NAME", owner))
        {
            cmd.InitialLONGFetchSize = -1;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var n = r.GetString(0);
                sources.Add(new SourceObject(owner, n, "VIEW", status.GetValueOrDefault((n, "VIEW"), "VALID"), r.IsDBNull(1) ? string.Empty : r.GetString(1)));
            }
        }

        var sequences = new List<SequenceFact>();
        await using (var cmd = Cmd(c, $"SELECT SEQUENCE_NAME, MAX_VALUE, CYCLE_FLAG, CACHE_SIZE FROM {prefix}_SEQUENCES WHERE SEQUENCE_OWNER = :o ORDER BY SEQUENCE_NAME", owner))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                sequences.Add(new SequenceFact(owner, r.GetString(0), r.GetOracleDecimal(1).Value, r.GetString(2) == "Y", (long)r.GetOracleDecimal(3).Value));
            }
        }

        var synonyms = new List<SynonymFact>();
        await using (var cmd = Cmd(c, $"SELECT SYNONYM_NAME, TABLE_OWNER, TABLE_NAME, DB_LINK FROM {prefix}_SYNONYMS WHERE OWNER = :o ORDER BY SYNONYM_NAME", owner))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                synonyms.Add(new SynonymFact(owner, r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
            }
        }

        var links = (await ListAsync(c, $"SELECT DB_LINK FROM {prefix}_DB_LINKS WHERE OWNER = :o ORDER BY DB_LINK", owner, ct)).Select(l => new DbLinkFact(owner, l)).ToList();

        var tables = new List<TableFact>();
        await using (var cmd = Cmd(c, $"SELECT TABLE_NAME, TEMPORARY FROM {prefix}_TABLES WHERE OWNER = :o ORDER BY TABLE_NAME", owner))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                tables.Add(new TableFact(owner, r.GetString(0), r.GetString(1) == "Y"));
            }
        }

        var columns = new List<ColumnFact>();
        await using (var cmd = Cmd(c, $"SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.DATA_PRECISION, c.DATA_SCALE FROM {prefix}_TAB_COLUMNS c " +
                                      $"JOIN {prefix}_TABLES t ON t.OWNER = c.OWNER AND t.TABLE_NAME = c.TABLE_NAME WHERE c.OWNER = :o ORDER BY c.TABLE_NAME, c.COLUMN_ID", owner))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                columns.Add(new ColumnFact(owner, r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt32(4)));
            }
        }

        var mviews = new List<MaterializedViewFact>();
        await using (var cmd = Cmd(c, $"SELECT MVIEW_NAME, REFRESH_METHOD, REFRESH_MODE FROM {prefix}_MVIEWS WHERE OWNER = :o ORDER BY MVIEW_NAME", owner))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                mviews.Add(new MaterializedViewFact(owner, r.GetString(0), r.GetString(1), r.GetString(2)));
            }
        }

        return (new SchemaFacts(owner, sources, sequences, synonyms, links, columns, tables, mviews), prefix + "_SOURCE");
    }

    private static async Task<bool> CanSeeAsync(OracleConnection c, string view, CancellationToken ct)
    {
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = string.Create(CultureInfo.InvariantCulture, $"SELECT 1 FROM {view} WHERE ROWNUM = 1");
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (OracleException ex) when (ex.Number == 942)
        {
            return false;
        }
    }

    private static OracleCommand Cmd(OracleConnection c, string sql, string owner, string? table = null)
    {
        var cmd = c.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = sql;
        cmd.Parameters.Add(new OracleParameter("o", owner));
        if (table is not null)
        {
            cmd.Parameters.Add(new OracleParameter("t", table));
        }

        return cmd;
    }

    private static async Task<IReadOnlyList<string>> ListAsync(OracleConnection c, string sql, string owner, CancellationToken ct, string? table = null)
    {
        await using var cmd = Cmd(c, sql, owner, table);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct))
        {
            list.Add(r.GetString(0));
        }

        return list;
    }
}
