using Ora2PgVerify.Synthetic;

namespace Ora2PgVerify.UnitTests.Support;

/// <summary>Raw rows as each driver would return them for the synthetic tables (Oracle CHAR padded, offsets kept; PostgreSQL UTC).</summary>
internal static class TestRows
{
    public static List<object?[]> OracleOrders(int n, int seed = 1) =>
        [.. new SyntheticData(seed).Orders(n, 50).Select(o => new object?[]
        {
            o.OrderId, o.CustomerCode.PadRight(10), o.Status, o.Note, o.Amount, o.DiscountRate, o.CreatedOn, o.ShippedAt, o.UpdatedAt,
        })];

    public static List<object?[]> PostgresOrders(int n, int seed = 1) =>
        [.. new SyntheticData(seed).Orders(n, 50).Select(o => new object?[]
        {
            o.OrderId, o.CustomerCode, o.Status, o.Note, o.Amount, o.DiscountRate, o.CreatedOn, o.ShippedAt, DateTime.SpecifyKind(o.UpdatedAt.UtcDateTime, DateTimeKind.Utc),
        })];

    public static List<object?[]> Documents(int n, int seed = 1) =>
        [.. new SyntheticData(seed).Documents(n).Select(d => new object?[] { d.OrderId, d.DocNo, d.Body, d.CreatedAt })];

    public static List<object?[]> Customers(int n, int seed = 1) =>
        [.. new SyntheticData(seed).Customers(n).Select(c => new object?[] { c.CustomerCode, c.FullName, c.BirthDate, c.Segment })];

    public static object?[] With(object?[] row, int index, object? value)
    {
        var copy = (object?[])row.Clone();
        copy[index] = value;
        return copy;
    }
}
