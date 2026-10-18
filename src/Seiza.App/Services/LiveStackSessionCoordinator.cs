using System.Text.Json;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal sealed class LiveStackFilterGroup(LiveStackFilterIdentity filter, LiveStackCoordinator coordinator)
{
    public LiveStackFilterIdentity Filter { get; } = filter;
    public LiveStackCoordinator Coordinator { get; } = coordinator;
    internal Task RunTask { get; set; } = Task.CompletedTask;
    public string? Error { get; internal set; }
    public bool Completed { get; internal set; }
    public override string ToString()
    {
        LiveStackRunSnapshot snapshot = Coordinator.CurrentSnapshot;
        return $"{Filter.DisplayName} — {snapshot.AcceptedFrames:N0} accepted, {snapshot.RejectedFrames:N0} rejected — {Error ?? (Completed ? "Completed" : snapshot.State.ToString())}";
    }
}

/// <summary>
/// One capture session, with an independent native accumulator and durable
/// checkpoint per filter. Discovery keeps running when a filter is idle or
/// fails; native ingestion/startup is bounded to two simultaneous workers.
/// </summary>
internal sealed class LiveStackSessionCoordinator : IAsyncDisposable
{
    private readonly LiveStackRunConfiguration _configuration;
    private readonly Func<CalibrationFrameProbe, CancellationToken, Task<CalibrationPreparationResult>>? _prepareCalibration;
    private readonly Dictionary<string, LiveStackFilterGroup> _groups = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _processingSlots = new(2, 2);
    private readonly StackFolderMonitor _monitor;
    private readonly string _registryPath;
    private CancellationTokenSource? _cancellation;
    private Task _discovery = Task.CompletedTask;
    private FileStream? _lease;
    private bool _loaded, _disposed;

    public LiveStackSessionCoordinator(LiveStackRunConfiguration configuration,
        Func<CalibrationFrameProbe, CancellationToken, Task<CalibrationPreparationResult>>? prepareCalibration = null)
    {
        configuration.Validate();
        _configuration = configuration with { Options = configuration.Options.Copy(), Calibration = configuration.Calibration.Copy() };
        _prepareCalibration = prepareCalibration;
        _registryPath = Path.Combine(configuration.SessionRootDirectory, "filters.json");
        _monitor = new StackFolderMonitor(new StackFolderMonitorOptions
        {
            FolderPath = configuration.WatchFolder,
            IncludeSubdirectories = configuration.IncludeSubdirectories,
            ExcludedPaths = configuration.MonitorExcludedPaths(),
            ExcludedDirectories = [configuration.SessionRootDirectory],
        });
    }

