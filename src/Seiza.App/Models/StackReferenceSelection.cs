namespace Seiza.App.Models;

internal sealed record StackReferenceScore(
    int Stars,
    double MedianStarArea,
    double Background,
    double BackgroundVariation,
    double Score);

internal sealed record StackReferenceSelection(
    int SchemaVersion,
    int ReferenceIndex,
    string ReferencePath,
    StackReferenceScore?[] Scores)
{
    /// <summary>Native best-first input indices; absent in older published cores.</summary>
    public int[]? Ranking { get; init; }

    public void Validate(IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (SchemaVersion != 1 || Scores is null || Scores.Length != candidates.Count ||
            ReferenceIndex < 0 || ReferenceIndex >= candidates.Count ||
            !string.Equals(ReferencePath, candidates[ReferenceIndex], StringComparison.Ordinal) ||
            Scores[ReferenceIndex] is null ||
            Scores.OfType<StackReferenceScore>().Any(score =>
                score.Stars < 0 || !double.IsFinite(score.MedianStarArea) || score.MedianStarArea <= 0 ||
                !double.IsFinite(score.Background) || !double.IsFinite(score.BackgroundVariation) ||
                score.BackgroundVariation < 0 || !double.IsFinite(score.Score) || score.Score <= 0))
        {
            throw new InvalidDataException("Seiza returned an inconsistent reference selection.");
        }

        if (Ranking is not null &&
            (Ranking.Length != Scores.Count(score => score is not null) ||
             Ranking.Length == 0 || Ranking[0] != ReferenceIndex ||
             Ranking.Distinct().Count() != Ranking.Length ||
             Ranking.Any(index => index < 0 || index >= candidates.Count || Scores[index] is null)))
        {
            throw new InvalidDataException("Seiza returned an inconsistent reference ranking.");
        }
    }

    /// <summary>Use the core's order without reproducing its scoring in Windows.</summary>
    public string[] GetAlignmentCandidates(IReadOnlyList<string> candidates, int maximum = 5)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        Validate(candidates);
        return Ranking is null
            ? [candidates[ReferenceIndex]]
            : Ranking.Take(maximum).Select(index => candidates[index]).ToArray();
    }
}
