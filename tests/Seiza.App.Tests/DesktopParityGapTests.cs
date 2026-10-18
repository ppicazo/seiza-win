using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed partial class DesktopParityGapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Seiza-parity-" + Guid.NewGuid().ToString("N"));
    public DesktopParityGapTests() => Directory.CreateDirectory(_root);

    private LiveStackRunConfiguration Configuration()
    {
        string lights = Path.Combine(_root, "LIGHT");
        Directory.CreateDirectory(lights);
        return new() { WatchFolder = lights, SessionRootDirectory = Path.Combine(_root, "session"),
            PreviewMaxDimension = 64, Options = new() { RemoveTransients = false } };
    }

    private static void WriteLight(LiveStackRunConfiguration config, string name) =>
        StackingOptionsNativeTests.WriteStarField(Path.Combine(config.WatchFolder, name), 0, false, 128);

    [Fact]
    public void LayeredDefaultLiveRecipeRendersMonoAndPreservesPhysicalMapping()
    {
        var config = Configuration();
        WriteLight(config, "mono.fits");
        string json = LiveStackRunConfiguration.PreviewProcessingJsonFor(FitsStretchType.Layered);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("linked", document.RootElement.GetProperty("stretch")[0].GetProperty("color_strategy").GetString());
        Assert.Equal("physical-linear", document.RootElement.GetProperty("sample_domain").GetProperty("type").GetString());
        var processing = new FitsImageProcessingConfiguration(new FitsStretchStack([
            new FitsStretchConfiguration { Type = FitsStretchType.Layered, ColorStrategy = FitsStretchColorStrategy.Linked }]), null);
        var image = SeizaCore.Render16(Path.Combine(config.WatchFolder, "mono.fits"), processing);
        Assert.Equal(128, image.Width);
        Assert.Throws<ArgumentOutOfRangeException>(() => LiveStackRunConfiguration.PreviewProcessingJsonFor(FitsStretchType.Identity));
    }

    [Fact]
    public async Task LiveAndPausedStretchChangesLeaveExportedLinearPixelsUnchangedAndReachLateGroups()
    {
        var config = Configuration();
        WriteLight(config, "first_R.fits");
        await using var session = new LiveStackSessionCoordinator(config);
        await session.StartAsync();
        await Until(() => session.Groups.Count == 1 && session.Groups[0].Coordinator.CurrentSnapshot.Preview is not null);
        var red = session.Groups[0].Coordinator;
        await session.SetPreviewStretchAsync(FitsStretchType.Layered);
        Assert.Equal(LiveStackRunConfiguration.PreviewProcessingJsonFor(FitsStretchType.Layered), red.PreviewProcessingJson);
        WriteLight(config, "first_B.fits");
        await Until(() => session.Groups.Count == 2 && session.Groups.All(group => group.Coordinator.CurrentSnapshot.Preview is not null));
        Assert.All(session.Groups, group => Assert.Equal(red.PreviewProcessingJson, group.Coordinator.PreviewProcessingJson));
        await session.PauseAndSaveAsync();
        string before = Path.Combine(_root, "before.fits");
        await red.SaveSnapshotAsync(before);
        int accepted = red.CurrentSnapshot.AcceptedFrames;
        foreach (FitsStretchType type in new[] { FitsStretchType.AutoMtf, FitsStretchType.MedianAnchor, FitsStretchType.Layered })
        {
            var oldPreview = red.CurrentSnapshot.Preview;
            await session.SetPreviewStretchAsync(type);
            Assert.NotSame(oldPreview, red.CurrentSnapshot.Preview);
            Assert.Equal(accepted, red.CurrentSnapshot.AcceptedFrames);
            Assert.Equal(LiveStackRunState.Paused, red.CurrentSnapshot.State);
        }
        string after = Path.Combine(_root, "after.fits");
        await red.SaveSnapshotAsync(after);
        Assert.Equal(await File.ReadAllBytesAsync(before), await File.ReadAllBytesAsync(after));
        Assert.DoesNotContain(red.CurrentSnapshot.Attention, item => item.Message.Contains("preview could not", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompletionRegistryFailureKeepsCheckpointEligibleForRecovery()
    {
        var config = Configuration();
        WriteLight(config, "first_L.fits");
        await using (var session = new LiveStackSessionCoordinator(config))
        {
            await session.StartAsync();
            await Until(() => session.Groups.Count == 1 && session.Groups[0].Coordinator.CurrentSnapshot.AcceptedFrames == 1);
            // Make the registry's atomic staging path unavailable after discovery.
            string temporary = Path.Combine(config.SessionRootDirectory, "filters.json.tmp");
            using (var blocked = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await Assert.ThrowsAnyAsync<IOException>(() => session.Groups[0].Coordinator.FinishAsync(Path.Combine(_root, "finished.fits")));
                Assert.False(session.Groups[0].Completed);
                Assert.True(session.Groups[0].Coordinator.CurrentSnapshot.RequiresReopenToResume);
                Assert.Empty(Directory.EnumerateFiles(config.SessionRootDirectory, "completed.json", SearchOption.AllDirectories));
            }
            File.Delete(temporary);
        }
        await using var resumed = new LiveStackSessionCoordinator(config);
        await resumed.StartAsync();
        await Until(() => resumed.Groups.Count == 1 && resumed.Groups[0].Coordinator.CurrentSnapshot.AcceptedFrames == 1);
        Assert.False(resumed.Groups[0].Completed);
    }

    [WindowsStackSourceIdentityFact]
    public async Task ExportsProtectEntireCheckpointTreeAndUnadmittedCaptureAliases()
    {
        var config = Configuration();
        Directory.CreateDirectory(config.SessionRootDirectory);
        string metadata = Path.Combine(config.SessionRootDirectory, "sibling", "current.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        File.WriteAllText(metadata, "checkpoint metadata");
        WriteLight(config, "not-yet-discovered_L.fits");
        await using var session = new LiveStackSessionCoordinator(config);
        Assert.Throws<InvalidOperationException>(() => session.ValidateOutputPath(metadata));
        Assert.Throws<InvalidOperationException>(() => session.ValidateOutputPath(Path.Combine(config.SessionRootDirectory, "filters.json")));
        string alias = Path.Combine(_root, "alias.fits");
        Assert.True(CreateHardLink(alias, metadata, 0));
        Assert.Throws<InvalidOperationException>(() => session.ValidateOutputPath(alias));
        File.Delete(alias);
        Assert.True(CreateHardLink(alias, Path.Combine(config.WatchFolder, "not-yet-discovered_L.fits"), 0));
        Assert.Throws<InvalidOperationException>(() => session.ValidateOutputPath(alias));
        Assert.Equal("checkpoint metadata", File.ReadAllText(metadata));
        session.ValidateOutputPath(Path.Combine(_root, "safe.fits"));
    }

    [Fact]
    public async Task ChildExportCannotBypassProtectionForAnotherFiltersCheckpoint()
    {
        var config = Configuration();
        WriteLight(config, "first_L.fits");
        WriteLight(config, "first_R.fits");
        await using var session = new LiveStackSessionCoordinator(config);
        await session.StartAsync();
        await Until(() => session.Groups.Count == 2 && session.Groups.All(group => group.Coordinator.CurrentSnapshot.AcceptedFrames == 1));
        await session.PauseAndSaveAsync();
        var left = session.Groups[0].Coordinator;
        var right = session.Groups[1].Coordinator;
        string protectedPath = Path.Combine(config.SessionRootDirectory,
            LiveStackSessionStore.SafeGroupDirectoryName("filter-" + session.Groups[1].Filter.Key), "current.json");
        byte[] original = await File.ReadAllBytesAsync(protectedPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => left.SaveSnapshotAsync(protectedPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(protectedPath));
        Assert.Equal(1, right.CurrentSnapshot.AcceptedFrames);
    }

    [Fact]
    public void BrowserGroupingPersistsWithoutReplacingOtherSettingsAndUnknownValuesFallBack()
    {
        string path = Path.Combine(_root, "settings.json");
        CatalogSettingsStore.SaveSettings(new() { RcAstroExecutable = "rc-astro.exe", CatalogDirectory = "catalog", AutomaticallyCheckForUpdates = false }, path);
        foreach (ImageBrowserGrouping grouping in Enum.GetValues<ImageBrowserGrouping>())
        {
            CatalogSettingsStore.SaveBrowserGrouping(grouping, path);
            Assert.Equal(grouping, CatalogSettingsStore.LoadBrowserGrouping(path));
            Assert.Equal("rc-astro.exe", CatalogSettingsStore.LoadSettings(path).RcAstroExecutable);
            Assert.Equal("catalog", CatalogSettingsStore.LoadSettings(path).CatalogDirectory);
            Assert.False(CatalogSettingsStore.LoadSettings(path).AutomaticallyCheckForUpdates);
        }
        CatalogSettingsStore.SaveSettings(new() { BrowserGrouping = "future-mode" }, path);
        Assert.Equal(ImageBrowserGrouping.Folder, CatalogSettingsStore.LoadBrowserGrouping(path));
    }

    [Fact]
    public async Task CrossFilterMasterOutsideCaptureAndCheckpointTreesCannotBeOverwritten()
    {
        var config = Configuration();
        WriteLight(config, "first_L.fits");
        WriteLight(config, "first_R.fits");
        string bias = Path.Combine(_root, "red-master-bias.fits");
        byte[] master = await File.ReadAllBytesAsync(Path.Combine(config.WatchFolder, "first_R.fits"));
        System.Text.Encoding.ASCII.GetBytes("IMAGETYP= 'Bias Frame'".PadRight(80)).CopyTo(master, 6 * 80);
        Array.Clear(master, 2880, master.Length - 2880);
        await File.WriteAllBytesAsync(bias, master);
        await using var session = new LiveStackSessionCoordinator(config, (reference, _) =>
            Task.FromResult(new CalibrationPreparationResult { Summaries = [], Warnings = [],
                Calibration = new() { BiasPath = LiveStackFilterIdentity.FromProbe(reference).Key == "red" ? bias : null } }));
        await session.StartAsync();
        await Until(() => session.Groups.Count == 2 && session.Groups.All(group => group.Coordinator.CurrentSnapshot.AcceptedFrames == 1));
        var luminance = session.Groups.Single(group => group.Filter.Key == "luminance").Coordinator;
        await Assert.ThrowsAsync<ArgumentException>(() => luminance.SaveSnapshotAsync(bias));
        Assert.Equal(master, await File.ReadAllBytesAsync(bias));
    }

    [Fact]
    public async Task SimultaneousFinishesAndDiscoverySerializeRegistryPublication()
    {
        var config = Configuration();
        WriteLight(config, "first_L.fits");
        WriteLight(config, "first_R.fits");
        await using var session = new LiveStackSessionCoordinator(config);
        await session.StartAsync();
        await Until(() => session.Groups.Count == 2 && session.Groups.All(group => group.Coordinator.CurrentSnapshot.AcceptedFrames == 1));
        var finishes = session.Groups.Select(group => group.Coordinator.FinishAsync(Path.Combine(_root, group.Filter.Key + ".fits"))).ToArray();
        WriteLight(config, "first_B.fits");
        await Task.WhenAll(finishes);
        await Until(() => session.Groups.Count == 3 && session.Groups.Single(group => group.Filter.Key == "blue").Coordinator.CurrentSnapshot.AcceptedFrames == 1);
        using var registry = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(config.SessionRootDirectory, "filters.json")));
        var entries = registry.RootElement.GetProperty("Filters").EnumerateArray().ToArray();
        Assert.Equal(3, entries.Length);
        Assert.Equal(2, entries.Count(entry => entry.GetProperty("Completed").GetBoolean()));
        Assert.False(File.Exists(Path.Combine(config.SessionRootDirectory, "filters.json.tmp")));
    }

    [Fact]
    public void SettingsChangesCoalesceUntilNativeWorkIsIdleAndNeverApplyToClosedWindows()
    {
        var update = new DeferredSettingsUpdate();
        update.Request();
        Assert.False(update.TryTake(closed: false, busy: true, activeJobs: 0));
        update.Request();
        Assert.False(update.TryTake(closed: false, busy: false, activeJobs: 2));
        Assert.False(update.TryTake(closed: false, busy: false, activeJobs: 1));
        Assert.True(update.TryTake(closed: false, busy: false, activeJobs: 0));
        Assert.False(update.TryTake(closed: false, busy: false, activeJobs: 0));
        update.Request();
        Assert.False(update.TryTake(closed: true, busy: false, activeJobs: 0));
        Assert.False(update.TryTake(closed: false, busy: false, activeJobs: 0));
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        while (!ready()) await Task.Delay(100, timeout.Token);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