    public event EventHandler? Changed;
    public bool IsPaused { get; private set; } = true;
    public string Status { get; private set; } = "Waiting for filter groups.";
    public IReadOnlyList<LiveStackFilterGroup> Groups
    {
        get { lock (_sync) return _groups.Values.OrderBy(group => group.Filter.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsPaused) return;
            Directory.CreateDirectory(_configuration.SessionRootDirectory);
            // Own the filter registry as well as each child's native context.
            _lease ??= new FileStream(Path.Combine(_configuration.SessionRootDirectory, "filters.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!_loaded)
            {
                if (_configuration.ResumeExisting && File.Exists(_registryPath))
                {
                    FilterRegistry registry = JsonSerializer.Deserialize<FilterRegistry>(await File.ReadAllTextAsync(_registryPath, cancellationToken).ConfigureAwait(false))
                        ?? throw new InvalidDataException("The live session filter registry is empty.");
                    if (registry.SchemaVersion != 1 || registry.Filters is null)
                        throw new InvalidDataException("Unsupported live session filter registry.");
                    foreach (FilterEntry filter in registry.Filters)
                        AddGroup(LiveStackFilterIdentity.FromStoredName(filter.Name)).Completed = filter.Completed;
                }
                _loaded = true;
            }
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            foreach (LiveStackFilterGroup group in Groups) StartGroup(group, _cancellation.Token);
            IsPaused = false;
            Status = "Watching for all filters; new filter groups appear automatically.";
            _discovery = DiscoverAsync(_cancellation.Token);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _lifecycle.Release(); }
    }

    private LiveStackFilterGroup AddGroup(LiveStackFilterIdentity filter)
    {
        lock (_sync)
        {
            if (_groups.TryGetValue(filter.Key, out LiveStackFilterGroup? existing)) return existing;
            var coordinator = new LiveStackCoordinator(_configuration with
            {
                GroupId = "filter-" + filter.Key,
                GroupTitle = filter.DisplayName,
                TargetFilter = filter,
                InitialReferencePath = null,
                // Automatic calibration runs independently when each fresh
                // group finds its reference; restored groups keep their masters.
                ApplyCalibrationOnResume = _prepareCalibration is null && _configuration.ApplyCalibrationOnResume,
            }, processingSlots: _processingSlots, prepareCalibration: _prepareCalibration);
            coordinator.Changed += GroupChanged;
            var group = new LiveStackFilterGroup(filter, coordinator);
            _groups.Add(filter.Key, group);
            return group;
        }
    }

    private void StartGroup(LiveStackFilterGroup group, CancellationToken cancellationToken)
    {
        if (!group.RunTask.IsCompleted || group.Completed || group.Coordinator.CurrentSnapshot.State == LiveStackRunState.Completed) return;
        group.Error = null;
        group.RunTask = Task.Run(async () =>
        {
            try
            {
                await _processingSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { await group.Coordinator.StartAsync(cancellationToken).ConfigureAwait(false); }
                finally { _processingSlots.Release(); }
                await group.Coordinator.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error) { group.Error = error.Message; }
            finally { Changed?.Invoke(this, EventArgs.Empty); }
        }, CancellationToken.None);
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StackFileReadyCandidate candidate in _monitor.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    CalibrationFrameProbe probe = await CalibrationService.ProbeAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
                    if (!_monitor.IsCandidateCurrent(candidate)) continue;
                    if (CalibrationLightEligibility.IsEligible(probe))
                    {
                        LiveStackFilterIdentity filter = LiveStackFilterIdentity.FromProbe(probe);
                        bool isNew;
                        lock (_sync) isNew = !_groups.ContainsKey(filter.Key);
                        LiveStackFilterGroup group = AddGroup(filter);
                        if (isNew || (!group.Completed && group.RunTask.IsCompleted && group.Coordinator.CurrentSnapshot.State == LiveStackRunState.Created))
                        {
                            // Persist discovery before starting a native stack.
                            await SaveRegistryAsync(cancellationToken).ConfigureAwait(false);
                            StartGroup(group, cancellationToken);
                            Status = $"Discovered {filter.DisplayName}; watching {Groups.Count} filter group(s).";
                            Changed?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    _monitor.ReportProcessingResult(candidate, new StackFileProcessingResult(StackFileProcessingDisposition.Accepted));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    Status = $"Could not inspect {Path.GetFileName(candidate.Path)}: {error.Message}";
                    _monitor.ReportProcessingResult(candidate, new StackFileProcessingResult(StackFileProcessingDisposition.RetryableFailure, error.Message));
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { Status = error.Message; Changed?.Invoke(this, EventArgs.Empty); }
    }

    private async Task SaveRegistryAsync(CancellationToken cancellationToken)
    {
        string temporary = _registryPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new FilterRegistry(1,
                Groups.Select(group => new FilterEntry(
                    group.Filter.Source == LiveStackFilterSource.Unspecified ? null : group.Filter.DisplayName,
                    group.Completed || group.Coordinator.CurrentSnapshot.State == LiveStackRunState.Completed)).ToArray())), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _registryPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private void GroupChanged(object? sender, LiveStackRunChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public async Task PauseAndSaveAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _cancellation?.Cancel();
            await _discovery.ConfigureAwait(false);
            await Task.WhenAll(Groups.Select(group => group.RunTask)).ConfigureAwait(false);
            // Try every checkpoint even if one fails. Keep all owners alive so
            // a failed save can be retried from the session window.
            await Task.WhenAll(Groups.Where(group => !group.Completed).Select(group => group.Coordinator.PauseAndSaveAsync())).ConfigureAwait(false);
            await SaveRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            IsPaused = true;
            Status = "All filter stacks paused and saved.";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _lifecycle.Release(); }
    }

    public void ValidateOutputPath(string path)
    {
        string relative = Path.GetRelativePath(_configuration.WatchFolder, Path.GetFullPath(path));
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Save session outputs outside the capture folder so no filter can ingest an exported stack.");
        if (Groups.SelectMany(group => group.Coordinator.CurrentSnapshot.Frames)
            .Any(frame => StackSourceIdentity.RefersToSameFile(frame.Path, path)))
            throw new InvalidOperationException("The output cannot replace a captured light from any filter.");
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _cancellation?.Cancel();
            await _discovery.ConfigureAwait(false);
            await Task.WhenAll(Groups.Select(group => group.RunTask)).ConfigureAwait(false);
            foreach (LiveStackFilterGroup group in Groups)
            {
                group.Coordinator.Changed -= GroupChanged;
                await group.Coordinator.DisposeAsync().ConfigureAwait(false);
            }
            _cancellation?.Dispose();
            _lease?.Dispose();
            _processingSlots.Dispose();
            _disposed = true;
        }
        finally { _lifecycle.Release(); }
    }

    private sealed record FilterRegistry(int SchemaVersion, FilterEntry[] Filters);
    private sealed record FilterEntry(string? Name, bool Completed);
}
