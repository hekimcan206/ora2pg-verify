using DotNet.Testcontainers.Configurations;
using Npgsql;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Cli;
using Ora2PgVerify.Compare;
using Ora2PgVerify.Oracle;
using Ora2PgVerify.Postgres;
using Ora2PgVerify.Synthetic;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Ora2PgVerify.IntegrationTests.Engines))]

namespace Ora2PgVerify.IntegrationTests;

/// <summary>
/// One Oracle 23ai Free and one PostgreSQL 17 container for the whole run, initialised with the same scripts as
/// docker compose and seeded through the same workflow as the CLI (5,000 orders, 5 defects per kind).
/// </summary>
public sealed class Engines : IAsyncLifetime
{
    // Test-only credentials for throwaway containers.
    public const string OwnerPassword = "OwnerTest1";
    public const string ReaderPassword = "ReaderTest1";
    public const int Rows = 5_000;
    public const int Seed = 11;
    public const int PerKind = 5;

    private static readonly UnixFileModes Executable = UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.UserExecute
        | UnixFileModes.GroupRead | UnixFileModes.GroupExecute | UnixFileModes.OtherRead | UnixFileModes.OtherExecute;

    private readonly OracleContainer _oracle = new OracleBuilder("gvenzl/oracle-free:23-slim-faststart")
        .WithEnvironment("OWNER_PASSWORD", OwnerPassword)
        .WithEnvironment("READER_PASSWORD", ReaderPassword)
        .WithResourceMapping(Script("oracle-01-users.sh"), "/container-entrypoint-initdb.d/01-users.sh", fileMode: Executable)
        .WithLabel("project", "ora2pg-verify-tests")
        .Build();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("appdb")
        .WithEnvironment("OWNER_PASSWORD", OwnerPassword)
        .WithEnvironment("READER_PASSWORD", ReaderPassword)
        .WithResourceMapping(Script("postgres-01-roles.sh"), "/docker-entrypoint-initdb.d/01-roles.sh", fileMode: Executable)
        .WithLabel("project", "ora2pg-verify-tests")
        .Build();

    private readonly List<NpgsqlDataSource> _sources = [];

    public string OracleReader { get; private set; } = "";

    public string OracleOwner { get; private set; } = "";

    public string PostgresReader { get; private set; } = "";

    public string PostgresOwner { get; private set; } = "";

    public string PostgresSuperuser { get; private set; } = "";

    public DefectManifest Manifest { get; private set; } = null!;

    internal Workflows Flows { get; } = new(TextWriter.Null);

    private static byte[] Script(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "db-init", name));

    public NpgsqlDataSource PostgresSource(string connectionString)
    {
        var s = PostgresEngine.CreateDataSource(connectionString);
        _sources.Add(s);
        return s;
    }

    public IRowSource OracleRows(CanonicalOptions? o = null) => new DbRowSource(new OracleEngine(OracleReader), o ?? CanonicalOptions.Default);

    public IRowSource PostgresRows(CanonicalOptions? o = null) => new DbRowSource(new PostgresEngine(PostgresSource(PostgresReader)), o ?? CanonicalOptions.Default);

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_oracle.StartAsync(), _postgres.StartAsync());

        string Ora(string user, string password) =>
            new OracleConnectionStringBuilder(_oracle.GetConnectionString()) { UserID = user, Password = password, ConnectionTimeout = 120 }.ConnectionString;
        string Pg(string user, string password) =>
            new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Username = user, Password = password, Timeout = 60 }.ConnectionString;

        OracleReader = Ora("verify_reader", ReaderPassword);
        OracleOwner = Ora("app_owner", OwnerPassword);
        PostgresReader = Pg("verify_reader", ReaderPassword);
        PostgresOwner = Pg("app_owner", OwnerPassword);
        PostgresSuperuser = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Timeout = 60 }.ConnectionString;

        // Warm-up: the first ODP.NET connection after the container reports ready can take a while on a loaded host.
        await using (var c = await OracleEngine.OpenOracleAsync(OracleOwner, CancellationToken.None))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM dual";
            await cmd.ExecuteScalarAsync();
        }

        Manifest = await Flows.SeedAsync(OracleOwner, PostgresOwner, Rows, Seed, PerKind, "VERIFY_READ", "verify_reader", CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources)
        {
            await s.DisposeAsync();
        }

        await Task.WhenAll(_oracle.DisposeAsync().AsTask(), _postgres.DisposeAsync().AsTask());
    }
}
