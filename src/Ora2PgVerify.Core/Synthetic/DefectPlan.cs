using System.Globalization;
using Ora2PgVerify.Canonical;
using Ora2PgVerify.Compare;

namespace Ora2PgVerify.Synthetic;

public enum Side
{
    Oracle,
    Postgres,
}

/// <summary>A statement to run on one side while seeding (parameters are bound, never spliced).</summary>
public sealed record SeedCommand(Side Side, string Sql, IReadOnlyList<KeyValuePair<string, object>> Parameters);

/// <summary>Ground truth for one injected defect: what the comparison must report, and where.</summary>
public sealed record InjectedDefect(string Code, string Table, string Key, string DisplayKey, DiffKind Kind, string? Column, ValueDifference? Difference);

public sealed record DefectManifest(int Seed, int PerKind, DatasetSize Size, IReadOnlyList<InjectedDefect> Defects);

/// <summary>Names of the synthetic schema on both engines.</summary>
public static class SyntheticSchema
{
    public const string OracleSchema = "APP_OWNER";
    public const string PostgresSchema = "app";

    public static readonly IReadOnlyList<string> ComparedTables = ["orders", "customers", "order_documents"];

    public const string AccountsTable = "accounts";
}

/// <summary>
/// Plans deliberate defects on the PostgreSQL copy (and two on the Oracle side) after an otherwise faithful load.
/// Each kind reproduces a real migration failure mode: lost or duplicated rows, rounding, Oracle's '' = NULL rule,
/// whitespace, DATE truncated to date, time zone misinterpretation, case folding and an edited CLOB.
/// Rows are chosen deterministically from the seed, and only rows where the defect is observable (a NULL note for
/// the empty-string case, a non-midnight DATE for truncation) are used.
/// </summary>
public static class DefectPlanner
{
    public static (DefectManifest Manifest, IReadOnlyList<SeedCommand> Commands) Plan(int seed, DatasetSize size, int perKind)
    {
        ArgumentNullException.ThrowIfNull(size);
        var data = new SyntheticData(seed);
        var rnd = new Random(seed ^ 0x5EED);
        var used = new HashSet<long>();
        var defects = new List<InjectedDefect>();
        var commands = new List<SeedCommand>();
        var orders = data.Orders(size.Orders, size.Customers).ToList();

        long Pick(Func<OrderRow, bool> where)
        {
            for (var attempt = 0; attempt < 1_000_000; attempt++)
            {
                var o = orders[rnd.Next(orders.Count)];
                if (where(o) && used.Add(o.OrderId))
                {
                    return o.OrderId;
                }
            }

            throw new InvalidOperationException("Could not find a row for the defect; dataset too small.");
        }

        static KeyValuePair<string, object> P(string name, object value) => new(name, value);
        const string Pg = SyntheticSchema.PostgresSchema;
        const string Ora = SyntheticSchema.OracleSchema;

        void Order(string code, DiffKind kind, string? column, ValueDifference? diff, long id, Side side, string sql)
        {
            var key = id.ToString(CultureInfo.InvariantCulture);
            defects.Add(new InjectedDefect(code, "orders", key, key, kind, column, diff));
            commands.Add(new SeedCommand(side, sql, [P("id", id)]));
        }

        for (var i = 0; i < perKind; i++)
        {
            Order("ORD_MISSING", DiffKind.MissingInTarget, null, null, Pick(_ => true), Side.Postgres,
                $"DELETE FROM {Pg}.orders WHERE order_id = @id");

            // Half the "extra" rows sit beyond Oracle's MAX(key) (an insert that only reached PostgreSQL), half inside the
            // range (a row deleted in Oracle after the copy). Both must be found; the first also tests key-range planning.
            if (i % 2 == 0)
            {
                var newId = size.Orders + 1 + i;
                var template = Pick(_ => true);
                var key = newId.ToString(CultureInfo.InvariantCulture);
                defects.Add(new InjectedDefect("ORD_EXTRA", "orders", key, key, DiffKind.ExtraInTarget, null, null));
                commands.Add(new SeedCommand(Side.Postgres,
                    $"INSERT INTO {Pg}.orders SELECT @newId, customer_code, status, note, amount, discount_rate, created_on, shipped_at, updated_at FROM {Pg}.orders WHERE order_id = @id",
                    [P("newId", (long)newId), P("id", template)]));
            }
            else
            {
                Order("ORD_EXTRA", DiffKind.ExtraInTarget, null, null, Pick(_ => true), Side.Oracle,
                    $"DELETE FROM {Ora}.ORDERS WHERE ORDER_ID = :id");
            }

            Order("ORD_AMOUNT_DRIFT", DiffKind.Changed, "amount", ValueDifference.NumericDrift, Pick(_ => true), Side.Postgres,
                $"UPDATE {Pg}.orders SET amount = amount + 0.01 WHERE order_id = @id");
            Order("ORD_EMPTY_NOTE", DiffKind.Changed, "note", ValueDifference.EmptyStringVsNull, Pick(o => o.Note is null), Side.Postgres,
                $"UPDATE {Pg}.orders SET note = '' WHERE order_id = @id");
            Order("ORD_TRAILING_SPACE", DiffKind.Changed, "status", ValueDifference.TrailingWhitespace, Pick(_ => true), Side.Postgres,
                $"UPDATE {Pg}.orders SET status = status || ' ' WHERE order_id = @id");
            Order("ORD_TIME_TRUNCATED", DiffKind.Changed, "created_on", ValueDifference.TimeOfDayLost, Pick(o => o.CreatedOn.TimeOfDay != TimeSpan.Zero), Side.Postgres,
                $"UPDATE {Pg}.orders SET created_on = date_trunc('day', created_on) WHERE order_id = @id");
            Order("ORD_TZ_SHIFT", DiffKind.Changed, "updated_at", ValueDifference.TimeShift, Pick(_ => true), Side.Postgres,
                $"UPDATE {Pg}.orders SET updated_at = updated_at + interval '3 hours' WHERE order_id = @id");
        }

        var customerIds = Enumerable.Range(1, size.Customers).OrderBy(_ => rnd.Next()).Take(perKind * 2).ToList();
        foreach (var (cid, idx) in customerIds.Select((c, i) => (c, i)))
        {
            var code = SyntheticData.CustomerCode(cid);
            var key = Canonicalizer.Encode(code);
            if (idx < perKind)
            {
                defects.Add(new InjectedDefect("CUS_CASE", "customers", key, code, DiffKind.Changed, "full_name", ValueDifference.CaseOnly));
                commands.Add(new SeedCommand(Side.Postgres, $"UPDATE {Pg}.customers SET full_name = upper(full_name) WHERE customer_code = @code", [P("code", code)]));
            }
            else
            {
                defects.Add(new InjectedDefect("CUS_MISSING", "customers", key, code, DiffKind.MissingInTarget, null, null));
                commands.Add(new SeedCommand(Side.Postgres, $"DELETE FROM {Pg}.customers WHERE customer_code = @code", [P("code", code)]));
            }
        }

        var docs = data.Documents(size.Documents).Select(d => (d.OrderId, d.DocNo)).OrderBy(_ => rnd.Next()).Take(perKind).ToList();
        foreach (var (orderId, docNo) in docs)
        {
            var key = Canonicalizer.Encode(orderId.ToString(CultureInfo.InvariantCulture)) + Canonicalizer.Encode(docNo.ToString(CultureInfo.InvariantCulture));
            defects.Add(new InjectedDefect("DOC_CLOB_EDIT", "order_documents", key, Canonicalizer.DisplayKey(key), DiffKind.Changed, "body", ValueDifference.DigestDiffers));
            commands.Add(new SeedCommand(Side.Postgres,
                $"UPDATE {Pg}.order_documents SET body = overlay(body placing 'X' from 100 for 1) WHERE order_id = @oid AND doc_no = @dno",
                [P("oid", orderId), P("dno", docNo)]));
        }

        return (new DefectManifest(seed, perKind, size, defects), commands);
    }
}
