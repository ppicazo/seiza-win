using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StackingGpuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Seiza-gpu-settings-" + Guid.NewGuid().ToString("N"));
    public StackingGpuTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MissingAndLegacySettingsDefaultToAutomaticAndChangesPreserveOtherSettings()
    {
        string path = Path.Combine(_root, "settings.json");
        Assert.True(CatalogSettingsStore.LoadUseGpuForStacking(path));
        File.WriteAllText(path, "{\"CatalogDirectory\":\"catalogs\",\"BrowserGrouping\":\"Filter\",\"AutomaticallyCheckForUpdates\":false,\"SkippedUpdateVersion\":\"0.8.5\"}");
        Assert.True(CatalogSettingsStore.LoadUseGpuForStacking(path));
        CatalogSettingsStore.SaveUseGpuForStacking(false, path);
        Assert.False(CatalogSettingsStore.LoadUseGpuForStacking(path));
        var settings = CatalogSettingsStore.LoadSettings(path);
        Assert.Equal("catalogs", settings.CatalogDirectory);
        Assert.Equal("Filter", settings.BrowserGrouping);
        Assert.False(settings.AutomaticallyCheckForUpdates);
        Assert.Equal("0.8.5", settings.SkippedUpdateVersion);
        CatalogSettingsStore.SaveBrowserGrouping(ImageBrowserGrouping.Folder, path);
        Assert.False(CatalogSettingsStore.LoadUseGpuForStacking(path));
        CatalogSettingsStore.SaveUseGpuForStacking(true, path);
        Assert.True(CatalogSettingsStore.LoadUseGpuForStacking(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeStackAndDefectOptionsAgreeAndSnapshotsRetainTheirMode(bool enabled)
    {
        var options = new ImageStackOptions { UseGpu = enabled };
        using var stack = JsonDocument.Parse(options.ToJson());
        if (enabled) Assert.False(stack.RootElement.TryGetProperty("gpu", out _));
        else Assert.False(stack.RootElement.GetProperty("gpu").GetProperty("enabled").GetBoolean());
        using var defects = JsonDocument.Parse(StackDefectService.DetectionOptionsJson(options));
        Assert.Equal(enabled, defects.RootElement.GetProperty("gpu").GetProperty("enabled").GetBoolean());
        var snapshot = options.Copy();
        options.UseGpu = !enabled;
        Assert.Equal(enabled, snapshot.UseGpu);
        Assert.Equal(enabled, ImageStackOptions.SavedUsesGpu(snapshot.ToJson()));
    }

    [Fact]
    public void OnlyGpuExecutionPolicyIsExcludedFromResumeCompatibility()
    {
        var cpu = new ImageStackOptions { UseGpu = false };
        var gpu = new ImageStackOptions { UseGpu = true, TraceGpu = true };
        Assert.True(ImageStackOptions.SameProcessingOptions(cpu.ToJson(), gpu.ToJson()));
        gpu.SigmaLow = 4;
        Assert.False(ImageStackOptions.SameProcessingOptions(cpu.ToJson(), gpu.ToJson()));
        Assert.False(ImageStackOptions.SameProcessingOptions("not JSON", gpu.ToJson()));
        Assert.False(ImageStackOptions.SameProcessingOptions("null", gpu.ToJson()));
        Assert.False(ImageStackOptions.SameProcessingOptions("{\"gpu\":{\"enabled\":\"bad\"}}", "{}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumingAfterSettingsChangeKeepsTheSavedModeAndAccumulator(bool originalGpu)
    {
        string watch = Path.Combine(_root, "lights"); Directory.CreateDirectory(watch);
        string reference = Path.Combine(_root, "reference.fits");
        StackingOptionsNativeTests.WriteStarField(reference, 0, bayer: false);
        var config = new LiveStackRunConfiguration { WatchFolder = watch, InitialReferencePath = reference,
            SessionRootDirectory = Path.Combine(_root, "session"), GroupId = "gpu", PreviewMaxDimension = 64,
            Options = new() { UseGpu = originalGpu, RemoveTransients = false } };
        await using (var first = new LiveStackCoordinator(config))
        {
            await first.StartAsync(); await first.PauseAndSaveAsync();
            Assert.Equal(1, first.CurrentSnapshot.AcceptedFrames);
        }
        File.Delete(reference);
        await using (var restored = new LiveStackCoordinator(config with
        { InitialReferencePath = null, Options = new() { UseGpu = !originalGpu, RemoveTransients = false } }))
        {
            await restored.StartAsync(); await restored.PauseAndSaveAsync();
            Assert.Equal(1, restored.CurrentSnapshot.AcceptedFrames);
            Assert.Contains(restored.CurrentSnapshot.Attention, item => item.Message.Contains("saved GPU setting"));
        }
        using var store = new LiveStackSessionStore(Path.Combine(config.SessionRootDirectory, LiveStackSessionStore.SafeGroupDirectoryName(config.GroupId)));
        var generations = await store.GetRestoreCandidatesAsync();
        Assert.Equal(originalGpu, ImageStackOptions.SavedUsesGpu(generations[0].State.StackOptionsJson));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
