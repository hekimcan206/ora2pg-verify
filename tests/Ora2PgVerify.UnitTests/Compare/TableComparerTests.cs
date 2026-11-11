using System.Globalization;
using FsCheck;
using FsCheck.Fluent;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.UnitTests.Support;
using CompareOptions = Ora2PgVerify.Compare.CompareOptions;

namespace Ora2PgVerify.UnitTests.Compare;

public sealed class TableComparerTests
{
    private static Task<TableComparison> Run(TableSpec spec, List<object?[]> source, List<object?[]> target, CompareOptions? o = null) =>
        new TableComparer(o ?? new CompareOptions { ChunkRows = 200, LeafRows = 20 })
            .CompareAsync(spec, new InMemoryRowSource("Oracle", source), new InMemoryRowSource("PostgreSQL", target), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Equal_tables_match_although_padding_and_offsets_are_stored_differently()
    {
        var result = await Run(TestSpecs.Orders, TestRows.OracleOrders(2_000), TestRows.PostgresOrders(2_000));

        result.IsEqual.Should().BeTrue();
        result.Chunking.Should().Be(ChunkingMode.Range);
        result.TopLevelChunks.Should().BeGreaterThan(5);
        result.MismatchedTopLevelChunks.Should().Be(0);
        result.Source.Queries.Should().Be(2, "key stats plus one grouped fingerprint scan, nothing else when everything matches");
    }

    [Fact]
    public async Task Each_kind_of_difference_is_reported_at_the_exact_row_with_the_column_named()
    {
        var src = TestRows.OracleOrders(3_000);
        var tgt = TestRows.PostgresOrders(3_000);
        tgt.RemoveAll(r => (long)r[0]! == 17);                                         // missing
        tgt.Add(TestRows.With(tgt[5], 0, 9_999L));                                       // extra, beyond Oracle's MAX(key)
        var i = tgt.FindIndex(r => (long)r[0]! == 1_500);
        tgt[i] = TestRows.With(tgt[i], 4, (decimal)tgt[i][4]! + 0.01m);                   // amount drift
        var j = tgt.FindIndex(r => (long)r[0]! == 2_222);
        tgt[j] = TestRows.With(tgt[j], 2, (string)tgt[j][2]! + " ");                     // trailing space

        var result = await Run(TestSpecs.Orders, src, tgt);

        result.Diffs.Select(d => (d.Key, d.Kind)).Should().BeEquivalentTo(
        [
            ("17", DiffKind.MissingInTarget), ("1500", DiffKind.Changed), ("2222", DiffKind.Changed), ("9999", DiffKind.ExtraInTarget),
        ]);
        result.Diffs.Single(d => d.Key == "1500").Columns.Should().ContainSingle()
            .Which.Should().Match<ColumnDiff>(c => c.Column == "amount" && c.Difference == ValueDifference.NumericDrift && c.Detail == "+0.01");
        result.Diffs.Single(d => d.Key == "2222").Columns.Single().Difference.Should().Be(ValueDifference.TrailingWhitespace);
        result.Diffs.Single(d => d.Key == "1500").Columns.Single().SourceValue.Should().BeNull("values are opt-in");
    }

    [Fact]
    public async Task Values_are_only_reported_when_asked_for()
    {
        var src = TestRows.OracleOrders(100);
        var tgt = TestRows.PostgresOrders(100);
        tgt[10] = TestRows.With(tgt[10], 4, 1.5m);
        var result = await Run(TestSpecs.Orders, src, tgt, new CompareOptions { IncludeValues = true });
        var col = result.Diffs.Single().Columns.Single();
        col.TargetValue.Should().Be("1.5");
        col.SourceValue.Should().Be(Canonicalizer.Value(CanonicalKind.Number, src[10][4], CanonicalOptions.Default));
    }

    [Fact]
    public async Task Database_side_transfers_a_fraction_of_the_rows_client_side_streams()
    {
        var src = TestRows.OracleOrders(5_000);
        var tgt = TestRows.PostgresOrders(5_000);
        tgt[4_321] = TestRows.With(tgt[4_321], 3, "changed note");

        var db = await Run(TestSpecs.Orders, src, tgt, new CompareOptions { ChunkRows = 500, LeafRows = 50 });
        var client = await Run(TestSpecs.Orders, src, tgt, new CompareOptions { Strategy = CompareStrategy.ClientSide });

        db.Diffs.Select(d => d.Key).Should().Equal(client.Diffs.Select(d => d.Key));
        db.Diffs.Single().Columns.Single().Column.Should().Be("note");
        client.Diffs.Single().Columns.Single().Column.Should().Be("note");
        (db.Source.RowsTransferred + db.Target.RowsTransferred).Should().BeLessThan((client.Source.RowsTransferred + client.Target.RowsTransferred) / 20);
    }

    [Fact]
    public async Task One_empty_side_reports_every_row()
    {
        var result = await Run(TestSpecs.Orders, TestRows.OracleOrders(300), []);
        result.Diffs.Should().HaveCount(300).And.OnlyContain(d => d.Kind == DiffKind.MissingInTarget);
    }

    [Fact]
    public async Task Both_sides_empty_is_equal()
    {
        (await Run(TestSpecs.Orders, [], [])).IsEqual.Should().BeTrue();
        (await Run(TestSpecs.Customers, [], [])).IsEqual.Should().BeTrue();
    }

    [Fact]
    public async Task Collection_stops_at_max_diffs_and_says_so()
    {
        var result = await Run(TestSpecs.Orders, TestRows.OracleOrders(500), [], new CompareOptions { MaxDiffs = 25 });
        result.Diffs.Should().HaveCount(25);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Composite_text_keys_use_hash_buckets_and_find_clob_edits()
    {
        var src = TestRows.Documents(400);
        var tgt = TestRows.Documents(400);
        var body = (string)tgt[123][2]!;
        tgt[123] = TestRows.With(tgt[123], 2, body[..100] + "X" + body[101..]);
        tgt.RemoveAt(7);

        var result = await Run(TestSpecs.Documents, src, tgt, new CompareOptions { ChunkRows = 50 });

        TestSpecs.Documents.Chunking.Should().Be(ChunkingMode.HashBucket);
        result.Diffs.Should().HaveCount(2);
        result.Diffs.Single(d => d.Kind == DiffKind.Changed).Columns.Single().Difference.Should().Be(ValueDifference.DigestDiffers);
        result.Diffs.Single(d => d.Kind == DiffKind.Changed).DisplayKey.Should().Be($"{src[123][0]}|{src[123][1]}");
    }

    /// <summary>
    /// The core guarantee: for any set of deletions, insertions and value changes, and any chunk, leaf and bisection
    /// settings, the comparison reports exactly the mutated rows (no misses, no extras), with both strategies agreeing.
    /// </summary>
    [FsCheck.Xunit.Property(MaxTest = 60)]
    public Property Reports_exactly_the_mutated_rows_for_any_chunking() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(0, 1_199).ArrayOf().Select(a => a.Distinct().Take(40).ToArray())),
            Arb.From(from c in Gen.Elements(7L, 64L, 300L, 5_000L) from l in Gen.Elements(1L, 10L, 100L) from f in Gen.Elements(2, 4, 16) select (Chunk: c, Leaf: l, Factor: f)),
            (positions, cfg) =>
            {
                var src = TestRows.OracleOrders(1_200, seed: 3);
                var tgt = TestRows.PostgresOrders(1_200, seed: 3);
                var expected = new Dictionary<string, DiffKind>();
                foreach (var p in positions)
                {
                    var key = (long)tgt[p][0]!;
                    switch (p % 3)
                    {
                        case 0:
                            tgt[p] = TestRows.With(tgt[p], 5, (decimal)tgt[p][5]! + 0.0001m);
                            expected[key.ToString(CultureInfo.InvariantCulture)] = DiffKind.Changed;
                            break;
                        case 1:
                            tgt[p] = TestRows.With(tgt[p], 0, -key); // the row "moves": missing at key, extra at -key
                            expected[key.ToString(CultureInfo.InvariantCulture)] = DiffKind.MissingInTarget;
                            expected[(-key).ToString(CultureInfo.InvariantCulture)] = DiffKind.ExtraInTarget;
                            break;
                        default:
                            tgt[p] = TestRows.With(tgt[p], 3, tgt[p][3] is null ? "" : null);
                            expected[key.ToString(CultureInfo.InvariantCulture)] = DiffKind.Changed;
                            break;
                    }
                }

                var options = new CompareOptions { ChunkRows = cfg.Chunk, LeafRows = cfg.Leaf, BisectionFactor = cfg.Factor };
                var db = Run(TestSpecs.Orders, src, tgt, options).GetAwaiter().GetResult();
                var client = Run(TestSpecs.Orders, src, tgt, options with { Strategy = CompareStrategy.ClientSide }).GetAwaiter().GetResult();
                var actual = db.Diffs.ToDictionary(d => d.Key, d => d.Kind);
                var same = actual.Count == expected.Count && expected.All(e => actual.TryGetValue(e.Key, out var k) && k == e.Value);
                var agree = client.Diffs.Select(d => (d.Key, d.Kind)).OrderBy(x => x.Key, StringComparer.Ordinal)
                    .SequenceEqual(db.Diffs.Select(d => (d.Key, d.Kind)).OrderBy(x => x.Key, StringComparer.Ordinal));
                return (same && agree).Label($"expected {expected.Count}, db {actual.Count}, client {client.Diffs.Count}");
            });

    [FsCheck.Xunit.Property(MaxTest = 40)]
    public Property Hash_buckets_report_exactly_the_mutated_text_keyed_rows() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(0, 399).ArrayOf().Select(a => a.Distinct().Take(25).ToArray())),
            Arb.From(Gen.Elements(3L, 40L, 1_000L)),
            (positions, chunk) =>
            {
                var src = TestRows.Customers(400, seed: 5);
                var tgt = TestRows.Customers(400, seed: 5);
                var expected = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in positions)
                {
                    expected.Add(Canonicalizer.Encode((string)src[p][0]!));
                    tgt[p] = TestRows.With(tgt[p], 1, ((string)tgt[p][1]!).ToUpperInvariant() + "!");
                }

                var result = Run(TestSpecs.Customers, src, tgt, new CompareOptions { ChunkRows = chunk }).GetAwaiter().GetResult();
                return result.Diffs.Select(d => d.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expected)
                       && result.Diffs.All(d => d.Kind == DiffKind.Changed && d.Columns.Single().Column == "full_name");
            });
}
