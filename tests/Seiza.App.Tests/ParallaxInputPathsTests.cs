using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxInputPathsTests
{
    [Fact]
    public void CompositionProtectsAllArtworkIncludingInactiveTourStopCards()
    {
        var composition = new ParallaxComposition
        {
            Motion = ParallaxMotion.FlyIn,
            WatermarkImage = "logo.png", IntroCard = "opening.png", OutroCard = "closing.png",
        };
        composition.Stops[0].Card = "first-stop.png";
        composition.Stops[1].Card = "second-stop.png";

        string?[] paths = ParallaxInputPaths.FromComposition(composition).ToArray();
        foreach (string artwork in new[] { "logo.png", "opening.png", "closing.png", "first-stop.png", "second-stop.png" })
            Assert.Contains(artwork, paths);
        Assert.Empty(composition.VideoSettings(false).Tour);
    }

    [Fact]
    public void VideoProtectsEveryArtworkPathIndependentlyOfCreditAndTitleText()
    {
        var video = new ParallaxVideoSettings
        {
            Watermark = new ParallaxWatermark(null) { Image = "logo.png" },
            IntroCard = "opening.png", OutroCard = "closing.png", TourTitles = false,
            Tour = [new() { Card = "first-stop.png" }, new() { Card = "second-stop.png" }],
        };

        string?[] paths = ParallaxInputPaths.FromVideo(video).ToArray();
        foreach (string artwork in new[] { "logo.png", "opening.png", "closing.png", "first-stop.png", "second-stop.png" })
            Assert.Contains(artwork, paths);
    }

    [Fact]
    public void CapturedPathsDropBlankEntriesAndNormalizeDuplicateAliases()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SeizaArtworkPathTests");
        string artwork = Path.Combine(directory, "logo.png");
        string alias = Path.Combine(directory, ".", "logo.png");

        string captured = Assert.Single(ParallaxInputPaths.Capture([null, string.Empty, " ", artwork, alias]));
        Assert.Equal(LiveStackPath.NormalizeForComparison(artwork), captured);
    }
}
