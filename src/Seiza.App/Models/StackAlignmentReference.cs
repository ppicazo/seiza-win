namespace Seiza.App.Models;

internal sealed record StackAlignmentReferenceSkip(int Index, string Path, string Reason);

/// <summary>The core's choice from a best-first list of channel lights.</summary>
internal sealed record StackAlignmentReferenceSelection(
    int SchemaVersion,
    int ChannelReferenceIndex,
    string ChannelReferencePath,
    StackAlignmentReferenceSkip[] Skipped)
{
    public void Validate(IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (SchemaVersion != 1 || ChannelReferenceIndex < 0 ||
            ChannelReferenceIndex >= candidates.Count ||
            !string.Equals(ChannelReferencePath, candidates[ChannelReferenceIndex], StringComparison.Ordinal) ||
            Skipped is null || Skipped.Length != ChannelReferenceIndex)
        {
            throw new InvalidDataException("Seiza returned an inconsistent alignment reference selection.");
        }
        for (int index = 0; index < Skipped.Length; index++)
        {
            StackAlignmentReferenceSkip? skipped = Skipped[index];
            if (skipped is null || skipped.Index != index ||
                !string.Equals(skipped.Path, candidates[index], StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(skipped.Reason))
            {
                throw new InvalidDataException("Seiza returned an inconsistent alignment reference skip.");
            }
        }
    }
}
