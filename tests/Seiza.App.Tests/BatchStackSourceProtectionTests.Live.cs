using System.Security.Cryptography;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed partial class BatchStackSourceProtectionTests
{
    [WindowsStackSourceIdentityFact]
    public void FinalPathResolverFollowsJunctionsForExistingAndFutureChildren()
    {
        string target = PathFor("target");
        Directory.CreateDirectory(target);
        string existing = Path.Combine(target, "existing.fits");
        File.WriteAllText(existing, "original source");
        string junction = CreateJunction(target);

        Assert.True(LiveStackPath.Equals(existing,
            WindowsFileIdentity.TryResolveFinalPath(Path.Combine(junction, "existing.fits"))));
        Assert.True(LiveStackPath.Equals(Path.Combine(target, "future", "output.fits"),
            WindowsFileIdentity.TryResolveFinalPath(Path.Combine(junction, "future", "output.fits"))));
        Assert.False(Directory.Exists(Path.Combine(target, "future")));
        Assert.Equal("original source", File.ReadAllText(existing));
    }

    [WindowsStackSourceIdentityFact]
    public async Task LiveOutputsProtectManagedPolicyRejectedSourcesThroughJunctions()
    {
        string captures = PathFor("captures");
        Directory.CreateDirectory(captures);
        string initial = PathFor("initial.fits");
        string rejected = Path.Combine(captures, "incompatible.fits");
        StackingOptionsNativeTests.WriteStarField(initial, 0, bayer: false);
        StackingOptionsNativeTests.WriteStarField(rejected, 0, bayer: false, side: 128);
        byte[] original = SHA256.HashData(File.ReadAllBytes(rejected));
        var configuration = LiveConfiguration(captures, initial);
        await using var coordinator = new LiveStackCoordinator(configuration);
        var rejectedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Changed += (_, _) =>
        {
            if (coordinator.CurrentSnapshot.Frames.Any(frame =>
                LiveStackPath.Equals(frame.Path, rejected) && frame.Disposition == LiveStackPersistedFrameDisposition.Rejected))
            {
                rejectedSeen.TrySetResult();
            }
        };

        await coordinator.StartAsync();
        await rejectedSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await coordinator.PauseAndSaveAsync();
        string group = Path.Combine(configuration.SessionRootDirectory,
            LiveStackSessionStore.SafeGroupDirectoryName(configuration.GroupId));
        string context = Directory.EnumerateFiles(group, "*.seiza-stack").Order(StringComparer.Ordinal).Last();
        await using (ImageStackSession native = await ImageStackSession.ResumeAsync(context))
        {
            Assert.DoesNotContain((await native.GetStateAsync()).InputPaths, path => LiveStackPath.Equals(path, rejected));
        }
        string alias = Path.Combine(CreateJunction(captures), Path.GetFileName(rejected));

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.SaveSnapshotAsync(alias));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.FinishAsync(alias));

        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(rejected)));
        Assert.Equal(1, coordinator.CurrentSnapshot.AcceptedFrames);
        Assert.Equal(1, coordinator.CurrentSnapshot.RejectedFrames);
    }

    [WindowsStackSourceIdentityFact]
    public async Task LiveOutputsRejectExistingAndFutureCheckpointPathsThroughJunctions()
    {
        string captures = PathFor("captures");
        Directory.CreateDirectory(captures);
        string initial = PathFor("initial.fits");
        StackingOptionsNativeTests.WriteStarField(initial, 0, bayer: false);
        var configuration = LiveConfiguration(captures, initial);
        await using var coordinator = new LiveStackCoordinator(configuration);
        await coordinator.StartAsync();
        await coordinator.PauseAndSaveAsync();
        string group = Path.Combine(configuration.SessionRootDirectory,
            LiveStackSessionStore.SafeGroupDirectoryName(configuration.GroupId));
        string context = Directory.EnumerateFiles(group, "*.seiza-stack").Order(StringComparer.Ordinal).Last();
        byte[] original = SHA256.HashData(File.ReadAllBytes(context));
        string junction = CreateJunction(group);
        string existingAlias = Path.Combine(junction, Path.GetFileName(context));
        string futureAlias = Path.Combine(junction, "future-output.fits");

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.SaveSnapshotAsync(existingAlias));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.SaveSnapshotAsync(futureAlias));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.FinishAsync(existingAlias));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.FinishAsync(futureAlias));

        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(context)));
        Assert.False(File.Exists(futureAlias));
        Assert.Equal(1, coordinator.CurrentSnapshot.AcceptedFrames);
    }

    [WindowsStackSourceIdentityFact]
    public async Task NativeSnapshotWritersRunAdditionalValidationImmediatelyBeforePublication()
    {
        string light = PathFor("light.fits");
        StackingOptionsNativeTests.WriteStarField(light, 0, bayer: false);
        await using ImageStackSession session = await ImageStackSession.OpenAsync(light,
            new ImageStackOptions(), new ImageStackCalibration());

        await using (ImageStackExportSnapshot export = await session.ExportSnapshotAsync())
        {
            await AssertFinalValidationAsync((output, validate) => export.WriteFitsAsync(output, validate));
        }
        await using (ImageStackSnapshot snapshot = await session.SnapshotAsync())
        {
            await AssertFinalValidationAsync((output, validate) => snapshot.WriteFitsAsync(output, null, validate));
        }

        async Task AssertFinalValidationAsync(Func<string, Action, Task> write)
        {
            string output = PathFor(Guid.NewGuid().ToString("N") + ".fits");
            File.WriteAllText(output, "previous output");
            int validations = 0;
            await Assert.ThrowsAsync<ArgumentException>(() => write(output, () =>
            {
                if (++validations == 2) throw new ArgumentException("A managed source alias appeared during the native write.");
            }));
            Assert.Equal(2, validations);
            Assert.Equal("previous output", File.ReadAllText(output));
            Assert.Empty(Directory.EnumerateFiles(_directory, ".seiza-stack-*.fits"));
        }
    }

    private LiveStackRunConfiguration LiveConfiguration(string captures, string initial) => new()
    {
        WatchFolder = captures,
        SessionRootDirectory = PathFor("sessions"),
        InitialReferencePath = initial,
        Options = new ImageStackOptions { RemoveTransients = false },
        PreviewMaxDimension = 128,
    };
}
