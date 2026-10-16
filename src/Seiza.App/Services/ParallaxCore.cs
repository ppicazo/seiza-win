using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class ParallaxCore
{
    public static bool SupportsFraming => Version.TryParse(Marshal.PtrToStringUTF8(NativeMethods.GetCoreVersion()), out var version) && version >= new Version(0, 32, 0);
    private static readonly Lazy<bool> PullBackSupport = new(ProbePullBackSupport);
    public static bool SupportsPullBackToWholeImage => PullBackSupport.Value;

    private static unsafe bool ProbePullBackSupport()
    {
        if (!SupportsFraming) return false;
        // #283 adds a JSON field without changing the development core version.
        // An intentionally wrong field type fails during deserialization, before
        // file/catalog work. Only the recognized boolean-type error proves support;
        // unknown-field errors (published 0.32) and all other failures fail closed.
        const string marker = "seiza-pullback-capability-probe";
        nint result = NativeMethods.PlanParallaxTourJson(
            "{\"tour\":[{\"pullBackToWholeImage\":\"" + marker + "\"}]}", null, 0, out nint error);
        if (result != 0) _ = NativeString.TakeOwned(result, string.Empty);
        string? message = error == 0 ? null : NativeString.TakeError(error, "Unsupported camera setting.").Message;
        return message is not null && message.Contains(marker, StringComparison.Ordinal) &&
            message.Contains("expected a boolean", StringComparison.Ordinal);
    }

    private static ParallaxVideoSettings CompatibleVideo(ParallaxVideoSettings settings)
    {
        if (settings.Tour.Any(stop => stop.PullBackToWholeImage) && !SupportsPullBackToWholeImage)
            throw new NotSupportedException("Whole-image pull-back requires a native core containing Seiza #283.");
        if (SupportsFraming) return settings;
        if (settings.Framing is not (null or "fill"))
            throw new NotSupportedException("Show whole image framing requires Seiza 0.32.0 or newer.");
        return settings with { Framing = null };
    }
    private static ParallaxRequest CompatibleRequest(ParallaxRequest request)
    {
        if (request.Scene.Crop is not null && !SupportsFraming)
            throw new NotSupportedException("Video cropping requires Seiza 0.32.0 or newer.");
        return request with { Video = CompatibleVideo(request.Video) };
    }
    public static Task<ParallaxTourPlan> PlanAsync(ParallaxRequest request, IProgress<ParallaxEvent>? events = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Plan(request, events, cancellationToken), cancellationToken);

    public static Task<ParallaxTourPlan> PlanOnSceneAsync(ParallaxVideo scene, string size, ParallaxTourOptions options, CancellationToken cancellationToken = default) =>
        PlanOnSceneAsync(scene, size, options, "fill", cancellationToken);
    public static Task<ParallaxTourPlan> PlanOnSceneAsync(ParallaxVideo scene, string size, ParallaxTourOptions options, string framing, CancellationToken cancellationToken = default) =>
        Task.Run(() => PlanOnScene(scene, size, options, framing, cancellationToken), cancellationToken);

    public static Task<ParallaxVideo> PrepareAsync(ParallaxRequest request, IProgress<ParallaxEvent>? events = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Prepare(request, events, cancellationToken), cancellationToken);

    public static Task<ParallaxVideo> ReconfigureAsync(ParallaxVideo video, ParallaxVideoSettings settings, IProgress<ParallaxEvent>? events = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Reconfigure(video, settings, events, cancellationToken), cancellationToken);

    public static Task<ParallaxVideo> WithDustMapAsync(ParallaxVideo video, string? path, IProgress<ParallaxEvent>? events = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => WithDustMap(video, path, events, cancellationToken), cancellationToken);

    public static unsafe ParallaxTourPlan Plan(ParallaxRequest request, IProgress<ParallaxEvent>? events = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = new EventScope(events, cancellationToken);
        // The current planning ABI has no cancel parameter. Retain all owners
        // until it returns; a cancelled caller must not detach native work.
        nint result = NativeMethods.PlanParallaxTourJson(CompatibleRequest(request).ToJson(), &ReceiveEvent, scope.Context, out nint error);
        string? json = result == 0 ? null : NativeString.TakeOwned(result, string.Empty);
        Exception? failure = error == 0 ? null : NativeString.TakeError(error, "Could not plan a parallax tour.");
        scope.Check();
        if (json is null) throw failure ?? new SeizaCoreException("Could not plan a parallax tour.");
        return ParallaxTourPlan.FromJson(json);
    }

    // A tour of the catalogued objects planned on a prepared scene, for a
    // `size` frame ("WIDTHxHEIGHT"): each stop is framed at the depth the scene
    // shows its target at, and no image is solved. The scene keeps the distance
    // it was prepared with, so the plan's focus matters only to a new preparation.
    public static ParallaxTourPlan PlanOnScene(ParallaxVideo scene, string size, ParallaxTourOptions options, CancellationToken cancellationToken = default) =>
        PlanOnScene(scene, size, options, "fill", cancellationToken);
    public static ParallaxTourPlan PlanOnScene(ParallaxVideo scene, string size, ParallaxTourOptions options, string framing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        string? compatibleFraming = CompatibleVideo(new() { Framing = framing }).Framing;
        nint result = NativeMethods.PlanParallaxSceneTourJson(scene.Handle, ParallaxJson.SerializeSceneTourSettings(size, options, compatibleFraming), out nint error);
        string? json = result == 0 ? null : NativeString.TakeOwned(result, string.Empty);
        Exception? failure = error == 0 ? null : NativeString.TakeError(error, "Could not plan a tour on the prepared scene.");
        if (json is null) throw failure ?? new SeizaCoreException("Could not plan a tour on the prepared scene.");
        cancellationToken.ThrowIfCancellationRequested();
        ParallaxTourPlan plan = ParallaxTourPlan.FromJson(json);
        plan.PlannedOnScene = true;
        return plan;
    }

    public static unsafe ParallaxVideo Prepare(ParallaxRequest request, IProgress<ParallaxEvent>? events = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string[] protectedPaths = ParallaxInputPaths.Capture(
            [.. ParallaxInputPaths.FromInputs(request.Inputs), .. ParallaxInputPaths.FromScene(request.Scene),
                .. ParallaxInputPaths.FromVideo(request.Video)]);
        using var scope = new EventScope(events, cancellationToken);
        nint result = NativeMethods.PrepareParallaxJson(CompatibleRequest(request).ToJson(), scope.Signal, &ReceiveEvent, scope.Context, out nint error);
        return FinishVideo(result, error, scope, protectedPaths);
    }

    public static unsafe ParallaxVideo Reconfigure(ParallaxVideo video, ParallaxVideoSettings settings, IProgress<ParallaxEvent>? events = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        string[] protectedPaths = ParallaxInputPaths.Capture(
            [.. video.ProtectedPaths, .. ParallaxInputPaths.FromVideo(settings)]);
        using var scope = new EventScope(events, cancellationToken);
        nint result = NativeMethods.ReconfigureParallaxJson(video.Handle, CompatibleVideo(settings).ToJson(), scope.Signal, &ReceiveEvent, scope.Context, out nint error);
        return FinishVideo(result, error, scope, protectedPaths);
    }

    // A new video of the same scene and camera with the dust map at `path`
    // (white opaque; PNG, JPEG, TIFF, FITS or XISF), or the dust counted from
    // the stars again when `path` is null. Only the dust changes; the native
    // scene's planes and stars are shared, not prepared again.
    public static unsafe ParallaxVideo WithDustMap(ParallaxVideo video, string? path, IProgress<ParallaxEvent>? events = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = new EventScope(events, cancellationToken);
        nint result = NativeMethods.WithParallaxDustMapJson(video.Handle, ParallaxJson.SerializeDustMapPath(path), scope.Signal, &ReceiveEvent, scope.Context, out nint error);
        return FinishVideo(result, error, scope, path is null ? video.ProtectedPaths : [.. video.ProtectedPaths, path]);
    }

    // As above with the map painted in memory: `opacity` is `width` × `height`
    // values row by row, 0 where clear to 1 where opaque.
    public static unsafe ParallaxVideo WithDustMap(ParallaxVideo video, float[] opacity, int width, int height, IProgress<ParallaxEvent>? events = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video); ArgumentNullException.ThrowIfNull(opacity);
        if (width < 1 || height < 1 || opacity.Length != checked(width * height))
            throw new ArgumentException("The dust map must hold one value for each of its pixels.", nameof(opacity));
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = new EventScope(events, cancellationToken);
        nint result, error;
        fixed (float* values = opacity)
            result = NativeMethods.WithParallaxDustMap(video.Handle, values, checked((uint)width), checked((uint)height), scope.Signal, &ReceiveEvent, scope.Context, out error);
        return FinishVideo(result, error, scope, video.ProtectedPaths);
    }

    private static ParallaxVideo FinishVideo(nint result, nint error, EventScope scope, IReadOnlyList<string> protectedPaths)
    {
        Exception? failure = error == 0 ? null : NativeString.TakeError(error, "Could not prepare the parallax video.");
        using var owner = new SafeParallaxVideoHandle(result);
        scope.Check();
        if (owner.IsInvalid) throw failure ?? new SeizaCoreException("Could not prepare the parallax video.");
        // Transfer only after summary validation has succeeded. On any failure,
        // the temporary owner releases the pointer exactly once.
        var video = new ParallaxVideo(owner, protectedPaths);
        owner.SetHandleAsInvalid();
        try { scope.Check(); return video; }
        catch { video.Dispose(); throw; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReceiveEvent(nint json, nint context)
    {
        EventScope? scope = null;
        try
        {
            scope = GCHandle.FromIntPtr(context).Target as EventScope;
            if (scope is null || json == 0) return;
            string value = Marshal.PtrToStringUTF8(json) ?? throw new JsonException("The native event was empty.");
            var update = JsonSerializer.Deserialize(value, ParallaxJsonContext.Default.ParallaxEvent)
                ?? throw new JsonException("The native event was empty.");
            scope.Report(update);
        }
        catch (Exception exception)
        {
            // Never unwind through Rust. Stop native preparation/refitting and
            // rethrow on the managed side after every native owner is released.
            scope?.Fail(exception);
        }
    }

    private sealed class EventScope : IDisposable
    {
        private readonly IProgress<ParallaxEvent>? _events;
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _registration;
        private readonly GCHandle _context;
        private ExceptionDispatchInfo? _failure;
        internal SafeCancelSignalHandle Signal { get; }
        internal nint Context => GCHandle.ToIntPtr(_context);

        internal EventScope(IProgress<ParallaxEvent>? events, CancellationToken token)
        {
            _events = events; _token = token;
            Signal = new SafeCancelSignalHandle(NativeMethods.CreateCancelSignal());
            if (Signal.IsInvalid) { Signal.Dispose(); throw new SeizaCoreException("Could not create the parallax cancellation signal."); }
            _context = GCHandle.Alloc(this);
            _registration = token.UnsafeRegister(static state => ((SafeCancelSignalHandle)state!).Cancel(), Signal);
        }

        internal void Report(ParallaxEvent update) => _events?.Report(update);
        internal void Fail(Exception exception)
        {
            Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(exception), null);
            Signal.Cancel();
        }
        internal void Check() { _failure?.Throw(); _token.ThrowIfCancellationRequested(); }
        public void Dispose() { _registration.Dispose(); _context.Free(); Signal.Dispose(); }
    }
}

