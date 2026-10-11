using Seiza.App.Interop;

namespace Seiza.App.Services;

internal sealed class ImageStackSnapshot : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SafeStackSnapshotHandle? _handle;
    private bool _disposed;
    private readonly string[] _protectedPaths;

    internal ImageStackSnapshot(SafeStackSnapshotHandle handle, string[] protectedPaths)
    {
        _handle = handle;
        _protectedPaths = (string[])protectedPaths.Clone();
        nint nativeHandle = handle.DangerousGetHandle();
        AcceptedFrames = checked((int)NativeMethods.GetStackSnapshotAcceptedFrames(nativeHandle));
        RejectedFrames = checked((int)NativeMethods.GetStackSnapshotRejectedFrames(nativeHandle));
    }

    public int AcceptedFrames { get; }

    public int RejectedFrames { get; }

    public async Task WriteFitsAsync(
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SafeStackSnapshotHandle handle = RequireHandle();
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                // The native writer sees a staging filename. Guard the final
                // destination too, before atomic publication can replace it.
                StackOutputProtection.ValidateDestination(outputPath, _protectedPaths);
                AtomicOutputFile.Write(
                    outputPath,
                    stagingPath =>
                    {
                        nint error = 0;
                        if (!NativeMethods.WriteStackSnapshotFits(
                                handle.DangerousGetHandle(),
                                stagingPath,
                                out error))
                        {
                            throw NativeString.TakeError(
                                error,
                                "The Seiza core could not write the stacked image.");
                        }
                    },
                    cancellationToken);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            DisposeHandle();
        }
        finally
        {
            _gate.Release();
        }
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposeHandle();
        }
        finally
        {
            _gate.Release();
        }
        GC.SuppressFinalize(this);
    }

    private SafeStackSnapshotHandle RequireHandle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _handle
            ?? throw new ObjectDisposedException(nameof(ImageStackSnapshot));
    }

    private void DisposeHandle()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _handle?.Dispose();
        _handle = null;
    }
}
