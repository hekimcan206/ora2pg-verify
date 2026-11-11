using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Model;
using Ora2PgVerify.Sql;

namespace Ora2PgVerify.Compare;

/// <summary>What an engine has to provide so the generic ADO.NET row source can run on it.</summary>
public abstract class EngineAdapter
{
    public abstract SqlDialect Dialect { get; }

    /// <summary>Opens a connection with deterministic session settings (time zone, NLS).</summary>
    public abstract Task<DbConnection> OpenAsync(CancellationToken ct);

    /// <summary>Starts a read-only snapshot so every query of one comparison sees the same data.</summary>
    public abstract Task<DbTransaction?> BeginSnapshotAsync(DbConnection connection, CancellationToken ct);

    public abstract void AddParameter(DbCommand command, string name, object value);

    public virtual void Configure(DbCommand command)
    {
    }

    public virtual void Configure(DbDataReader reader)
    {
    }

    /// <summary>Reads a raw column value for client-side canonicalisation (types as <see cref="Canonicalizer"/> expects).</summary>
    public abstract object? ReadRaw(DbDataReader reader, int ordinal, ColumnPair column);

    public virtual long ReadInt64(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public virtual decimal ReadDecimal(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }
}

/// <summary>Row source over any ADO.NET provider, driven by a <see cref="SqlDialect"/>.</summary>
public sealed class DbRowSource(EngineAdapter adapter, CanonicalOptions options) : IRowSource
{
    public string EngineName => adapter.Dialect.EngineName;

