using Ora2PgVerify.Compare;
using Ora2PgVerify.UnitTests.Support;

namespace Ora2PgVerify.UnitTests.Compare;

public sealed class RetryTests
{
    /// <summary>Fails the first <paramref name="failures"/> session opens the way Oracle refuses a snapshot taken right after DDL.</summary>
    private sealed class FlakySource(IRowSource inner, int failures, bool retryable) : IRowSource
    {
        public int Opens { get; private set; }

        public string EngineName => inner.EngineName;

        public Task<IRowSourceSession> OpenSessionAsync(CancellationToken ct) =>
            ++Opens <= failures ? throw new InvalidOperationException("ORA-01466") : inner.OpenSessionAsync(ct);

        public bool IsRetryableSnapshotError(Exception exception) => retryable && exception.Message == "ORA-01466";
    }

    [Fact]
    public async Task A_retryable_snapshot_error_gets_a_fresh_snapshot()
    {
        var source = new FlakySource(new InMemoryRowSource("Oracle", TestRows.OracleOrders(50)), failures: 1, retryable: true);
        var result = await new TableComparer(new CompareOptions()).CompareAsync(TestSpecs.Orders, source, new InMemoryRowSource("PostgreSQL", TestRows.PostgresOrders(50)), TestContext.Current.CancellationToken);
        result.IsEqual.Should().BeTrue();
        source.Opens.Should().Be(2);
    }

    [Fact]
    public async Task Other_errors_propagate_without_retry()
    {
        var source = new FlakySource(new InMemoryRowSource("Oracle", TestRows.OracleOrders(50)), failures: 1, retryable: false);
        var act = () => new TableComparer(new CompareOptions()).CompareAsync(TestSpecs.Orders, source, new InMemoryRowSource("PostgreSQL", TestRows.PostgresOrders(50)), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
        source.Opens.Should().Be(1);
    }
}
