using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class SkyPanelGroupingTests
{
    private static SkySolvedFrame Frame(string path, double ra = 0, double dec = 0, double rotation = 0)
    {
        double r = rotation * Math.PI / 180, scale = 0.001;
        return new(path, new SolveResult { CenterRaDegrees = ra, CenterDecDegrees = dec,
            MatchedStars = 30, SolvedFrom = "blind", Wcs = new WcsResult
            {
                Crval = [ra, dec], Crpix = [499.5, 399.5],
                Cd = [[-scale * Math.Cos(r), scale * Math.Sin(r)], [-scale * Math.Sin(r), -scale * Math.Cos(r)]],
            } }, 1000, 800);
    }

    [Fact]
    public void RepeatedPointingsArePanelsAndOverlappingPanelsFormFields()
    {
        SkySolvedFrame[] frames = [Frame("a.fits"), Frame("b.fits"), Frame("c.fits"), Frame("d.fits")];
        double[][] overlap = [[1, .95, .2, 0], [.95, 1, .19, 0], [.2, .19, 1, 0], [0, 0, 0, 1]];
        var fields = SkyPanelGrouping.Group(frames, overlap);
        Assert.Equal(2, fields.Count);
        Assert.Equal(2, fields[0].Panels.Count);
        Assert.Equal(2, fields[0].Panels[0].Frames.Count);
        Assert.Single(fields[1].Panels);
    }

    [Fact]
    public void DitherChainsDoNotCollapseTheWholeMosaicIntoOnePanel()
    {
        var fields = SkyPanelGrouping.Group([Frame("a"), Frame("b"), Frame("c")],
            [[1, .9, .8], [.9, 1, .9], [.8, .9, 1]]);
        Assert.Equal(2, Assert.Single(fields).Panels.Count);
    }

    [Fact]
    public void RemovingBridgePanelsSplitsOutputAndPreservesFilterSeparation()
    {
        SkySolvedFrame[] frames = [Frame("light1_Ha.fits"), Frame("light2_Ha.fits"), Frame("light3_OIII.fits")];
        double[][] overlaps = [[1, .2, 0], [.2, 1, .2], [0, .2, 1]];
        var analysis = new SkyPanelAnalysis(SkyPanelGrouping.Group(frames, overlaps), [],
            new Dictionary<string, SkySourceStamp>(), frames, overlaps);
        Assert.Equal(2, analysis.SelectedGroups(frames.Select(f => f.Path).ToArray(), true).Count);
        var groups = analysis.SelectedGroups([frames[0].Path, frames[2].Path], false);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Single(group.Inputs));
        var paths = ImageStackOutputNaming.SplitOutputPaths(Path.GetTempPath(), "mosaic", groups);
        Assert.Equal(2, paths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Sky field", groups[0].Title);
    }

    [Fact]
    public void InvalidOverlapMatricesFailClosed()
    {
        Assert.Throws<ArgumentException>(() => SkyPanelGrouping.Group([Frame("a")], []));
        Assert.Throws<ArgumentException>(() => SkyPanelGrouping.Group([Frame("a")], [[double.NaN]]));
    }

    [Theory]
    [InlineData("header-unchecked", 30)]
    [InlineData("header-checked", 0)]
    [InlineData("blind", 0)]
    public void UnconfirmedCoordinatesCannotEnterAutomaticGrouping(string route, int matches)
    {
        Assert.Throws<InvalidOperationException>(() => SkyPanelService.ValidateSolution(new SolveResult
        { SolvedFrom = route, MatchedStars = matches, Wcs = new() }));
    }

    [Fact]
    public void AnotherFiltersPanelCannotBridgeDisconnectedLights()
    {
        SkySolvedFrame[] frames = [Frame("light1_Ha.fits"), Frame("light2_OIII.fits"), Frame("light3_Ha.fits")];
        double[][] overlaps = [[1, .2, 0], [.2, 1, .2], [0, .2, 1]];
        var analysis = new SkyPanelAnalysis(SkyPanelGrouping.Group(frames, overlaps), [],
            new Dictionary<string, SkySourceStamp>(), frames, overlaps);
        var groups = analysis.SelectedGroups(frames.Select(f => f.Path).ToArray(), true);
        Assert.Equal(3, groups.Count);
        Assert.All(groups, group => Assert.Single(group.Inputs));
    }

    [Fact]
    public void DeselectingWholeFieldsDoesNotRenumberTheRemainingOutputLabels()
    {
        SkySolvedFrame[] frames = [Frame("light1.fits"), Frame("light2.fits")];
        double[][] overlaps = [[1, 0], [0, 1]];
        var analysis = new SkyPanelAnalysis(SkyPanelGrouping.Group(frames, overlaps), [],
            new Dictionary<string, SkySourceStamp>(), frames, overlaps);
        Assert.Equal("Sky field 2", Assert.Single(analysis.SelectedGroups([frames[1].Path], false)).Title);
    }

    [Fact]
    public void NativeFootprintsRespectRaWrapRotationAndUnrelatedSky()
    {
        SkySolvedFrame[] frames = [Frame("a", 359.8), Frame("b", .2), Frame("c", 359.8, rotation: 180), Frame("d", 180)];
        var matrix = SkyPanelService.OverlapMatrix(frames, null, CancellationToken.None);
        Assert.InRange(matrix[0][1], .599, .601);
        Assert.InRange(matrix[0][2], .999999, 1);
        Assert.Equal(0, matrix[0][3]);
        Assert.Equal(2, SkyPanelGrouping.Group(frames, matrix).Count);
        Assert.Throws<OperationCanceledException>(() => SkyPanelService.OverlapMatrix(frames, null, new CancellationToken(true)));
    }

    [Fact]
    public void ChangedOrMissingSourcesInvalidateSolvedSelections()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fits");
        try
        {
            File.WriteAllText(path, "original");
            var stamps = new Dictionary<string, SkySourceStamp>(StringComparer.OrdinalIgnoreCase) { [path] = SkySourceStamp.Capture(path) };
            var analysis = new SkyPanelAnalysis([], [], stamps, [], []);
            Assert.True(analysis.SourcesUnchanged([path]));
            File.WriteAllText(path, "changed source");
            Assert.False(analysis.SourcesUnchanged([path]));
            File.Delete(path);
            Assert.False(analysis.SourcesUnchanged([path]));
        }
        finally { File.Delete(path); }
    }
}
