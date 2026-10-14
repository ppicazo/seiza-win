using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxInputHistoryTests
{
    [Fact]
    public void ReplacedDustArtworkAndPlaneInputsStayProtectedAcrossNewPreparations()
    {
        string root = Path.Combine(Path.GetTempPath(), "SeizaInputHistory");
        string originalDust = Path.Combine(root, "given-dust.png"), paintedDust = Path.Combine(root, "private-painted.png");
        string originalLogo = Path.Combine(root, "original-logo.png"), originalMask = Path.Combine(root, "original-mask.png");
        var history = new ParallaxInputHistory();
        history.Remember([originalDust]);
        var original = new ParallaxComposition
        {
            WatermarkImage = originalLogo,
            Scene = new() { Planes = [new() { Mask = originalMask, DistanceParsecs = 150 }] },
        };
        history.Remember(ParallaxInputPaths.FromComposition(original));
        var updated = original.DeepClone();
        updated.WatermarkImage = Path.Combine(root, "new-logo.png");
        updated.Scene.Planes = null;
        history.Remember([paintedDust]);
        history.Remember(ParallaxInputPaths.FromComposition(updated));
        string[] protectedPaths = ParallaxInputPaths.Capture(history.Paths);
        foreach (string path in new[] { originalDust, originalLogo, originalMask, paintedDust, updated.WatermarkImage! })
            Assert.Contains(LiveStackPath.NormalizeForComparison(path), protectedPaths);
        Assert.DoesNotContain(originalMask, ParallaxInputPaths.FromComposition(updated));
    }

    [Fact]
    public void NormalizedAliasesAreDeduplicatedAndPublishedSnapshotsAreIndependent()
    {
        string path = Path.Combine(Path.GetTempPath(), "SeizaInputHistory", "source.png");
        var history = new ParallaxInputHistory();
        history.Remember([path, Path.Combine(Path.GetDirectoryName(path)!, ".", "source.png"), path.ToUpperInvariant()]);
        string[] before = ParallaxInputPaths.Capture(history.Paths);
        Assert.Single(before);
        history.Remember([Path.ChangeExtension(path, ".tif")]);
        Assert.Single(before);
        Assert.Equal(2, history.Paths.Count());
    }

    [Fact]
    public void InvalidDraftPathsDoNotDiscardPreviouslyProtectedInputs()
    {
        string path = Path.Combine(Path.GetTempPath(), "source.png");
        var history = new ParallaxInputHistory();
        history.Remember([path]);
        history.Remember([null, "", " ", "invalid\0path"]);
        Assert.Equal(LiveStackPath.NormalizeForComparison(path), Assert.Single(history.Paths));
    }
}
