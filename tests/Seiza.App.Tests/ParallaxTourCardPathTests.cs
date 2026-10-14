using System.Text.Json;
using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxTourCardPathTests
{
    [Theory]
    [InlineData("cards/stop.png")]
    [InlineData(@"cards\stop.png")]
    [InlineData("cards/Opening card α.png")]
    [InlineData("../shared/stop.png")]
    public void FileImportResolvesRelativeCardsAgainstTheItineraryNotTheProcessDirectory(string card)
    {
        string tour = Path.Combine(Path.GetTempPath(), "SeizaTour-" + Guid.NewGuid().ToString("N"), "tour.json");
        string expected = Path.GetFullPath(card, Path.GetDirectoryName(tour)!);
        Assert.NotEqual(Path.GetFullPath(card), expected);
        ParallaxTourPlan imported = ParallaxTourPlan.FromJson(Plan(card).ToJson(), tour);
        Assert.Equal(expected, imported.Tour[1].Card);
        Assert.Null(imported.Tour[0].Card);
        Assert.Equal([20, 30], imported.Focus);
        Assert.Equal(0.35, imported.TourGlide);
        Assert.False(imported.TourTitles);
        Assert.True(imported.TourLoop);
    }

    [Theory]
    [InlineData(@"C:\Artwork\Opening card α.png")]
    [InlineData(@"P:\_Incoming\cards\stop.png")]
    [InlineData(@"\\nas\images\cards\stop.png")]
    [InlineData(@"\\?\C:\Artwork\stop.png")]
    public void FileImportPreservesRootedDriveAndUncCardPaths(string card)
    {
        string tour = Path.Combine(Path.GetTempPath(), "SeizaTour", "tour.json");
        Assert.Equal(card, ParallaxTourPlan.FromJson(Plan(card).ToJson(), tour).Tour[1].Card);
    }

    [Theory]
    [InlineData(@"\cards\stop.png")]
    [InlineData("/cards/stop.png")]
    public void FileImportResolvesRootRelativeCardsUsingTheItineraryDrive(string card)
    {
        string tour = Path.Combine(Path.GetTempPath(), "SeizaTour-" + Guid.NewGuid().ToString("N"), "tour.json");
        string expected = Path.Combine(Path.GetPathRoot(tour)!, "cards", "stop.png");
        Assert.True(Path.IsPathRooted(card));
        Assert.False(Path.IsPathFullyQualified(card));
        Assert.Equal(expected, ParallaxTourPlan.FromJson(Plan(card).ToJson(), tour).Tour[1].Card);
    }

    [Fact]
    public void FileImportResolvesSameDriveRelativeCardsUsingTheItineraryFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SeizaTour-" + Guid.NewGuid().ToString("N"));
        string tour = Path.Combine(folder, "tour.json");
        string card = Path.GetPathRoot(tour)![..2] + @"cards\stop.png";
        Assert.True(Path.IsPathRooted(card));
        Assert.False(Path.IsPathFullyQualified(card));
        Assert.Equal(Path.Combine(folder, "cards", "stop.png"), ParallaxTourPlan.FromJson(Plan(card).ToJson(), tour).Tour[1].Card);
    }

    [Fact]
    public void SavingThenMovingTheTourFolderKeepsItsCardsPortableWithoutMutatingLiveStops()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SeizaTour-" + Guid.NewGuid().ToString("N"));
        string tour = Path.Combine(folder, "tour.json");
        string artwork = Path.Combine(folder, "cards", "Opening card α.png");
        ParallaxTourPlan live = Plan(artwork);
        ParallaxStop originalStop = live.Tour[1];
        double[] originalFocus = originalStop.Focus!;
        string saved = live.ToJson(tour);
        using (var json = JsonDocument.Parse(saved))
            Assert.Equal("cards/Opening card α.png", json.RootElement.GetProperty("tour")[1].GetProperty("card").GetString());
        string movedFolder = Path.Combine(Path.GetTempPath(), "MovedTour-" + Guid.NewGuid().ToString("N"));
        ParallaxTourPlan moved = ParallaxTourPlan.FromJson(saved, Path.Combine(movedFolder, "tour.json"));
        Assert.Equal(Path.Combine(movedFolder, "cards", "Opening card α.png"), moved.Tour[1].Card);
        Assert.Equal(artwork, live.Tour[1].Card);
        Assert.Same(originalStop, live.Tour[1]);
        Assert.Same(originalFocus, live.Tour[1].Focus);
        Assert.Equal(originalStop.Id, live.Tour[1].Id);
        Assert.NotSame(originalStop, moved.Tour[1]);
        Assert.NotSame(originalFocus, moved.Tour[1].Focus);
    }

    [Theory]
    [InlineData(@"P:\Artwork\stop.png")]
    [InlineData(@"\\nas\images\stop.png")]
    public void SavingKeepsExternalRootedArtworkPathsUnchanged(string card)
    {
        string tour = Path.Combine(Path.GetTempPath(), "SeizaTour", "tour.json");
        ParallaxTourPlan live = Plan(card);
        Assert.Equal(card, ParallaxTourPlan.FromJson(live.ToJson(tour)).Tour[1].Card);
        Assert.Equal(card, live.Tour[1].Card);
    }

    [Fact]
    public void SavingKeepsSiblingArtworkRootedInsteadOfAccidentallyTreatingAPrefixAsContained()
    {
        string folder = Path.Combine(Path.GetTempPath(), "SeizaTour-" + Guid.NewGuid().ToString("N"));
        string card = Path.Combine(folder + "-sibling", "stop.png");
        string tour = Path.Combine(folder, "tour.json");
        Assert.Equal(card, ParallaxTourPlan.FromJson(Plan(card).ToJson(tour)).Tour[1].Card);
    }

    [Fact]
    public void SavingARelativeDraftCardRetainsItsCurrentFileMeaning()
    {
        const string card = "cards/stop.png";
        string tour = Path.Combine(Path.GetTempPath(), "SeizaTour-" + Guid.NewGuid().ToString("N"), "tour.json");
        ParallaxTourPlan live = Plan(card);
        ParallaxTourPlan imported = ParallaxTourPlan.FromJson(live.ToJson(tour), tour);
        Assert.Equal(Path.GetFullPath(card), imported.Tour[1].Card);
        Assert.Equal(card, live.Tour[1].Card);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavingARootOrDriveRelativeDraftCardAnchorsItToTheProcessDirectory(bool driveRelative)
    {
        string card = driveRelative ? Path.GetPathRoot(Environment.CurrentDirectory)![..2] + @"cards\stop.png" : @"\cards\stop.png";
        string tour = Path.Combine(Path.GetTempPath(), "SeizaTour-" + Guid.NewGuid().ToString("N"), "tour.json");
        ParallaxTourPlan live = Plan(card);
        string saved = live.ToJson(tour);
        Assert.Equal(Path.GetFullPath(card), ParallaxTourPlan.FromJson(saved, tour).Tour[1].Card);
        Assert.Equal(card, live.Tour[1].Card);
    }

    [Fact]
    public void OldCardFreeItinerariesAndContextFreeNativePlansKeepTheirMeaning()
    {
        const string oldJson = """{"schemaVersion":1,"focus":[20,30],"seconds":6,"tour":[{"travel":0,"hold":1},{"hold":1}]}""";
        string tour = Path.Combine(Path.GetTempPath(), "SeizaTour", "tour.json");
        ParallaxTourPlan old = ParallaxTourPlan.FromJson(oldJson, tour);
        Assert.All(old.Tour, stop => Assert.Null(stop.Card));
        Assert.Null(old.TourGlide);
        Assert.Null(old.TourTitles);
        Assert.Null(old.TourLoop);
        Assert.All(ParallaxTourPlan.FromJson(old.ToJson(tour)).Tour, stop => Assert.Null(stop.Card));
        // Native planner responses have no itinerary directory to rebase against.
        Assert.Equal("cards/stop.png", ParallaxTourPlan.FromJson(Plan("cards/stop.png").ToJson()).Tour[1].Card);
    }

    private static ParallaxTourPlan Plan(string card) => new()
    {
        Focus = [20, 30], FocusName = "Scene", Seconds = 6,
        TourGlide = 0.35, TourTitles = false, TourLoop = true,
        Tour = [new() { Travel = 0, Hold = 1 }, new() { Focus = [20, 30], Card = card, Travel = 4, Hold = 1 }],
    };
}
