using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class LiveStackOverviewTests
{
    [Theory]
    [InlineData(0, 4, 1)]
    [InlineData(559, 4, 1)]
    [InlineData(560, 4, 2)]
    [InlineData(839, 4, 2)]
    [InlineData(840, 4, 3)]
    [InlineData(2000, 1, 1)]
    [InlineData(2000, 2, 2)]
    [InlineData(2000, 0, 1)]
    [InlineData(double.NaN, 4, 1)]
    public void LayoutFitsAvailableWidthAndGroupCount(double width, int count, int columns) =>
        Assert.Equal(columns, LiveStackOverviewItem.ColumnsFor(width, count));

    [Fact]
    public void EmptyUnfilteredGroupHasAnHonestWaitingState()
    {
        var item = LiveStackOverviewItem.Create(LiveStackFilterIdentity.FromStoredName(null),
            new LiveStackRunSnapshot { State = LiveStackRunState.WaitingForLight });
        Assert.Equal("unfiltered", item.Key);
        Assert.Equal("Unfiltered", item.Name);
        Assert.Equal("Waiting for lights", item.State);
        Assert.Contains("first light", item.Placeholder);
        Assert.Null(item.Preview);
    }

    [Fact]
    public void PreviewIsReusedAndCountsRemainIndependent()
    {
        var preview = Preview();
        var snapshot = new LiveStackRunSnapshot
        {
            State = LiveStackRunState.Processing, Preview = preview,
            AcceptedFrames = 30, RejectedFrames = 2, IgnoredFrames = 3, UnreadableFrames = 1,
            StatusMessage = "Registering the next R light",
        };
        var red = LiveStackOverviewItem.Create(LiveStackFilterIdentity.FromStoredName("R"), snapshot);
        var luminance = LiveStackOverviewItem.Create(LiveStackFilterIdentity.FromStoredName("L"),
            snapshot with { AcceptedFrames = 10, Preview = null, State = LiveStackRunState.Paused });
        Assert.Same(preview, red.Preview);
        Assert.Equal("30 accepted · 2 rejected · 4 skipped", red.Counts);
        Assert.Equal(snapshot.StatusMessage, red.Message);
        Assert.StartsWith("10 accepted", luminance.Counts);
        Assert.Equal("Paused", luminance.State);
        Assert.Null(luminance.Preview);
    }

    [Fact]
    public void FailedGroupShowsItsErrorWithoutLosingItsLastPreview()
    {
        var preview = Preview();
        var item = LiveStackOverviewItem.Create(LiveStackFilterIdentity.FromStoredName("Ha"),
            new LiveStackRunSnapshot { Preview = preview }, error: "Calibration failed");
        Assert.Equal("Needs attention", item.State);
        Assert.Equal("Calibration failed", item.Message);
        Assert.Same(preview, item.Preview);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoredAndJustFinishedGroupsDoNotPretendToBeWaiting(bool completed)
    {
        var item = LiveStackOverviewItem.Create(LiveStackFilterIdentity.FromStoredName("B"),
            new LiveStackRunSnapshot { State = completed ? LiveStackRunState.Created : LiveStackRunState.Completed }, completed);
        Assert.Equal("Completed", item.State);
        Assert.Contains("no preview retained", item.Placeholder);
        Assert.DoesNotContain("first light", item.Placeholder);
    }

    private static RenderedImageData Preview() => new([0, 0, 0, 255], 1, 1,
        new ImageMetadata(1, 1, 1, "fits", "mono", null, new ImageStatistics(0, 0, 0, 0, 0),
            new Dictionary<string, System.Text.Json.JsonElement>()));
}
