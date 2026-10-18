using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ColorWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Seiza-color-" + Guid.NewGuid().ToString("N"));
    public ColorWorkflowTests() => Directory.CreateDirectory(_root);
    private string Light(string name, int frame = 0, int side = 128)
    {
        string path = Path.Combine(_root, name + ".fits");
        StackingOptionsNativeTests.WriteStarField(path, frame, false, side);
        return path;
    }
    private ColorWorkflowRequest Request(string extension, bool luminance) => new(Light("red"), Light("green"), Light("blue"),
        luminance ? Light("luminance") : null, null, Path.Combine(_root, "result" + extension), true, false, null);

    [Theory]
    [InlineData(".fits", false)]
    [InlineData(".xisf", false)]
    [InlineData(".fits", true)]
    [InlineData(".xisf", true)]
    public async Task ComposesLinearChannelsThroughNativeCoreWithoutChangingInputs(string extension, bool luminance)
    {
        var request = Request(extension, luminance);
        byte[] original = File.ReadAllBytes(request.Red!);
        var result = await ColorWorkflowService.RunAsync(request);
        Assert.Equal(request.Output, result.Output);
        var image = SeizaCore.Render(request.Output, 64);
        Assert.Equal(64, image.Width);
        Assert.Equal(3, image.Metadata.Planes);
        Assert.Equal(original, File.ReadAllBytes(request.Red!));
        if (extension == ".xisf") Assert.Equal("XISF0100", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(request.Output), 0, 8));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".seiza-color-*"));
    }

    [Fact]
    public async Task CalibratedMarkerLinksPreviewAndSixteenBitExport()
    {
        var request = Request(".fits", false);
        await ColorWorkflowService.RunAsync(request);
        byte[] fits = File.ReadAllBytes(request.Output);
        int end = 0;
        while (System.Text.Encoding.ASCII.GetString(fits, end, 8).Trim() != "END") end += 80;
        // Add a valid calibration marker without changing the fixture pixels.
        byte[] cards = System.Text.Encoding.ASCII.GetBytes("SEIZAPCC= 'GAIA'".PadRight(80) + "END".PadRight(80));
        cards.CopyTo(fits, end);
        File.WriteAllBytes(request.Output, fits);
        var unlinked = FitsImageProcessingConfiguration.Default;
        var effective = ColorWorkflowService.ProcessingFor(request.Output, unlinked);
        Assert.All(effective.StretchStack.Stages, stage => Assert.NotEqual(FitsStretchColorStrategy.Unlinked, stage.ColorStrategy));
        Assert.Equal(3, SeizaCore.Render(request.Output, 64, unlinked).Metadata.Planes);
        Assert.Equal(3, SeizaCore.Render16(request.Output, unlinked).Metadata.Planes);
    }

    [Fact]
    public void ConvertsSolverCoordinatesToFitsThroughNativeWcsApi()
    {
        var cards = ColorWorkflowService.HeadersFor(new SolveResult { Wcs = new WcsResult {
            Crval = [120, 30], Crpix = [599.5, 499.5], Cd = [[-0.001, 0], [0, 0.001]] } });
        Assert.Equal(600.5, cards["CRPIX1"]!.GetValue<double>());
        Assert.Equal(500.5, cards["CRPIX2"]!.GetValue<double>());
        Assert.Equal(120, cards["CRVAL1"]!.GetValue<double>());
    }

    [Fact]
    public void RejectsEmptyRgbAndRepeatedChannels()
    {
        var request = Request(".fits", false);
        Assert.Throws<InvalidOperationException>(() => ColorWorkflowService.Validate(request with { Rgb = "", Calibrate = true }));
        Assert.Throws<InvalidOperationException>(() => ColorWorkflowService.Validate(request with { Blue = request.Red }));
    }

    [Fact]
    public void PublishedCoreProvidesStableStackingOutputVersion()
    {
        string? version = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Interop.NativeMethods.GetStackingOutputVersion());
        Assert.True(Version.TryParse(version, out _));
    }

    [Fact]
    public async Task UnsolvedCalibrationPreservesDestinationAndReportsWhy()
    {
        var request = Request(".fits", false) with { Calibrate = true };
        File.WriteAllText(request.Output, "keep destination");
        var result = await ColorWorkflowService.RunAsync(request);
        Assert.Null(result.Output);
        Assert.Contains("no astrometric solution", result.Message);
        Assert.Equal("keep destination", File.ReadAllText(request.Output));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".seiza-color-*"));
    }

    [Fact]
    public async Task RejectsUnalignedOrSourceOverwriteAndPreservesOutputOnDimensionFailure()
    {
        var request = Request(".fits", false);
        Assert.Throws<InvalidOperationException>(() => ColorWorkflowService.Validate(request with { Aligned = false }));
        Assert.Throws<InvalidOperationException>(() => ColorWorkflowService.Validate(request with { Output = request.Red! }));
        File.WriteAllText(request.Output, "original");
        await Assert.ThrowsAsync<SeizaCoreException>(() => ColorWorkflowService.RunAsync(request with { Blue = Light("small", side: 64) }));
        Assert.Equal("original", File.ReadAllText(request.Output));
    }

    [Fact]
    public void ColorPolicyLinksEveryUnlinkedStageWithoutMutatingTheDraft()
    {
        var draft = new FitsImageProcessingConfiguration(new FitsStretchStack([
            new() { Type = FitsStretchType.AutoMtf },
            new() { Type = FitsStretchType.MedianAnchor },
            new() { Type = FitsStretchType.Layered, ColorStrategy = FitsStretchColorStrategy.LuminancePreserving },
        ]), null);
        var linked = ColorWorkflowService.PreserveColor(draft);
        Assert.Equal(FitsStretchColorStrategy.Unlinked, draft.StretchStack.Stages[0].ColorStrategy);
        Assert.Equal(FitsStretchColorStrategy.Linked, linked.StretchStack.Stages[0].ColorStrategy);
        Assert.Equal(FitsStretchColorStrategy.Linked, linked.StretchStack.Stages[1].ColorStrategy);
        Assert.Equal(FitsStretchColorStrategy.LuminancePreserving, linked.StretchStack.Stages[2].ColorStrategy);
    }

    [Fact]
    public async Task LearnsNativeDefectMapAndPassesItIntoStackOptions()
    {
        string[] paths = Enumerable.Range(0, 4).Select(i => Light("dither" + i, i)).ToArray();
        var options = new ImageStackOptions { LearnDefects = true };
        var prepared = await StackDefectService.PrepareAsync(paths, options, new(), default);
        Assert.Null(options.LearnedDefects);
        using var json = JsonDocument.Parse(prepared.ToJson());
        Assert.True(json.RootElement.GetProperty("defects").GetProperty("frames").GetInt32() >= 3);
        await using var stack = await ImageStackSession.OpenAsync(paths[0], prepared, new());
        Assert.Equal(1, (await stack.GetCountsAsync()).AcceptedFrames);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StackDefectService.PrepareAsync(paths[..2], options, new(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => StackDefectService.PrepareAsync(paths, options, new() { DarkPath = paths[0] }, default));
    }

    [Fact]
    public async Task LiveDefectLearningWaitsForEnoughLightsThenIngestsAllOfThem()
    {
        string lights = Path.Combine(_root, "arriving");
        Directory.CreateDirectory(lights);
        File.Move(Light("first"), Path.Combine(lights, "first.fits"));
        var config = new LiveStackRunConfiguration { WatchFolder = lights, SessionRootDirectory = Path.Combine(_root, "session"),
            PreviewMaxDimension = 64, Options = new() { LearnDefects = true, RemoveTransients = false } };
        await using var session = new LiveStackCoordinator(config);
        await session.StartAsync();
        await Task.Delay(1500);
        Assert.Equal(0, session.CurrentSnapshot.AcceptedFrames);
        for (int i = 1; i < 4; i++) File.Move(Light("later" + i, i), Path.Combine(lights, "later" + i + ".fits"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (session.CurrentSnapshot.AcceptedFrames != 4) await Task.Delay(100, timeout.Token);
        await session.PauseAndSaveAsync();
    }

    [Fact]
    public async Task LearnedLiveStackResumesItsMapWithoutSourceFiles()
    {
        string lights = Path.Combine(_root, "LIGHT");
        Directory.CreateDirectory(lights);
        for (int i = 0; i < 4; i++) File.Move(Light("capture" + i, i), Path.Combine(lights, "capture" + i + ".fits"));
        var config = new LiveStackRunConfiguration { WatchFolder = lights, SessionRootDirectory = Path.Combine(_root, "session"),
            PreviewMaxDimension = 64, Options = new() { LearnDefects = true, RemoveTransients = false } };
        await using (var session = new LiveStackCoordinator(config))
        {
            await session.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (session.CurrentSnapshot.AcceptedFrames != 4) await Task.Delay(100, timeout.Token);
            await session.PauseAndSaveAsync();
        }
        foreach (string path in Directory.EnumerateFiles(lights)) File.Delete(path);
        await using var restored = new LiveStackCoordinator(config);
        await restored.StartAsync();
        Assert.Equal(4, restored.CurrentSnapshot.AcceptedFrames);
        string replacement = Light("replacement-master");
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.UpdateCalibrationAsync(new() { DarkPath = replacement }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.UpdateCalibrationAsync(new() { BiasPath = replacement }));
        await restored.PauseAndSaveAsync();
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
