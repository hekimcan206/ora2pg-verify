using System.Text;
using Bogus;

namespace Ora2PgVerify.Synthetic;

public sealed record OrderRow(
    long OrderId,
    string CustomerCode,
    string Status,
    string? Note,
    decimal Amount,
    decimal DiscountRate,
    DateTime CreatedOn,
    DateTime? ShippedAt,
    DateTimeOffset UpdatedAt);

public sealed record CustomerRow(string CustomerCode, string FullName, string City, decimal CreditLimit, DateTime BirthDate, string Segment);

public sealed record DocumentRow(long OrderId, int DocNo, string Body, DateTime CreatedAt);

public sealed record AccountRow(long AccountId, decimal Balance, string Status, DateTime UpdatedAt);

/// <summary>Sizes of the synthetic schema.</summary>
public sealed record DatasetSize(int Orders, int Customers, int Documents, int Accounts)
{
    public static DatasetSize ForOrders(int orders, int accounts = 20_000) =>
        new(orders, Math.Max(200, orders / 20), Math.Max(100, orders / 50), accounts);
}

/// <summary>
/// Deterministic synthetic data (all data is synthetic; no real people or companies). The same seed produces the
/// same rows on every run, which keeps benchmark runs and defect manifests reproducible.
/// <para>
/// The data is chosen to exercise the type-semantics differences that matter: CHAR padding, Turkish and 4-byte
/// UTF-8 text, NULL notes, Oracle DATE values with and without a time of day, microsecond timestamps,
/// TIMESTAMP WITH TIME ZONE values stored with different offsets, and multi-kilobyte CLOBs.
/// </para>
/// </summary>
public sealed class SyntheticData(int seed)
{
    private static readonly string[] Statuses = ["NEW", "PAID", "SHIPPED", "DELIVERED", "CANCELLED", "RETURNED"];

    private static readonly string[] NoteWords =
    [
        "kapıda", "ödeme", "teslimat", "çağrı", "merkezi", "şube", "iade", "gönderim", "ürün", "değişim", "öncelikli",
        "fatura", "adres", "güncellendi", "müşteri", "istedi", "Işık", "İzmir", "Ağrı", "express", "hediye", "paketi",
    ];

    private static readonly string[] Emoji = ["📦", "🚚", "✅"];

    /// <summary>Offsets a source system might have written; the instant is what PostgreSQL keeps.</summary>
    private static readonly TimeSpan[] Offsets = [TimeSpan.FromHours(3), TimeSpan.Zero, TimeSpan.FromHours(-5), new TimeSpan(5, 30, 0)];

    private static readonly DateTime Epoch = new(2019, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static string CustomerCode(int i) => "C" + i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture);

    public IEnumerable<OrderRow> Orders(int count, int customers)
    {
        var r = new Random(seed);
        for (var id = 1L; id <= count; id++)
        {
            var created = Epoch.AddDays(r.Next(0, 6 * 365));
            // About 10% of DATE values are exactly midnight (typical of date-only data in a DATE column).
            if (r.Next(10) != 0)
            {
                created = created.AddSeconds(r.Next(1, 86_400));
            }

            DateTime? shipped = r.Next(10) < 7
                ? created.AddHours(r.Next(1, 240)).AddTicks(r.Next(0, 1_000_000) * 10L)
                : null;
            var updatedLocal = created.AddDays(r.Next(0, 30)).AddTicks(r.Next(0, 86_400_000) * 10_000L + (r.Next(0, 1000) * 10L));
            var offset = Offsets[r.Next(Offsets.Length)];
            var updated = new DateTimeOffset(updatedLocal, offset);

            yield return new OrderRow(
                id,
                CustomerCode(r.Next(1, customers + 1)),
                Statuses[r.Next(Statuses.Length)],
                r.Next(10) < 4 ? null : Note(r),
                decimal.Round((decimal)(r.NextDouble() * 50_000) + 1m, 2),
                decimal.Round((decimal)(r.NextDouble() * 0.3), 4),
                created,
                shipped,
                updated);
        }
    }

    public IEnumerable<CustomerRow> Customers(int count)
    {
        var faker = new Faker("tr") { Random = new Randomizer(seed + 1) };
        for (var i = 1; i <= count; i++)
        {
            yield return new CustomerRow(
                CustomerCode(i),
                faker.Name.FullName(),
                faker.Address.City(),
                decimal.Round(faker.Random.Decimal(1_000m, 250_000m), 2),
                Epoch.AddYears(-70).AddDays(faker.Random.Int(0, 50 * 365)),
                faker.PickRandom("A", "B", "C"));
        }
    }

    public IEnumerable<DocumentRow> Documents(int count)
    {
        var faker = new Faker("tr") { Random = new Randomizer(seed + 2) };
        var produced = 0;
        for (var orderId = 1L; produced < count; orderId += 7)
        {
            var docs = faker.Random.Int(1, 3);
            for (var d = 1; d <= docs && produced < count; d++, produced++)
            {
                var sb = new StringBuilder();
                var target = faker.Random.Int(1_000, 6_000);
                while (sb.Length < target)
                {
                    sb.Append(faker.Lorem.Paragraph()).Append(" Teslim alan: ").Append(faker.Name.FullName()).Append(" — şçğıöü İ.\n");
                }

                yield return new DocumentRow(orderId, d, sb.ToString(), Epoch.AddDays(faker.Random.Int(0, 2000)).AddTicks(faker.Random.Long(0, 863_999_999_999L) / 10 * 10));
            }
        }
    }

    public IEnumerable<AccountRow> Accounts(int count)
    {
        var r = new Random(seed + 3);
        for (var id = 1L; id <= count; id++)
        {
            yield return new AccountRow(id, decimal.Round((decimal)(r.NextDouble() * 10_000), 2), "ACTIVE", Epoch.AddDays(r.Next(0, 2000)));
        }
    }

    private static string Note(Random r)
    {
        var words = r.Next(2, 9);
        var sb = new StringBuilder();
        for (var w = 0; w < words; w++)
        {
            if (w > 0)
            {
                sb.Append(' ');
            }

            sb.Append(NoteWords[r.Next(NoteWords.Length)]);
        }

        if (r.Next(50) == 0)
        {
            sb.Append(' ').Append(Emoji[r.Next(Emoji.Length)]);
        }

        return sb.ToString();
    }
}