// Native scenes and videos are immutable. SafeHandle's P/Invoke leases prevent
// disposal racing an in-flight frame or refit; siblings share their native scene.
internal sealed class ParallaxVideo : IDisposable
{
    internal SafeParallaxVideoHandle Handle { get; }
    internal IReadOnlyList<string> ProtectedPaths { get; }
    public ParallaxSummary Summary { get; }
    public int Width => Summary.Width;
    public int Height => Summary.Height;
    public int FramesPerSecond => Summary.Fps;
    public int FrameCount => Summary.Frames;

    internal ParallaxVideo(SafeParallaxVideoHandle borrowedOwner, IReadOnlyList<string> protectedPaths)
    {
        nint result = NativeMethods.GetParallaxSummaryJson(borrowedOwner, out nint error);
        if (result == 0) throw NativeString.TakeError(error, "The parallax summary was empty.");
        string json = NativeString.TakeOwned(result, string.Empty);
        if (error != 0) NativeMethods.FreeString(error);
        Summary = JsonSerializer.Deserialize(json, ParallaxJsonContext.Default.ParallaxSummary)
            ?? throw new InvalidDataException("The parallax summary was empty.");
        if (Summary.SchemaVersion != 1 || Summary.Frames is < 2 or > 36_000 || Summary.Fps is < 1 or > 60 ||
            Summary.Width is < 16 or > 3840 || Summary.Height is < 16 or > 3840 ||
            Summary.BackgroundFocus is null || Summary.BackgroundFocus.Length != 2 || !Summary.BackgroundFocus.All(double.IsFinite) ||
            !double.IsFinite(Summary.BackgroundDistanceParsecs) || Summary.BackgroundDistanceParsecs <= 0 || Summary.Fit is null)
            throw new InvalidDataException("The native parallax video has an unsupported size, timing or summary.");
        ProtectedPaths = Array.AsReadOnly(protectedPaths.Select(LiveStackPath.NormalizeForComparison)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        Handle = new SafeParallaxVideoHandle(borrowedOwner.DangerousGetHandle());
    }

    public byte[] RenderBgraFrame(int index, CancellationToken cancellationToken = default)
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(checked(Width * Height * 4));
        RenderBgraFrame(index, buffer, checked(Width * 4), cancellationToken);
        return buffer;
    }

