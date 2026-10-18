using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class SessionUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Seiza-session-tests-" + Guid.NewGuid().ToString("N"));
    public SessionUpgradeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void RcAstroSettingSurvivesReloadAndOtherSettingsChanges()
    {
        string path = Path.Combine(_root, "settings.json");
        // Older settings lack the new field and continue to load.
        File.WriteAllText(path, "{\"CatalogDirectory\":\"catalogs\",\"AutomaticallyCheckForUpdates\":false}");
        var settings = CatalogSettingsStore.LoadSettings(path);
        Assert.Null(settings.RcAstroExecutable);
        CatalogSettingsStore.SaveSettings(settings with { RcAstroExecutable = @"C:\tools with spaces\rc-astro.exe" }, path);
        var restored = CatalogSettingsStore.LoadSettings(path);
        Assert.Equal(@"C:\tools with spaces\rc-astro.exe", restored.RcAstroExecutable);
        Assert.False(restored.AutomaticallyCheckForUpdates);
        CatalogSettingsStore.SaveSettings(restored with { CatalogDirectory = "new-catalogs" }, path);
        Assert.Equal(restored.RcAstroExecutable, CatalogSettingsStore.LoadSettings(path).RcAstroExecutable);
        CatalogSettingsStore.SaveSettings(restored with { RcAstroExecutable = null }, path);
        Assert.Null(CatalogSettingsStore.LoadSettings(path).RcAstroExecutable);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("MedianAnchor")]
    [InlineData("Layered")]
    public void NewStretchParametersRoundTripAndRenderThroughNativeCore(string method)
    {
        var stage = new FitsStretchConfiguration
        {
            Type = Enum.Parse<FitsStretchType>(method),
            ColorStrategy = FitsStretchColorStrategy.LuminancePreserving,
            TargetMedian = 0.27, BlackPointSigma = null, CurvesBoost = 0.21,
            HdrAmount = 0.4, HdrKnee = 0.7, Normalize = true,
            TargetBackground = 0.17, Aggressiveness = 0.7, ScaleSeparation = 16,
            DynamicRangeCompression = 0.3, ContrastRecovery = 0.4,
            SaturationAmount = 0.2, SaturationBoost = 0.8, LightnessMask = false,
        };
        var processing = new FitsImageProcessingConfiguration(new FitsStretchStack([stage]), null);
        var restored = FitsImageProcessingConfiguration.FromClipboardJson(processing.ToClipboardJson());
        Assert.Equal(processing.ToJson(), restored.ToJson());
        string source = Path.Combine(_root, "light.fits");
        StackingOptionsNativeTests.WriteStarField(source, 0, bayer: true, side: 128);
        var rendered = SeizaCore.Render(source, maxDimension: 64, processing: restored);
        Assert.Equal(64, rendered.Width);
        Assert.Equal(64 * 64 * 4, rendered.Bgra.Length);
        Assert.True(rendered.Bgra.Where((_, i) => i % 4 != 3).Distinct().Count() > 8);
        using JsonDocument preview = JsonDocument.Parse(LiveStackRunConfiguration.PreviewProcessingJsonFor(stage.Type));
        Assert.Equal("physical-linear", preview.RootElement.GetProperty("sample_domain").GetProperty("type").GetString());
    }

    [Fact]
    public void LayeredRejectsUnlinkedColorAndOutOfRangeScale()
    {
        var stage = new FitsStretchConfiguration { Type = FitsStretchType.Layered };
        Assert.NotNull(stage.ValidationMessage);
        stage.ColorStrategy = FitsStretchColorStrategy.Linked;
        Assert.Null(stage.ValidationMessage);
        stage.ScaleSeparation = 7;
        Assert.NotNull(stage.ValidationMessage);
    }

    [Fact]
    public async Task ParentFolderRunsSeparateFiltersDiscoversNewGroupsAndResumesWithoutSources()
    {
        string lights = Path.Combine(_root, "LIGHT");
        Directory.CreateDirectory(lights);
        string Write(string filter, int index)
        {
            string directory = Path.Combine(lights, filter);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"light_{index}_{filter}.fits");
            StackingOptionsNativeTests.WriteStarField(path, index, bayer: false, side: 128);
            return path;
        }
        string l = Write("L", 0);
        File.SetLastWriteTimeUtc(l, DateTime.UtcNow.AddMinutes(-5));
        string r = Write("R", 0);
        var configuration = new LiveStackRunConfiguration
        {
            WatchFolder = lights,
            SessionRootDirectory = Path.Combine(_root, "session"),
            IncludeSubdirectories = true,
            PreviewMaxDimension = 64,
            CheckpointAcceptedFrameInterval = 1,
            Options = new ImageStackOptions { RemoveTransients = false },
        };
        await using (var session = new LiveStackSessionCoordinator(configuration))
        {
            await session.StartAsync();
            await using (var duplicate = new LiveStackSessionCoordinator(configuration))
                await Assert.ThrowsAsync<IOException>(() => duplicate.StartAsync());
            await Until(() => session.Groups.Count == 2 && session.Groups.All(group => group.Coordinator.CurrentSnapshot.AcceptedFrames == 1));
            Write("R", 1);
            Write("B", 0);
            await Until(() => session.Groups.Count == 3 && session.Groups.Sum(group => group.Coordinator.CurrentSnapshot.AcceptedFrames) == 4);
            Assert.Equal(2, session.Groups.Single(group => group.Filter.Key == "red").Coordinator.CurrentSnapshot.AcceptedFrames);
            Assert.Equal(1, session.Groups.Single(group => group.Filter.Key == "luminance").Coordinator.CurrentSnapshot.AcceptedFrames);
            foreach (var group in session.Groups)
            {
                Assert.All(group.Coordinator.CurrentSnapshot.Frames,
                    frame => Assert.True(group.Filter.Matches(LiveStackFilterIdentity.FromStoredName(Path.GetFileName(Path.GetDirectoryName(frame.Path))))));
                Assert.Equal(0, group.Coordinator.CurrentSnapshot.IgnoredFrames);
            }
            Assert.Throws<InvalidOperationException>(() => session.ValidateOutputPath(Path.Combine(lights, "output.fits")));
            await session.PauseAndSaveAsync();
            Assert.True(session.IsPaused);
        }
        foreach (string file in Directory.EnumerateFiles(lights, "*.fits", SearchOption.AllDirectories)) File.Delete(file);
        await using var resumed = new LiveStackSessionCoordinator(configuration);
        await resumed.StartAsync();
        await Until(() => resumed.Groups.Count == 3 && resumed.Groups.Sum(group => group.Coordinator.CurrentSnapshot.AcceptedFrames) == 4);
        await resumed.PauseAndSaveAsync();
        Assert.All(resumed.Groups, group => Assert.Null(group.Error));
    }

    [Fact]
    public async Task UnfilteredResumesAndFinishedFilterStaysFinishedWhileOthersContinue()
    {
        string lights = Path.Combine(_root, "LIGHT");
        Directory.CreateDirectory(lights);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "capture.fits"), 0, bayer: false, side: 128);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "capture_L.fits"), 0, bayer: false, side: 128);
        var configuration = new LiveStackRunConfiguration
        {
            WatchFolder = lights, SessionRootDirectory = Path.Combine(_root, "session"),
            Options = new ImageStackOptions { RemoveTransients = false }, PreviewMaxDimension = 64,
        };
        await using (var session = new LiveStackSessionCoordinator(configuration))
        {
            await session.StartAsync();
            await Until(() => session.Groups.Count == 2 && session.Groups.All(group => group.Coordinator.CurrentSnapshot.AcceptedFrames == 1));
            var luminance = session.Groups.Single(group => group.Filter.Key == "luminance");
            await luminance.Coordinator.FinishAsync(Path.Combine(_root, "finished.fits"));
            StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "capture2.fits"), 1, bayer: false, side: 128);
            await Until(() => session.Groups.Single(group => group.Filter.Key == "unfiltered").Coordinator.CurrentSnapshot.AcceptedFrames == 2);
            await session.PauseAndSaveAsync();
        }
        await using var resumed = new LiveStackSessionCoordinator(configuration);
        await resumed.StartAsync();
        await Until(() => resumed.Groups.Count == 2 && resumed.Groups.Single(group => group.Filter.Key == "unfiltered").Coordinator.CurrentSnapshot.AcceptedFrames == 2);
        Assert.True(resumed.Groups.Single(group => group.Filter.Key == "luminance").Completed);
        Assert.Equal(0, resumed.Groups.Single(group => group.Filter.Key == "luminance").Coordinator.CurrentSnapshot.AcceptedFrames);
        await resumed.PauseAndSaveAsync();
        Assert.All(resumed.Groups, group => Assert.Null(group.Error));
    }

    [Fact]
    public async Task CalibrationFailureInOneFilterDoesNotStopAnother()
    {
        string lights = Path.Combine(_root, "LIGHT");
        Directory.CreateDirectory(lights);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "capture_L.fits"), 0, bayer: false, side: 128);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "capture_R.fits"), 0, bayer: false, side: 128);
        var configuration = new LiveStackRunConfiguration
        {
            WatchFolder = lights, SessionRootDirectory = Path.Combine(_root, "session"),
            Options = new ImageStackOptions { RemoveTransients = false }, PreviewMaxDimension = 64,
            ChooseReferenceAutomatically = true,
        };
        await using var session = new LiveStackSessionCoordinator(configuration, (reference, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (LiveStackFilterIdentity.FromProbe(reference).Key == "luminance")
                throw new IOException("Simulated inaccessible L calibration library");
            return Task.FromResult(new CalibrationPreparationResult
            {
                Calibration = new ImageStackCalibration(), Summaries = [], Warnings = [],
            });
        });
        await session.StartAsync();
        await Until(() => session.Groups.Count == 2 && session.Groups.Single(group => group.Filter.Key == "red").Coordinator.CurrentSnapshot.AcceptedFrames == 1);
        Assert.Equal(0, session.Groups.Single(group => group.Filter.Key == "luminance").Coordinator.CurrentSnapshot.AcceptedFrames);
        await Until(() => session.Groups.Single(group => group.Filter.Key == "luminance")
            .Coordinator.CurrentSnapshot.Attention.Any(attention => attention.Message.Contains("Simulated inaccessible", StringComparison.Ordinal)));
        StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "capture2_R.fits"), 1, bayer: false, side: 128);
        await Until(() => session.Groups.Single(group => group.Filter.Key == "red").Coordinator.CurrentSnapshot.AcceptedFrames == 2);
        await session.PauseAndSaveAsync();
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!ready()) await Task.Delay(100, timeout.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FlatReviewApprovalGatesOneFilterWithoutStoppingAnother(bool automaticReference)
    {
        string lights = Path.Combine(_root, "review-lights");
        Directory.CreateDirectory(lights);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "capture_L.fits"), 0, false, 128);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "capture_R.fits"), 0, false, 128);
        var approve = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        var configuration = new LiveStackRunConfiguration { WatchFolder = lights,
            SessionRootDirectory = Path.Combine(_root, "review-session"), PreviewMaxDimension = 64,
            ChooseReferenceAutomatically = automaticReference, Options = new() { RemoveTransients = false } };
        await using var session = new LiveStackSessionCoordinator(configuration, async (reference, token) =>
        {
            if (LiveStackFilterIdentity.FromProbe(reference).Key == "luminance")
            {
                Interlocked.Increment(ref requests);
                if (!await approve.Task.WaitAsync(token)) throw new OperationCanceledException("Flat review declined", token);
            }
            return new CalibrationPreparationResult { Calibration = new(), Summaries = [], Warnings = [] };
        });
        await session.StartAsync();
        await Until(() => session.Groups.Count == 2 && requests == 1 &&
            session.Groups.Single(group => group.Filter.Key == "red").Coordinator.CurrentSnapshot.AcceptedFrames == 1);
        var luminance = session.Groups.Single(group => group.Filter.Key == "luminance");
        Assert.Equal(0, luminance.Coordinator.CurrentSnapshot.AcceptedFrames);
        approve.SetResult(false);
        await Until(() => luminance.Error is not null || luminance.Coordinator.CurrentSnapshot.State == LiveStackRunState.Faulted);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "second_L.fits"), 1, false, 128);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(lights, "second_R.fits"), 1, false, 128);
        await Until(() => session.Groups.Single(group => group.Filter.Key == "red").Coordinator.CurrentSnapshot.AcceptedFrames == 2);
        Assert.Equal(0, luminance.Coordinator.CurrentSnapshot.AcceptedFrames);
        Assert.Equal(1, requests);
        await session.PauseAndSaveAsync();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
