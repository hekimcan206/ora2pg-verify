using System.Text.Json;
using System.Text.Json.Serialization;
using Ora2PgVerify.Audit;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Model;
using Ora2PgVerify.Scan;
using Ora2PgVerify.Scoring;

namespace Ora2PgVerify.Report;

/// <summary>Where and how the run happened. Endpoints are host/service only; credentials never enter the report.</summary>
public sealed record RunInfo(
    string Version,
    DateTimeOffset GeneratedAt,
    string OracleEndpoint,
    string PostgresEndpoint,
    string OracleSchema,
    string PostgresSchema,
    CompareStrategy Strategy,
    long ChunkRows,
    CanonicalOptions Canonical,
    bool ValuesIncluded);

public sealed record AuditSection(AuditResult Result, SimulatorResult? Simulation, AuditScore? Score);

public sealed record VerificationReport(
    RunInfo Run,
    ScanResult? Scan,
    IReadOnlyList<MappingFinding> Mapping,
    IReadOnlyList<TableComparison> Tables,
    DetectionScore? Detection,
    AuditSection? Audit)
{
    public bool RowsMatch => Tables.All(t => t.IsEqual);

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
