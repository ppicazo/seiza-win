using System.Security.Cryptography;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class StackAlignmentReferenceNativeTests
{
    [StackReferenceImageFact]
    public async Task SharedReferenceStartsEmptyAndSurvivesPushCheckpointAndResume()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string shared = Path.Combine(directory, "shared.fits");
            string light = Path.Combine(directory, "light.fits");
            string second = Path.Combine(directory, "second.fits");
            string context = Path.Combine(directory, "stack.seiza-stack");
            StackingOptionsNativeTests.WriteStarField(shared, 0, bayer: false);
            StackingOptionsNativeTests.WriteStarField(light, 1, bayer: false);
            StackingOptionsNativeTests.WriteStarField(second, 2, bayer: false);
            byte[] original = SHA256.HashData(File.ReadAllBytes(shared));
            ImageStackReferenceOpenResult opened = await ImageStackSession.OpenWithReferenceImageAsync(
                shared, [light], new ImageStackOptions(), new ImageStackCalibration());
            await using (ImageStackSession session = opened.Session)
            {
                Assert.Equal(light, opened.Selection.ChannelReferencePath);
                Assert.Equal(light, session.SelectedReferencePath);
                Assert.Empty(opened.Selection.Skipped);
                LiveStackNativeState empty = await session.GetStateAsync();
                Assert.Equal(0, empty.AcceptedFrames);
                Assert.False(empty.ReferenceIntegrated);
                Assert.True(LiveStackPath.Equals(shared, empty.ReferenceImagePath));
                Assert.Empty(empty.InputPaths);

                Assert.True((await session.PushFrameAsync(light)).Disposition.Accepted);
                LiveStackNativeState pushed = await session.GetStateAsync();
                Assert.Equal(1, pushed.AcceptedFrames);
                Assert.True(LiveStackPath.Equals(light, Assert.Single(pushed.InputPaths)));
                Assert.True((await session.PushFrameAsync(second)).Disposition.Accepted);
                await Assert.ThrowsAsync<SeizaCoreException>(() => session.SaveContextAsync(shared));
                using ImageStackExportSnapshot export = await session.ExportSnapshotAsync();
                await AssertProtectedAsync(path => export.WriteFitsAsync(path), shared, light);
                using ImageStackSnapshot snapshot = await session.SnapshotAsync();
                await AssertProtectedAsync(path => snapshot.WriteFitsAsync(path), shared, light);
                using ImageStackSnapshot reintegrated = await session.ReintegrateAsync(0, 0, progress: null);
                await AssertProtectedAsync(path => reintegrated.WriteFitsAsync(path), shared, light);
                await session.SaveContextAsync(context);
                using ImageStackSnapshot finished = await session.FinishAsync();
                await AssertProtectedAsync(path => finished.WriteFitsAsync(path), shared, light);
            }

            await using ImageStackSession resumed = await ImageStackSession.ResumeAsync(context);
            LiveStackNativeState restored = await resumed.GetStateAsync();
            Assert.False(restored.ReferenceIntegrated);
            Assert.Equal(2, restored.AcceptedFrames);
            Assert.True(LiveStackPath.Equals(shared, restored.ReferenceImagePath));
            using ImageStackExportSnapshot resumedExport = await resumed.ExportSnapshotAsync();
            await AssertProtectedAsync(path => resumedExport.WriteFitsAsync(path), shared, light);
            Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(shared)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublishedCoreSnapshotsCannotOverwriteLightsThroughAtomicStaging()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string first = Path.Combine(directory, "first.fits");
            string second = Path.Combine(directory, "second.fits");
            StackingOptionsNativeTests.WriteStarField(first, 0, bayer: false);
            StackingOptionsNativeTests.WriteStarField(second, 1, bayer: false);
            byte[] original = SHA256.HashData(File.ReadAllBytes(first));
            await using ImageStackSession session = await ImageStackSession.OpenAsync(
                first, new ImageStackOptions(), new ImageStackCalibration());
            Assert.True((await session.PushFrameAsync(second)).Disposition.Accepted);
            using ImageStackExportSnapshot export = await session.ExportSnapshotAsync();
            await AssertProtectedAsync(path => export.WriteFitsAsync(path), first, second);
            using ImageStackSnapshot snapshot = await session.SnapshotAsync();
            await AssertProtectedAsync(path => snapshot.WriteFitsAsync(path), first, second);
            using ImageStackSnapshot reintegrated = await session.ReintegrateAsync(0, 0, progress: null);
            await AssertProtectedAsync(path => reintegrated.WriteFitsAsync(path), first, second);
            using ImageStackSnapshot finished = await session.FinishAsync();
            await AssertProtectedAsync(path => finished.WriteFitsAsync(path), first, second);
            Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(first)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertProtectedAsync(Func<string, Task> write, params string[] paths)
    {
        foreach (string path in paths)
        {
            await Assert.ThrowsAsync<SeizaCoreException>(() => write(path));
        }
    }

    [StackReferenceImageCandidatesFact]
    public async Task AlignmentFallbackReportsSkippedLightAndReturnsAnEmptyStack()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            const int side = 256;
            string shared = Path.Combine(directory, "shared.fits");
            string turned = Path.Combine(directory, "turned.fits");
            string usable = Path.Combine(directory, "usable.fits");
            StackingOptionsNativeTests.WriteStarField(shared, 0, bayer: false, side);
            StackingOptionsNativeTests.WriteStarField(usable, 1, bayer: false, side);
            byte[] source = File.ReadAllBytes(usable);
            byte[] rotated = (byte[])source.Clone();
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    source.AsSpan(2880 + (y * side + x) * sizeof(float), sizeof(float)).CopyTo(
                        rotated.AsSpan(2880 + (x * side + side - y - 1) * sizeof(float), sizeof(float)));
                }
            }
            File.WriteAllBytes(turned, rotated);

            ImageStackReferenceOpenResult opened = await ImageStackSession.OpenWithReferenceImageAsync(
                shared, [turned, usable], new ImageStackOptions(), new ImageStackCalibration());
            await using ImageStackSession session = opened.Session;
            Assert.Equal(1, opened.Selection.ChannelReferenceIndex);
            Assert.Equal(usable, opened.Selection.ChannelReferencePath);
            StackAlignmentReferenceSkip skipped = Assert.Single(opened.Selection.Skipped);
            Assert.Equal(turned, skipped.Path);
            Assert.False(string.IsNullOrWhiteSpace(skipped.Reason));
            Assert.Equal(0, (await session.GetCountsAsync()).AcceptedFrames);
            Assert.True((await session.PushFrameAsync(usable)).Disposition.Accepted);
            Assert.False((await session.PushFrameAsync(turned)).Disposition.Accepted);
            Assert.Equal(1, (await session.GetCountsAsync()).AcceptedFrames);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