    public unsafe void RenderBgraFrame(int index, Span<byte> destination, int stride, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (index < 0 || index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index), "Frame is outside the video.");
        int rowBytes = checked(Width * 4);
        if (stride < rowBytes || destination.Length < checked((Height - 1) * stride + rowBytes))
            throw new ArgumentException("The BGRA destination is too small or has an incompatible row stride.", nameof(destination));
        fixed (byte* buffer = destination)
        {
            if (!NativeMethods.RenderParallaxFrame(Handle, checked((uint)index), 2, buffer, checked((nuint)destination.Length), checked((nuint)stride), out nint error))
                throw NativeString.TakeError(error, "Could not render the parallax frame.");
            if (error != 0) NativeMethods.FreeString(error);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    // The dust as a `width` × `height` map over the whole image, row by row,
    // 0 clear to 1 opaque: the dust in use, or the dust counted from the stars
    // even while a given map takes its place. Null when there is no such dust.
    public unsafe float[]? DustMap(int width, int height, bool counted = false)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "The dust map needs a width and height.");
        float[] opacity = new float[checked(width * height)];
        int result;
        nint error;
        fixed (float* values = opacity)
            result = NativeMethods.ReadParallaxDustMap(Handle, counted ? 1u : 0u, checked((uint)width), checked((uint)height),
                values, checked((nuint)opacity.Length), out error);
        if (result < 0) throw NativeString.TakeError(error, "Could not read the dust map.");
        if (error != 0) NativeMethods.FreeString(error);
        return result == 1 ? opacity : null;
    }

    // Write the dust to `path`, white opaque, in the format its extension
    // names: 16-bit PNG or TIFF, or 32-bit float FITS or XISF, the size of
    // the image. False when there is no such dust to write.
    public bool WriteDustMap(string path, bool counted = false, CancellationToken cancellationToken = default) =>
        WriteDustMap(path, [], counted, cancellationToken);

    internal bool WriteDustMap(string path, IEnumerable<string?> additionalProtectedPaths,
        bool counted = false, CancellationToken cancellationToken = default) =>
        ParallaxDustMapOutput.Write(path, ParallaxInputPaths.Capture([.. ProtectedPaths, .. additionalProtectedPaths]),
            staging => WriteDustMapFile(staging, counted), cancellationToken);

    private bool WriteDustMapFile(string path, bool counted)
    {
        int result = NativeMethods.WriteParallaxDustMapJson(Handle, ParallaxJson.SerializeDustMapWrite(path, counted), out nint error);
        if (result < 0) throw NativeString.TakeError(error, "Could not write the dust map.");
        if (error != 0) NativeMethods.FreeString(error);
        return result switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("The native dust-map writer returned an unsupported result."),
        };
    }

    public void Dispose() => Handle.Dispose();
}
