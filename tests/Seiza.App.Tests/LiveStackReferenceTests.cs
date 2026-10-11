using Seiza.App.Models;
using Seiza.App.Services;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Seiza.App.Tests;

public sealed class LiveStackReferenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));

    public LiveStackReferenceTests() => Directory.CreateDirectory(_directory);

    [StackReferenceImageFact]
    public async Task SharedReferenceUsesOnlyChannelLightTelemetryAndRemainsProtectedAfterRestore()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string shared = Path.Combine(captures, "shared_L.fits");
        string channel = Path.Combine(_directory, "channel_Ha.fits");
        StackingOptionsNativeTests.WriteStarField(shared, 0, bayer: false);
        StackingOptionsNativeTests.WriteStarField(channel, 0, bayer: false);
        byte[] original = SHA256.HashData(File.ReadAllBytes(shared));
        var configuration = Configuration(captures) with
        {
            InitialReferencePath = channel,
            AlignmentReferencePath = shared,
        };
        await using (var originalRun = new LiveStackCoordinator(configuration))
        {
            await originalRun.StartAsync();
            await originalRun.PauseAndSaveAsync();
            Assert.Equal(1, originalRun.CurrentSnapshot.AcceptedFrames);
            Assert.Equal(60, originalRun.CurrentSnapshot.CumulativeExposureSeconds);
            Assert.Equal(channel, Assert.Single(originalRun.CurrentSnapshot.Frames).Path);
            Assert.Equal("H-alpha", originalRun.CurrentSnapshot.LockedFilter!.DisplayName);
            Assert.Equal(1, Assert.Single(originalRun.CurrentSnapshot.CalibrationHistory).StartsAtAcceptedFrame);
        }
        int scoreCalls = 0;
        Task<StackReferenceSelection> NeverScore(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            scoreCalls++;
            throw new InvalidOperationException("A restored shared-reference stack must not choose a new reference.");
        }
        await using var restored = new LiveStackCoordinator(
            configuration with { AlignmentReferencePath = null, InitialReferencePath = null, ChooseReferenceAutomatically = true },
            referenceSelector: NeverScore);
        await restored.StartAsync();
        await restored.PauseAndSaveAsync();
        Assert.Equal(0, scoreCalls);
        Assert.Equal(1, restored.CurrentSnapshot.AcceptedFrames);
        Assert.Equal(60, restored.CurrentSnapshot.CumulativeExposureSeconds);
        await Assert.ThrowsAsync<ArgumentException>(() => restored.SaveSnapshotAsync(shared));
        await Assert.ThrowsAsync<ArgumentException>(() => restored.FinishAsync(shared));
        await Assert.ThrowsAsync<ArgumentException>(() => restored.UpdateCalibrationAsync(
            new ImageStackCalibration { FlatPath = shared }));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(shared)));
    }

    [StackReferenceImageFact]
    public async Task ExplicitlyChangingTheSharedCanvasDoesNotResumeTheOldAccumulator()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string shared = Path.Combine(_directory, "shared.fits");
        string replacement = Path.Combine(_directory, "replacement.fits");
        string channel = Path.Combine(_directory, "channel.fits");
        foreach (string path in new[] { shared, replacement, channel })
        {
            StackingOptionsNativeTests.WriteStarField(path, 0, bayer: false);
        }
        var configuration = Configuration(captures) with { InitialReferencePath = channel, AlignmentReferencePath = shared };
        await using (var original = new LiveStackCoordinator(configuration))
        {
            await original.StartAsync();
            await original.PauseAndSaveAsync();
        }
        await using var changed = new LiveStackCoordinator(configuration with
        {
            AlignmentReferencePath = replacement,
            InitialReferencePath = null,
        });
        await changed.StartAsync();
        await changed.PauseAndSaveAsync();
        Assert.Equal(0, changed.CurrentSnapshot.AcceptedFrames);
        Assert.Contains(changed.CurrentSnapshot.Attention,
            item => item.Message.Contains("different alignment reference", StringComparison.Ordinal));
    }

    [StackReferenceImageFact]
    public async Task ChangingTheSharedCanvasKeepsTheNewestRejectedCheckpointAsThePreviousGeneration()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string shared = Path.Combine(_directory, "shared.fits");
        string replacement = Path.Combine(_directory, "replacement.fits");
        string channel = Path.Combine(_directory, "channel.fits");
        foreach (string path in new[] { shared, replacement, channel })
        {
            StackingOptionsNativeTests.WriteStarField(path, 0, bayer: false);
        }
        var configuration = Configuration(captures) with
        {
            InitialReferencePath = channel,
            AlignmentReferencePath = shared,
        };
        await using (var original = new LiveStackCoordinator(configuration))
        {
            await original.StartAsync();
            await original.PauseAndSaveAsync();
        }
        string groupDirectory = Path.Combine(configuration.SessionRootDirectory,
            LiveStackSessionStore.SafeGroupDirectoryName(configuration.GroupId));
        string pointerPath = Path.Combine(groupDirectory, "current.json");
        using JsonDocument before = JsonDocument.Parse(File.ReadAllText(pointerPath));
        long originalCurrent = before.RootElement.GetProperty("currentGeneration").GetInt64();
        long originalPrevious = before.RootElement.GetProperty("previousGeneration").GetInt64();
        Assert.True(originalCurrent > originalPrevious);

        await using var changed = new LiveStackCoordinator(configuration with { AlignmentReferencePath = replacement });
        await changed.StartAsync();
        using JsonDocument after = JsonDocument.Parse(File.ReadAllText(pointerPath));
        Assert.True(after.RootElement.GetProperty("currentGeneration").GetInt64() > originalCurrent);
        Assert.Equal(originalCurrent, after.RootElement.GetProperty("previousGeneration").GetInt64());
        Assert.True(File.Exists(Path.Combine(groupDirectory, $"generation-{originalCurrent:D12}.seiza-stack")));
        Assert.True(File.Exists(Path.Combine(groupDirectory, $"generation-{originalCurrent:D12}.json")));
        Assert.Equal(1, changed.CurrentSnapshot.AcceptedFrames);
    }

    [StackReferenceImageFact]
    public async Task AnIncompatibleSharedCanvasDoesNotCommitASeedOrKeepTheNativeSession()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string shared = Path.Combine(_directory, "rgb-shared.fits");
        string channel = Path.Combine(_directory, "mono-channel.fits");
        StackingOptionsNativeTests.WriteStarField(shared, 0, bayer: true);
        StackingOptionsNativeTests.WriteStarField(channel, 0, bayer: false);
        var configuration = Configuration(captures) with { InitialReferencePath = channel, AlignmentReferencePath = shared };
        await using (var failed = new LiveStackCoordinator(configuration))
        {
            await Assert.ThrowsAsync<SeizaCoreException>(() => failed.StartAsync());
            Assert.Equal(0, failed.CurrentSnapshot.AcceptedFrames);
            Assert.Empty(failed.CurrentSnapshot.Frames);
            Assert.Empty(failed.CurrentSnapshot.CalibrationHistory);
        }
        await using var retry = new LiveStackCoordinator(configuration with { AlignmentReferencePath = null });
        await retry.StartAsync();
        await retry.PauseAndSaveAsync();
        Assert.Equal(1, retry.CurrentSnapshot.AcceptedFrames);
        Assert.Equal(channel, Assert.Single(retry.CurrentSnapshot.Frames).Path);
    }

    [StackReferenceImageCandidatesFact]
    public async Task AutomaticSharedReferenceFallbackCommitsOnlyTheSelectedStableChannelLight()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string shared = Path.Combine(_directory, "shared.fits");
        string rotated = Path.Combine(captures, "best_Ha.fits");
        string aligned = Path.Combine(captures, "next_Ha.fits");
        StackingOptionsNativeTests.WriteStarField(shared, 0, bayer: false);
        StackingOptionsNativeTests.WriteStarField(aligned, 0, bayer: false);
        WriteQuarterTurn(aligned, rotated);
        Task<StackReferenceSelection> Rank(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            int best = paths.ToList().IndexOf(rotated);
            int next = paths.ToList().IndexOf(aligned);
            Assert.True(best >= 0 && next >= 0);
            var scores = paths.Select(_ => (StackReferenceScore?)new StackReferenceScore(40, 12, 1000, 2, 8)).ToArray();
            return Task.FromResult(new StackReferenceSelection(1, best, rotated, scores) { Ranking = [best, next] });
        }
        await using var coordinator = new LiveStackCoordinator(Configuration(captures) with
        {
            AlignmentReferencePath = shared,
            ChooseReferenceAutomatically = true,
        }, referenceSelector: Rank);
        await coordinator.StartAsync();
        await coordinator.PauseAndSaveAsync();
        Assert.Equal(1, coordinator.CurrentSnapshot.AcceptedFrames);
        Assert.Equal(aligned, Assert.Single(coordinator.CurrentSnapshot.Frames).Path);
        Assert.Contains(coordinator.CurrentSnapshot.Attention,
            item => LiveStackPath.Equals(item.Path, rotated) && item.Message.Contains("Skipped", StringComparison.Ordinal));
    }

    [StackReferenceImageCandidatesFact]
    public async Task AFailedFirstPushDisposesTheEmptySessionWithoutCommittingTelemetry()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string shared = Path.Combine(_directory, "shared.fits");
        string rotated = Path.Combine(captures, "best_Ha.fits");
        string aligned = Path.Combine(captures, "next_Ha.fits");
        StackingOptionsNativeTests.WriteStarField(shared, 0, bayer: false);
        StackingOptionsNativeTests.WriteStarField(aligned, 0, bayer: false);
        WriteQuarterTurn(aligned, rotated);
        Task<StackReferenceSelection> Rank(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            int best = paths.ToList().IndexOf(rotated);
            int next = paths.ToList().IndexOf(aligned);
            var scores = paths.Select(_ => (StackReferenceScore?)new StackReferenceScore(40, 12, 1000, 2, 8)).ToArray();
            return Task.FromResult(new StackReferenceSelection(1, best, rotated, scores) { Ranking = [best, next] });
        }
        var configuration = Configuration(captures) with
        {
            AlignmentReferencePath = shared,
            ChooseReferenceAutomatically = true,
        };
        bool removedBeforePush = false;
        await using (var failed = new LiveStackCoordinator(configuration, referenceSelector: Rank))
        {
            // Attention is delivered synchronously after the native constructor
            // chose a channel light, but before the explicit first push. This
            // deterministically exercises that second read failing, not an open failure.
            failed.Changed += (_, _) =>
            {
                if (!removedBeforePush && failed.CurrentSnapshot.Attention.Any(item =>
                    LiveStackPath.Equals(item.Path, rotated) && item.Message.Contains("Skipped", StringComparison.Ordinal)))
                {
                    removedBeforePush = true;
                    File.Delete(aligned);
                }
            };
            await failed.StartAsync();
            await failed.PauseAndSaveAsync();
            Assert.True(removedBeforePush);
            Assert.Equal(0, failed.CurrentSnapshot.AcceptedFrames);
            Assert.Empty(failed.CurrentSnapshot.Frames);
            Assert.Empty(failed.CurrentSnapshot.CalibrationHistory);
            Assert.Contains(failed.CurrentSnapshot.Attention, item =>
                item.Message.Contains("channel reference could not start", StringComparison.Ordinal));
        }
        StackingOptionsNativeTests.WriteStarField(aligned, 0, bayer: false);
        await using var retry = new LiveStackCoordinator(configuration with
        {
            ChooseReferenceAutomatically = false,
            InitialReferencePath = aligned,
        });
        await retry.StartAsync();
        await retry.PauseAndSaveAsync();
        Assert.Equal(1, retry.CurrentSnapshot.AcceptedFrames);
        Assert.Equal(aligned, Assert.Single(retry.CurrentSnapshot.Frames).Path);
    }

    [Fact]
    public void ExistingCandidatesUseWatcherStabilityAndEveryExclusion()
    {
        string sessions = Path.Combine(_directory, "sessions");
        Directory.CreateDirectory(sessions);
        string light = CreateFile("light.fits");
        string output = CreateFile("output.fits");
        string bias = CreateFile("bias.fits");
        string dark = CreateFile("dark.fits");
        string flat = CreateFile("flat.fits");
        _ = CreateFile(".seiza-stack-in-progress.fits");
        _ = CreateFile(Path.Combine("sessions", "session-image.fits"));
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions
        {
            FolderPath = _directory,
            IncludeSubdirectories = true,
            ExcludedPaths = [output, bias, dark, flat],
            ExcludedDirectories = [sessions],
        });
        DateTimeOffset start = DateTimeOffset.UtcNow;

        Assert.Empty(observations.ObserveExisting(start));
        Assert.True(observations.HasPendingCandidates);
        Assert.Empty(observations.ObserveExisting(start.AddSeconds(1)));
        StackFileReadyCandidate ready = Assert.Single(observations.ObserveExisting(start.AddSeconds(2)));
        Assert.Equal(light, ready.Path);
        Assert.True(LiveStackReferenceCandidates.IsUnchanged(ready));

        File.AppendAllText(light, "writer is still active");
        Assert.False(LiveStackReferenceCandidates.IsUnchanged(ready));
    }

    [Fact]
    public void AChangingCaptureMustCompleteAnotherFullStableWindow()
    {
        string light = CreateFile("changing.fits");
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        DateTimeOffset start = DateTimeOffset.UtcNow;
        Assert.Empty(observations.ObserveExisting(start));
        File.AppendAllText(light, "new pixels");
        Assert.Empty(observations.ObserveExisting(start.AddSeconds(2)));
        Assert.Empty(observations.ObserveExisting(start.AddSeconds(3)));
        Assert.Single(observations.ObserveExisting(start.AddSeconds(4)));
    }

    [Fact]
    public async Task AnEarlyTimerWakeupCannotShortenTheCandidateStabilityWindow()
    {
        string light = CreateFile("early-timer.fits");
        var premature = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        var earlyClock = new EarlyTimerTimeProvider();
        Assert.Empty(premature.ObserveExisting(earlyClock.GetUtcNow()));
        await Task.Delay(TimeSpan.FromSeconds(2), earlyClock, CancellationToken.None);
        // Reproduces the old single-delay scan: its unchanged light is dropped
        // because a timer wakeup at 1.999 seconds is not two stable seconds.
        Assert.Equal(TimeSpan.FromMilliseconds(1999), earlyClock.Elapsed);
        Assert.Empty(premature.ObserveExisting(earlyClock.GetUtcNow()));

        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        var clock = new EarlyTimerTimeProvider();
        DateTimeOffset start = clock.GetUtcNow();

        StackFileReadyCandidate ready = Assert.Single(await observations.ObserveStableExistingAsync(
            clock, CancellationToken.None));

        Assert.Equal(light, ready.Path);
        Assert.Equal(2, clock.TimerCalls);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetUtcNow() - start);
        Assert.True(LiveStackReferenceCandidates.IsUnchanged(ready));
    }

    [Fact]
    public async Task ABackwardClockAdjustmentBoundsStartupAndLeavesTheCaptureUnseeded()
    {
        string light = CreateFile("backward-clock.fits");
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        var clock = new EarlyTimerTimeProvider(moveClockBackward: true);
        DateTimeOffset start = clock.GetUtcNow();

        Assert.Empty(await observations.ObserveStableExistingAsync(clock, CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(3), clock.Elapsed);
        Assert.Equal(2, clock.TimerCalls);
        Assert.True(observations.HasPendingCandidates);
        StackFileReadyCandidate ready = Assert.Single(observations.ObserveExisting(start.AddSeconds(2)));
        Assert.Equal(light, ready.Path);
    }

    [Fact]
    public async Task ASlowInitialScanStillWaitsTheFullStableDurationAfterEnumeration()
    {
        string light = CreateFile("slow-scan.fits");
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        var clock = new EarlyTimerTimeProvider(initialScanDuration: TimeSpan.FromSeconds(3));

        StackFileReadyCandidate ready = Assert.Single(await observations.ObserveStableExistingAsync(
            clock, CancellationToken.None));

        Assert.Equal(light, ready.Path);
        Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
        Assert.Equal(2, clock.TimerCalls);
    }

    [Fact]
    public async Task StartupObservationHonorsCancellationBeforeScanning()
    {
        _ = CreateFile("cancelled.fits");
        var observations = new LiveStackReferenceCandidates(new StackFolderMonitorOptions { FolderPath = _directory });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observations.ObserveStableExistingAsync(
            TimeProvider.System, cancellation.Token));

        Assert.False(observations.HasPendingCandidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompatibleRestoreDoesNotRankMissingOrUnscorableSources(bool removeOriginal)
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string reference = Path.Combine(_directory, "reference.fits");
        StackingOptionsNativeTests.WriteStarField(reference, 0, bayer: false);
        var configuration = Configuration(captures) with { InitialReferencePath = reference };
        await using (var original = new LiveStackCoordinator(configuration))
        {
            await original.StartAsync();
            await original.PauseAndSaveAsync();
            Assert.Equal(1, original.CurrentSnapshot.AcceptedFrames);
        }
        if (removeOriginal)
        {
            File.Delete(reference);
        }
        // A valid raw light too small for the scorer's 64px sky tiles cannot
        // be ranked. Its presence must not stop a compatible restore.
        StackingOptionsNativeTests.WriteStarField(Path.Combine(captures, "unscorable.fits"),
            0, bayer: false, side: 64);
        int scoreCalls = 0;
        Task<StackReferenceSelection> NeverScore(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            scoreCalls++;
            throw new InvalidOperationException("A restored stack must never rank new references.");
        }
        await using var restored = new LiveStackCoordinator(
            configuration with { InitialReferencePath = null, ChooseReferenceAutomatically = true },
            referenceSelector: NeverScore);

        await restored.StartAsync();
        await restored.PauseAndSaveAsync();

        Assert.Equal(0, scoreCalls);
        Assert.Equal(1, restored.CurrentSnapshot.AcceptedFrames);
        Assert.Equal(LiveStackRunState.Paused, restored.CurrentSnapshot.State);
    }

    [Fact]
    public async Task ASelectionChangedDuringScoringIsNotSeededAndTheWatcherCanRetryIt()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        string reference = Path.Combine(captures, "reference.fits");
        StackingOptionsNativeTests.WriteStarField(reference, 0, bayer: false);
        int scoreCalls = 0;
        Task<StackReferenceSelection> ChangeWhileScoring(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            scoreCalls++;
            Assert.Equal(reference, Assert.Single(paths));
            File.SetLastWriteTimeUtc(reference, File.GetLastWriteTimeUtc(reference).AddSeconds(10));
            return Task.FromResult(new StackReferenceSelection(1, 0, reference,
                [new StackReferenceScore(40, 12, 1000, 2, 8)]));
        }
        await using var coordinator = new LiveStackCoordinator(
            Configuration(captures) with { ResumeExisting = false, ChooseReferenceAutomatically = true },
            referenceSelector: ChangeWhileScoring);
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Changed += (_, _) =>
        {
            if (coordinator.CurrentSnapshot.AcceptedFrames > 0)
            {
                accepted.TrySetResult(true);
            }
        };

        await coordinator.StartAsync();
        Assert.True(scoreCalls == 1,
            $"Expected one scorer call, got {scoreCalls}. Startup attention: " +
            string.Join("; ", coordinator.CurrentSnapshot.Attention.Select(item => item.Message)));
        Assert.Equal(0, coordinator.CurrentSnapshot.AcceptedFrames);
        Assert.Contains(coordinator.CurrentSnapshot.Attention, item => item.Message.Contains("changed during scoring", StringComparison.Ordinal));
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await coordinator.PauseAndSaveAsync();

        Assert.Equal(1, coordinator.CurrentSnapshot.AcceptedFrames);
    }

    [Fact]
    public async Task UnscorableCandidatesFallBackToWatchingInsteadOfFailingStartup()
    {
        string captures = Path.Combine(_directory, "captures");
        Directory.CreateDirectory(captures);
        StackingOptionsNativeTests.WriteStarField(Path.Combine(captures, "reference.fits"), 0, bayer: false);
        Task<StackReferenceSelection> Unscorable(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
            throw new SeizaCoreException("no frame could be scored as a reference");
        await using var coordinator = new LiveStackCoordinator(
            Configuration(captures) with { ResumeExisting = false, ChooseReferenceAutomatically = true },
            referenceSelector: Unscorable);

        await coordinator.StartAsync();
        Assert.Equal(LiveStackRunState.WaitingForLight, coordinator.CurrentSnapshot.State);
        Assert.Contains(coordinator.CurrentSnapshot.Attention, item => item.Message.Contains("Could not rank", StringComparison.Ordinal));
        await coordinator.PauseAndSaveAsync();
    }

    private LiveStackRunConfiguration Configuration(string captures) => new()
    {
        WatchFolder = captures,
        SessionRootDirectory = Path.Combine(_directory, "sessions"),
        PreviewMaxDimension = 128,
    };

    private string CreateFile(string relativePath)
    {
        string path = Path.Combine(_directory, relativePath);
        File.WriteAllText(path, "candidate");
        return path;
    }

    private static void WriteQuarterTurn(string sourcePath, string destination)
    {
        const int side = 256;
        byte[] pixels = File.ReadAllBytes(sourcePath);
        byte[] turned = (byte[])pixels.Clone();
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                pixels.AsSpan(2880 + (y * side + x) * sizeof(float), sizeof(float))
                    .CopyTo(turned.AsSpan(2880 + (x * side + side - 1 - y) * sizeof(float), sizeof(float)));
            }
        }
        File.WriteAllBytes(destination, turned);
    }

    private sealed class EarlyTimerTimeProvider(
        bool moveClockBackward = false,
        TimeSpan initialScanDuration = default) : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        private TimeSpan _elapsed;
        private int _utcReads;
        public int TimerCalls { get; private set; }
        public TimeSpan Elapsed
        {
            get
            {
                lock (_sync)
                {
                    return _elapsed;
                }
            }
        }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                if (++_utcReads == 2)
                {
                    _now += initialScanDuration;
                    _elapsed += initialScanDuration;
                }
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimeSpan elapsed;
            bool adjustClock;
            lock (_sync)
            {
                TimerCalls++;
                elapsed = dueTime - (TimerCalls == 1 ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero);
                adjustClock = moveClockBackward && TimerCalls == 1;
            }
            var timer = new OneShotTimer(() =>
            {
                lock (_sync)
                {
                    _now += elapsed;
                    _elapsed += elapsed;
                    if (adjustClock)
                    {
                        _now -= TimeSpan.FromHours(1);
                    }
                }
                callback(state);
            });
            ThreadPool.QueueUserWorkItem(_ => timer.Fire());
            return timer;
        }

        private sealed class OneShotTimer(Action callback) : ITimer
        {
            private int _disposed;
            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    callback();
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
