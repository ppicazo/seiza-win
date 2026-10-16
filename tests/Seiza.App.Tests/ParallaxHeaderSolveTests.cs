using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxHeaderSolveTests
{
    [Theory]
    [InlineData("original.xisf")]
    [InlineData("original.tif")]
    public async Task KnownStarlessSourceOnlySolvesAlignedStars(string original)
    {
        var paths = new List<string>(); var request = Request();
        await ParallaxRequestSource.EnsureSolutionAsync(request, original, path =>
        { paths.Add(path); return Task.FromResult(Result("blind")); }, CancellationToken.None,
            sourceIsStarless: true, layersAligned: true, snapshotPath: "starless-snapshot.png");
        Assert.Equal(["private-stars.tif"], paths);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingOrUnalignedStarsBlockKnownStarlessBeforeAnySolve(bool hasStars, bool aligned)
    {
        var request = Request(); if (!hasStars) request.Inputs.Stars = null;
        int calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ParallaxRequestSource.EnsureSolutionAsync(request, "starless.tif",
            _ => { calls++; return Task.FromResult(Result("blind")); }, CancellationToken.None, true, aligned));
        Assert.Contains("starless", error.Message); Assert.Equal(0, calls); Assert.Null(request.Inputs.Wcs);
    }
    [Fact]
    public async Task KnownStarlessWithExistingWcsNeedsNoStarsOrSolve()
    {
        var request = Request(); request.Inputs.Wcs = Result("header-checked").Wcs; request.Inputs.Stars = null;
        Assert.Null(await ParallaxRequestSource.EnsureSolutionAsync(request, "starless.tif", _ => throw new InvalidOperationException(), CancellationToken.None, true, false));
    }
    [Fact]
    public async Task UnalignedOrdinarySourceNeverSolvesTheSelectedStarsAndExplainsFailure()
    {
        var paths = new List<string>();
        var error = await Assert.ThrowsAsync<SeizaCoreException>(() => ParallaxRequestSource.EnsureSolutionAsync(Request(), "original.fits", path =>
        { paths.Add(path); return Task.FromException<SolveResult>(new SeizaCoreException("No match")); }, CancellationToken.None,
            layersAligned: false, snapshotPath: "aligned-snapshot.png"));
        Assert.Equal(["original.fits", "aligned-snapshot.png"], paths);
        Assert.Contains("Enable Layers match", error.Message);
    }
    [Fact]
    public void StarlessIdentityComparesPathsNotFilenameGuesses()
    {
        Assert.True(ParallaxRequestSource.SameSource("source/../source/image.tif", "source/image.tif"));
        Assert.False(ParallaxRequestSource.SameSource("one/starless.tif", "two/starless.tif"));
    }
    [Fact]
    public async Task AstronomyOriginalIsTriedBeforeThePrivateStarsLayer()
    {
        var request = Request(); var paths = new List<string>();
        var result = Result("header-checked");
        Assert.Same(result, await ParallaxRequestSource.EnsureSolutionAsync(request, "original.xisf", path =>
        { paths.Add(path); return Task.FromResult(result); }, CancellationToken.None));
        Assert.Equal(["original.xisf"], paths); Assert.Same(result.Wcs, request.Inputs.Wcs);
    }
    [Fact]
    public async Task FailedOriginalFallsBackToAlignedPrivateStarsNotStarless()
    {
        var request = Request(); var paths = new List<string>();
        await ParallaxRequestSource.EnsureSolutionAsync(request, "original.fits", path =>
        {
            paths.Add(path);
            return path == "original.fits" ? Task.FromException<SolveResult>(new SeizaCoreException("No match")) : Task.FromResult(Result("blind"));
        }, CancellationToken.None);
        Assert.Equal(["original.fits", "private-stars.tif"], paths);
    }
    [Fact]
    public async Task OrientedRasterKeepsTheSafePrivateSolveSource()
    {
        string? solved = null;
        await ParallaxRequestSource.EnsureSolutionAsync(Request(), "original.jpg", path =>
        { solved = path; return Task.FromResult(Result("blind")); }, CancellationToken.None);
        Assert.Equal("private-stars.tif", solved);
    }
    [Fact]
    public async Task PreservedViewerWcsBypassesOriginalAndCancelledResultIsNotPublished()
    {
        var request = Request(); request.Inputs.Wcs = Result("blind").Wcs;
        Assert.Null(await ParallaxRequestSource.EnsureSolutionAsync(request, "original.fits",
            _ => throw new InvalidOperationException(), CancellationToken.None));
        request.Inputs.Wcs = null; using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ParallaxRequestSource.EnsureSolutionAsync(request, "original.fits", _ =>
        { cancel.Cancel(); return Task.FromResult(Result("header-checked")); }, cancel.Token));
        Assert.Null(request.Inputs.Wcs);
    }
    [Fact]
    public void SolveProvenanceAndPixelOriginReadFromNewAndOldCores()
    {
        var result = JsonSerializer.Deserialize("""{"solvedFrom":"header-unchecked","falseMatchProbability":1,"wcs":{"pixelOrigin":0,"crpix":[1,2]}}""", SeizaJsonSerializerContext.Default.SolveResult)!;
        Assert.True(result.IsUncheckedHeader); Assert.Equal(1, result.FalseMatchProbability);
        Assert.Contains("no stars confirmed", result.SourceDescription); Assert.Equal(0, result.Wcs.PixelOrigin);
        var old = JsonSerializer.Deserialize("""{"wcs":{"crpix":[1,2]}}""", SeizaJsonSerializerContext.Default.SolveResult)!;
        Assert.Null(old.SolvedFrom); Assert.Null(old.FalseMatchProbability); Assert.Equal(0, old.Wcs.PixelOrigin);
        Assert.Null(old.SourceDescription);
    }
    private static ParallaxRequest Request() => new() { Inputs = new() { Image = "private-stars.tif", Starless = "private-starless.tif", Stars = "private-stars.tif" } };
    private static SolveResult Result(string source) => new() { SolvedFrom = source, Wcs = new() { Crpix = [100, 200] } };
}
