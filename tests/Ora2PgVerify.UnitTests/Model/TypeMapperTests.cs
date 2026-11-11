using Ora2PgVerify.Model;
using Ora2PgVerify.UnitTests.Support;

namespace Ora2PgVerify.UnitTests.Model;

public sealed class TypeMapperTests
{
    private static CatalogColumn Ora(string type, int? precision = null, int? scale = null, int? chars = null, int? bytes = null, bool nullable = true) =>
        new("C", type, chars, bytes, precision, scale, nullable);

    private static CatalogColumn Pg(string type, int? precision = null, int? scale = null, int? chars = null, bool nullable = true) =>
        new("c", type, chars, null, precision, scale, nullable);

    private static IReadOnlyList<string> Codes(CatalogColumn o, CatalogColumn p) => [.. TypeMapper.Map("t", o, p).Findings.Select(f => f.Code)];

    [Fact]
    public void Synthetic_schema_maps_to_the_expected_kinds_and_chunking()
    {
        TestSpecs.Orders.Columns.Select(c => (c.Name, c.Kind)).Should().Equal(
            ("order_id", CanonicalKind.Number), ("customer_code", CanonicalKind.FixedChar), ("status", CanonicalKind.Text), ("note", CanonicalKind.Text),
            ("amount", CanonicalKind.Number), ("discount_rate", CanonicalKind.Number), ("created_on", CanonicalKind.Date),
            ("shipped_at", CanonicalKind.Timestamp), ("updated_at", CanonicalKind.TimestampTz));
        TestSpecs.Orders.Chunking.Should().Be(ChunkingMode.Range);
        TestSpecs.Customers.Chunking.Should().Be(ChunkingMode.HashBucket);
        TestSpecs.Documents.Chunking.Should().Be(ChunkingMode.HashBucket);
        TestSpecs.Documents.Column("body").Kind.Should().Be(CanonicalKind.LargeText);
    }

    [Fact]
    public void Number_to_double_precision_is_high_severity() =>
        TypeMapper.Map("t", Ora("NUMBER", 20, 0), Pg("double precision")).Findings.Should().Contain(f => f.Code == "NUMBER_TO_FLOAT" && f.Severity == Severity.High);

    [Fact]
    public void Fraction_into_integer_and_reduced_scale_are_flagged()
    {
        Codes(Ora("NUMBER", 10, 2), Pg("bigint")).Should().Contain("NUMBER_FRACTION_TRUNCATED");
        Codes(Ora("NUMBER", 14, 4), Pg("numeric", 14, 2)).Should().Contain("NUMBER_SCALE_REDUCED");
        Codes(Ora("NUMBER", 12, 0), Pg("integer")).Should().Contain("NUMBER_INTEGER_OVERFLOW");
        Codes(Ora("NUMBER"), Pg("numeric")).Should().Contain("NUMBER_UNCONSTRAINED");
        Codes(Ora("NUMBER", 12, 0), Pg("bigint")).Should().BeEmpty();
    }

    [Fact]
    public void Oracle_date_to_postgres_date_warns_about_the_lost_time_of_day()
    {
        Codes(Ora("DATE"), Pg("date")).Should().Contain("DATE_TO_DATE");
        Codes(Ora("DATE"), Pg("timestamp without time zone", 0)).Should().BeEmpty();
        Codes(Ora("DATE"), Pg("timestamp without time zone", 6)).Should().Contain("DATE_TO_TIMESTAMP_FRACTION");
    }

    [Fact]
    public void Time_zone_handling_is_reported()
    {
        Codes(Ora("TIMESTAMP(6) WITH TIME ZONE", 6), Pg("timestamp without time zone", 6)).Should().Contain("TIMESTAMPTZ_ZONE_DROPPED");
        Codes(Ora("TIMESTAMP(6) WITH TIME ZONE", 6), Pg("timestamp with time zone", 6)).Should().Contain("TIMESTAMPTZ_OFFSET_NOT_KEPT");
        Codes(Ora("TIMESTAMP(9)", 9), Pg("timestamp without time zone", 6)).Should().Contain("TIMESTAMP_PRECISION");
    }

    [Fact]
    public void Text_semantics_changes_are_reported()
    {
        Codes(Ora("VARCHAR2", chars: 20, bytes: 80), Pg("character", chars: 20)).Should().Contain("VARCHAR_TO_CHAR");
        Codes(Ora("VARCHAR2", chars: 20, bytes: 80), Pg("character varying", chars: 10)).Should().Contain("VARCHAR_SHORTENED");
        Codes(Ora("VARCHAR2", chars: 20, bytes: 80), Pg("character varying", chars: 20)).Should().Equal("EMPTY_STRING_IS_NULL");
        Codes(Ora("VARCHAR2", chars: 20, bytes: 80, nullable: false), Pg("character varying", chars: 20, nullable: false)).Should().BeEmpty();
        Codes(Ora("CHAR", chars: 10, bytes: 40), Pg("character varying", chars: 10)).Should().Contain("CHAR_PADDING_NOT_KEPT");
    }

    [Fact]
    public void Wide_varchar_travels_as_a_digest_to_respect_the_4000_byte_row_string() =>
        TypeMapper.Map("t", Ora("VARCHAR2", chars: 4000, bytes: 4000), Pg("text")).Pair!.Kind.Should().Be(CanonicalKind.LargeText);

    [Fact]
    public void Unsupported_types_are_excluded_with_a_finding()
    {
        var (pair, findings) = TypeMapper.Map("t", Ora("BLOB"), Pg("bytea"));
        pair.Should().BeNull();
        findings.Single().Code.Should().Be("TYPE_NOT_COMPARED");
    }

    [Fact]
    public void Spec_builder_reports_missing_columns_and_keys()
    {
        var oracle = new CatalogTable("S", "T", [Ora("NUMBER", 10, 0) with { Name = "ID", Nullable = false }, Ora("DATE") with { Name = "GONE" }], ["ID"]);
        var pg = new CatalogTable("s", "t", [Pg("bigint", nullable: false) with { Name = "id" }, Pg("text") with { Name = "added" }], []);
        var (spec, findings) = TableSpecBuilder.Build("t", oracle, pg);

        spec.Should().NotBeNull();
        findings.Select(f => f.Code).Should().Contain(["COLUMN_MISSING_IN_TARGET", "COLUMN_ONLY_IN_TARGET", "PRIMARY_KEY_DIFFERS"]);

        var (none, noPk) = TableSpecBuilder.Build("t", oracle with { PrimaryKey = [] }, pg);
        none.Should().BeNull();
        noPk.Should().Contain(f => f.Code == "NO_PRIMARY_KEY");
    }
}