    public async Task<IRowSourceSession> OpenSessionAsync(CancellationToken ct)
    {
        var connection = await adapter.OpenAsync(ct);
        try
        {
            var tx = await adapter.BeginSnapshotAsync(connection, ct);
            return new Session(adapter, options, connection, tx);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Session(EngineAdapter adapter, CanonicalOptions options, DbConnection connection, DbTransaction? tx) : IRowSourceSession
    {
        private SqlDialect D => adapter.Dialect;

        public string EngineName => D.EngineName;

        public SideMetrics Metrics { get; } = new();

        public async Task<KeyStats> GetKeyStatsAsync(TableSpec table, CancellationToken ct)
        {
            var started = Stopwatch.GetTimestamp();
            await using var cmd = Command(D.KeyStatsQuery(table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var stats = new KeyStats(
                adapter.ReadInt64(r, 0),
                r.IsDBNull(1) ? null : adapter.ReadInt64(r, 1),
                r.IsDBNull(2) ? null : adapter.ReadInt64(r, 2));
            Metrics.AddQuery(1, Stopwatch.GetElapsedTime(started));
            return stats;
        }

        public Task<IReadOnlyDictionary<long, ChunkHash>> GetRangeChunksAsync(TableSpec table, KeyRange range, long width, CancellationToken ct)
        {
            var cmd = Command(D.RangeChunkQuery(table, options));
            adapter.AddParameter(cmd, "lo", range.Lo);
            adapter.AddParameter(cmd, "hi", range.Hi);
            adapter.AddParameter(cmd, "w", width);
            return ChunksAsync(cmd, ct);
        }

        public Task<IReadOnlyDictionary<long, ChunkHash>> GetBucketChunksAsync(TableSpec table, long buckets, CancellationToken ct)
        {
            var cmd = Command(D.BucketChunkQuery(table, options));
            adapter.AddParameter(cmd, "n", buckets);
            return ChunksAsync(cmd, ct);
        }

        private async Task<IReadOnlyDictionary<long, ChunkHash>> ChunksAsync(DbCommand cmd, CancellationToken ct)
        {
            var started = Stopwatch.GetTimestamp();
            await using var _ = cmd;
            var result = new Dictionary<long, ChunkHash>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            adapter.Configure(r);
            while (await r.ReadAsync(ct))
            {
                result[adapter.ReadInt64(r, 0)] = new ChunkHash(adapter.ReadInt64(r, 1), adapter.ReadDecimal(r, 2));
            }

            Metrics.AddQuery(result.Count, Stopwatch.GetElapsedTime(started));
            return result;
        }

        public Task<IReadOnlyList<RowHash>> GetRangeRowHashesAsync(TableSpec table, IReadOnlyList<KeyRange> ranges, CancellationToken ct)
        {
            var cmd = Command(D.RangeLeafQuery(table, options, ranges.Count));
            for (var i = 0; i < ranges.Count; i++)
            {
                adapter.AddParameter(cmd, Inv($"lo{i}"), ranges[i].Lo);
                adapter.AddParameter(cmd, Inv($"hi{i}"), ranges[i].Hi);
            }

            return RowHashesAsync(cmd, numericKey: true, ct);
        }

        public Task<IReadOnlyList<RowHash>> GetBucketRowHashesAsync(TableSpec table, long buckets, IReadOnlyList<long> bucketIds, CancellationToken ct)
        {
            var cmd = Command(D.BucketLeafQuery(table, options, bucketIds.Count));
            adapter.AddParameter(cmd, "n", buckets);
            for (var i = 0; i < bucketIds.Count; i++)
            {
                adapter.AddParameter(cmd, Inv($"b{i}"), bucketIds[i]);
            }

            return RowHashesAsync(cmd, numericKey: false, ct);
        }

        private async Task<IReadOnlyList<RowHash>> RowHashesAsync(DbCommand cmd, bool numericKey, CancellationToken ct)
        {
            var started = Stopwatch.GetTimestamp();
            await using var _ = cmd;
            var rows = new List<RowHash>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            adapter.Configure(r);
            while (await r.ReadAsync(ct))
            {
                var key = numericKey ? adapter.ReadInt64(r, 0).ToString(CultureInfo.InvariantCulture) : r.GetString(0);
                rows.Add(new RowHash(key, r.GetString(1)));
            }

            Metrics.AddQuery(rows.Count, Stopwatch.GetElapsedTime(started));
            return rows;
        }

        public async Task<IReadOnlyList<RowFields>> GetRowFieldsAsync(TableSpec table, IReadOnlyList<string> keys, bool clientSide, CancellationToken ct)
        {
            var started = Stopwatch.GetTimestamp();
            if (keys.Count == 0)
            {
                return [];
            }

            var numeric = table.Chunking == ChunkingMode.Range;
            await using var cmd = Command(clientSide ? D.RawRowsByKeyQuery(table, options, keys.Count) : D.FieldsByKeyQuery(table, options, keys.Count));
            for (var i = 0; i < keys.Count; i++)
            {
                adapter.AddParameter(cmd, Inv($"k{i}"), numeric ? long.Parse(keys[i], CultureInfo.InvariantCulture) : keys[i]);
            }

            var rows = new List<RowFields>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            adapter.Configure(r);
            while (await r.ReadAsync(ct))
            {
                if (clientSide)
                {
                    var fields = Canonicalise(r, table);
                    rows.Add(new RowFields(KeyOf(fields, table), fields));
                }
                else
                {
                    var key = numeric ? adapter.ReadInt64(r, 0).ToString(CultureInfo.InvariantCulture) : r.GetString(0);
                    var fields = new string[table.Columns.Count];
                    for (var c = 0; c < fields.Length; c++)
                    {
                        fields[c] = r.GetString(c + 1);
                    }

                    rows.Add(new RowFields(key, fields));
                }
            }

            Metrics.AddQuery(rows.Count, Stopwatch.GetElapsedTime(started));
            return rows;
        }

        public async IAsyncEnumerable<RowHash> StreamClientRowHashesAsync(TableSpec table, [EnumeratorCancellation] CancellationToken ct)
        {
            var started = Stopwatch.GetTimestamp();
            await using var cmd = Command(D.RawRowsQuery(table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            adapter.Configure(r);
            long n = 0;
            while (await r.ReadAsync(ct))
            {
                var fields = Canonicalise(r, table);
                n++;
                yield return new RowHash(KeyOf(fields, table), Canonicalizer.HexDigest(Canonicalizer.Row(fields), options.Hash));
            }

            Metrics.AddQuery(n, Stopwatch.GetElapsedTime(started));
        }

        private string[] Canonicalise(DbDataReader r, TableSpec table)
        {
            var fields = new string[table.Columns.Count];
            for (var c = 0; c < fields.Length; c++)
            {
                var column = table.Columns[c];
                fields[c] = Canonicalizer.Field(column.Kind, adapter.ReadRaw(r, c, column), options);
            }

            return fields;
        }

        /// <summary>Range mode keys are the integer itself; otherwise the encoded key fields (key columns come first).</summary>
        private static string KeyOf(string[] fields, TableSpec table) =>
            table.Chunking == ChunkingMode.Range
                ? DifferenceClassifier.Decode(fields[0]) ?? throw new InvalidOperationException("NULL primary key value.")
                : string.Concat(fields.Take(table.KeyColumns.Count));

        private DbCommand Command(string sql)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = tx;
            cmd.CommandTimeout = 0;
            adapter.Configure(cmd);
            return cmd;
        }

        private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            if (tx is not null)
            {
                await tx.RollbackAsync();
                await tx.DisposeAsync();
            }

            await connection.DisposeAsync();
        }
    }
}
