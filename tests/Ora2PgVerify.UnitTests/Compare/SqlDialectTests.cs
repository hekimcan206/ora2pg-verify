using System.Text;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Sql;
using Ora2PgVerify.UnitTests.Support;

namespace Ora2PgVerify.UnitTests.Compare;

/// <summary>The generated pushdown SQL is reviewed as a golden file: any change to it is a deliberate, visible diff.</summary>
public sealed class SqlDialectTests
{
    private static string AllQueries(SqlDialect d)
    {
        var o = CanonicalOptions.Default;
        var sb = new StringBuilder();
        sb.Append("-- orders: key stats\n").Append(d.KeyStatsQuery(TestSpecs.Orders)).Append("\n\n")
          .Append("-- orders: range chunk fingerprints\n").Append(d.RangeChunkQuery(TestSpecs.Orders, o)).Append("\n\n")
          .Append("-- orders: leaf row digests (2 ranges)\n").Append(d.RangeLeafQuery(TestSpecs.Orders, o, 2)).Append("\n\n")
          .Append("-- orders: encoded fields by key (2 keys)\n").Append(d.FieldsByKeyQuery(TestSpecs.Orders, o, 2)).Append("\n\n")
          .Append("-- order_documents: bucket fingerprints, SHA-256\n").Append(d.BucketChunkQuery(TestSpecs.Documents, o with { Hash = RowHashAlgorithm.Sha256 })).Append("\n\n")
          .Append("-- order_documents: bucket leaf (1 bucket), empty strings as NULL\n").Append(d.BucketLeafQuery(TestSpecs.Documents, o with { EmptyStrings = EmptyStringPolicy.AsNull }, 1)).Append("\n\n")
          .Append("-- customers: client-side raw rows\n").Append(d.RawRowsQuery(TestSpecs.Customers)).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void Oracle_pushdown_sql_matches_golden() => Golden.Verify("oracle-pushdown.sql", AllQueries(OracleDialect.Instance));

    [Fact]
    public void Postgres_pushdown_sql_matches_golden() => Golden.Verify("postgres-pushdown.sql", AllQueries(PostgresDialect.Instance));

    [Theory]
    [InlineData("orders; DROP TABLE x")]
    [InlineData("a\"b")]
    [InlineData("")]
    [InlineData("1abc")]
    public void Unusual_identifiers_are_refused_not_escaped(string identifier) =>
        FluentActions.Invoking(() => SqlDialect.Quote(identifier)).Should().Throw<ArgumentException>();

    [Fact]
    public void Values_are_bound_never_inlined()
    {
        var sql = OracleDialect.Instance.FieldsByKeyQuery(TestSpecs.Orders, CanonicalOptions.Default, 3);
        sql.Should().Contain(":k0, :k1, :k2");
        PostgresDialect.Instance.RangeChunkQuery(TestSpecs.Orders, CanonicalOptions.Default).Should().Contain("BETWEEN @lo AND @hi");
    }
}
