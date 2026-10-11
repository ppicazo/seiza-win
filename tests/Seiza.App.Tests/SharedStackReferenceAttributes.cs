using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

/// <summary>Keep unreleased-core coverage visibly skipped against the published DLL.</summary>
public sealed class StackReferenceImageFactAttribute : FactAttribute
{
    public StackReferenceImageFactAttribute()
    {
        if (!StackingCoreCapabilities.Current.SupportsReferenceImage)
        {
            Skip = "The pinned published Seiza core does not yet expose shared alignment references.";
        }
    }
}

public sealed class StackReferenceImageTheoryAttribute : TheoryAttribute
{
    public StackReferenceImageTheoryAttribute()
    {
        if (!StackingCoreCapabilities.Current.SupportsReferenceImage)
        {
            Skip = "The pinned published Seiza core does not yet expose shared alignment references.";
        }
    }
}

public sealed class StackReferenceImageCandidatesFactAttribute : FactAttribute
{
    public StackReferenceImageCandidatesFactAttribute()
    {
        if (!StackingCoreCapabilities.Current.SupportsReferenceImageCandidates)
        {
            Skip = "The pinned published Seiza core does not yet expose ranked alignment fallback.";
        }
    }
}
