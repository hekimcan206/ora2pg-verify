using Ora2PgVerify.Compare;
using Ora2PgVerify.Synthetic;

namespace Ora2PgVerify.Scoring;

public sealed record KindScore(string Code, string Table, int Injected, int Detected, int ExactColumn, int ClassifiedAs);

public sealed record DetectionScore(
    IReadOnlyList<KindScore> Kinds,
    int Injected,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    int ExactColumn,
    IReadOnlyList<string> FalsePositiveKeys,
    IReadOnlyList<string> MissedKeys)
{
    public double Precision => TruePositives + FalsePositives == 0 ? 1 : (double)TruePositives / (TruePositives + FalsePositives);

    public double Recall => Injected == 0 ? 1 : (double)TruePositives / Injected;
}

/// <summary>
/// Scores comparison output against the defect manifest. A defect counts as detected only when the right row is
/// reported with the right kind (missing / extra / changed); for changed rows, "exact column" additionally requires
/// that the differing column is the injected one and nothing else, and "classified" that the diagnosis matches.
/// Every reported row that is not in the manifest is a false positive.
/// </summary>
public static class DetectionScorer
{
    public static DetectionScore Score(DefectManifest manifest, IEnumerable<TableComparison> comparisons)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(comparisons);
        var reported = comparisons
            .SelectMany(c => c.Diffs.Select(d => (c.Table, Diff: d)))
            .ToDictionary(x => (x.Table, x.Diff.Key), x => x.Diff);

        var expected = manifest.Defects.ToDictionary(d => (d.Table, d.Key));
        var kinds = new List<KindScore>();
        int tp = 0, exact = 0;
        var missed = new List<string>();

        foreach (var g in manifest.Defects.GroupBy(d => (d.Code, d.Table)).OrderBy(g => g.Key.Table, StringComparer.Ordinal).ThenBy(g => g.Key.Code, StringComparer.Ordinal))
        {
            int detected = 0, exactCol = 0, classified = 0;
            foreach (var d in g)
            {
                if (!reported.TryGetValue((d.Table, d.Key), out var r) || r.Kind != d.Kind)
                {
                    missed.Add($"{d.Table}:{d.DisplayKey} ({d.Code})");
                    continue;
                }

                detected++;
                if (d.Column is null || (r.Columns.Count == 1 && r.Columns[0].Column == d.Column))
                {
                    exactCol++;
                }

                if (d.Difference is null || r.Columns.Any(c => c.Column == d.Column && c.Difference == d.Difference))
                {
                    classified++;
                }
            }

            tp += detected;
            exact += exactCol;
            kinds.Add(new KindScore(g.Key.Code, g.Key.Table, g.Count(), detected, exactCol, classified));
        }

        var falsePositives = reported.Keys.Where(k => !expected.ContainsKey(k)).Select(k => $"{k.Table}:{reported[k].DisplayKey}").Order(StringComparer.Ordinal).ToList();
        return new DetectionScore(kinds, manifest.Defects.Count, tp, falsePositives.Count, manifest.Defects.Count - tp, exact, falsePositives, missed);
    }
}
