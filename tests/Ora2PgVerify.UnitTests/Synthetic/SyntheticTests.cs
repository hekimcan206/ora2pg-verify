using Ora2PgVerify.Compare;
using Ora2PgVerify.Scoring;
using Ora2PgVerify.Synthetic;

namespace Ora2PgVerify.UnitTests.Synthetic;

public sealed class SyntheticTests
{
    [Fact]
    public void Same_seed_same_data()
    {
        new SyntheticData(9).Orders(500, 20).Should().Equal(new SyntheticData(9).Orders(500, 20));
        new SyntheticData(9).Customers(50).Should().Equal(new SyntheticData(9).Customers(50));
        new SyntheticData(9).Documents(20).Select(d => d.Body).Should().Equal(new SyntheticData(9).Documents(20).Select(d => d.Body));
        new SyntheticData(9).Orders(50, 20).Should().NotEqual(new SyntheticData(10).Orders(50, 20));
    }

    [Fact]
    public void Data_exercises_the_semantics_that_matter()
    {
        var orders = new SyntheticData(42).Orders(5_000, 250).ToList();
        orders.Should().Contain(o => o.Note == null).And.Contain(o => o.Note != null && o.Note.Contains('ş'));
        orders.Should().Contain(o => o.CreatedOn.TimeOfDay == TimeSpan.Zero).And.Contain(o => o.CreatedOn.TimeOfDay != TimeSpan.Zero);
        orders.Select(o => o.UpdatedAt.Offset).Distinct().Should().HaveCountGreaterThan(2);
        orders.Should().Contain(o => o.Note != null && o.Note.EnumerateRunes().Any(r => r.Utf8SequenceLength == 4));
        new SyntheticData(42).Documents(10).Should().OnlyContain(d => d.Body.Length >= 1_000);
    }

    [Fact]
    public void Defect_plan_is_deterministic_with_unique_observable_rows()
    {
        var size = DatasetSize.ForOrders(5_000);
        var (a, commands) = DefectPlanner.Plan(7, size, 10);
        var (b, _) = DefectPlanner.Plan(7, size, 10);

        a.Defects.Should().Equal(b.Defects);
        a.Defects.Should().HaveCount(100);
        a.Defects.GroupBy(d => (d.Table, d.Key)).Should().OnlyContain(g => g.Count() == 1, "one defect per row keeps the ground truth unambiguous");
        commands.Should().HaveCount(100).And.OnlyContain(c => c.Parameters.Count > 0, "values are bound");
        a.Defects.Where(d => d.Code == "ORD_EXTRA").Should().HaveCount(10);

        var orders = new SyntheticData(7).Orders(size.Orders, size.Customers).ToDictionary(o => o.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        a.Defects.Where(d => d.Code == "ORD_EMPTY_NOTE").Should().OnlyContain(d => orders[d.Key].Note == null);
        a.Defects.Where(d => d.Code == "ORD_TIME_TRUNCATED").Should().OnlyContain(d => orders[d.Key].CreatedOn.TimeOfDay != TimeSpan.Zero);
    }

    [Fact]
    public void Scorer_counts_misses_wrong_kinds_and_false_positives()
    {
        var manifest = new DefectManifest(1, 1, DatasetSize.ForOrders(10),
        [
            new InjectedDefect("A", "t", "1", "1", DiffKind.Changed, "x", ValueDifference.NumericDrift),
            new InjectedDefect("B", "t", "2", "2", DiffKind.MissingInTarget, null, null),
            new InjectedDefect("C", "t", "3", "3", DiffKind.Changed, "y", ValueDifference.CaseOnly),
        ]);
        var diffs = new List<RowDiff>
        {
            new("1", "1", DiffKind.Changed, "a", "b", [new ColumnDiff("x", ValueDifference.NumericDrift, "", null, null)]),
            new("2", "2", DiffKind.ExtraInTarget, null, "b", []),
            new("3", "3", DiffKind.Changed, "a", "b", [new ColumnDiff("y", ValueDifference.Other, "", null, null), new ColumnDiff("z", ValueDifference.Other, "", null, null)]),
            new("9", "9", DiffKind.Changed, "a", "b", []),
        };
        var comparison = new TableComparison("t", CompareStrategy.DatabaseSide, Ora2PgVerify.Model.ChunkingMode.Range, new("O", 1, 1, 1), new("P", 1, 1, 1), 1, 1, 1, 1, diffs, false,
            new PhaseTimings(default, default, default, default, default), new());

        var score = DetectionScorer.Score(manifest, [comparison]);

        score.TruePositives.Should().Be(2);
        score.FalseNegatives.Should().Be(1, "key 2 was reported with the wrong kind");
        score.FalsePositives.Should().Be(1);
        score.ExactColumn.Should().Be(1, "row 3 named an extra column");
        score.Kinds.Single(k => k.Code == "C").ClassifiedAs.Should().Be(0);
        score.Recall.Should().BeApproximately(2 / 3.0, 1e-9);
        score.Precision.Should().BeApproximately(2 / 3.0, 1e-9);
    }
}
