using System.Runtime.InteropServices;

namespace Seiza.App.Interop;

internal static partial class NativeMethods
{
    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_prepare_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint PrepareParallaxJson(string request, SafeCancelSignalHandle cancel,
        delegate* unmanaged[Cdecl]<nint, nint, void> events, nint context, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_plan_tour_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint PlanParallaxTourJson(string request,
        delegate* unmanaged[Cdecl]<nint, nint, void> events, nint context, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_reconfigure_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint ReconfigureParallaxJson(SafeParallaxVideoHandle video, string settings, SafeCancelSignalHandle cancel,
        delegate* unmanaged[Cdecl]<nint, nint, void> events, nint context, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_with_dust_map")]
    internal static unsafe partial nint WithParallaxDustMap(SafeParallaxVideoHandle video, float* opacity, uint width, uint height,
        SafeCancelSignalHandle cancel, delegate* unmanaged[Cdecl]<nint, nint, void> events, nint context, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_with_dust_map_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial nint WithParallaxDustMapJson(SafeParallaxVideoHandle video, string request, SafeCancelSignalHandle cancel,
        delegate* unmanaged[Cdecl]<nint, nint, void> events, nint context, out nint error);

    // 1 written, 0 when the video has no such dust, -1 with an error.
    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_dust_map")]
    internal static unsafe partial int ReadParallaxDustMap(SafeParallaxVideoHandle video, uint which, uint width, uint height,
        float* opacity, nuint length, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_write_dust_map_json", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int WriteParallaxDustMapJson(SafeParallaxVideoHandle video, string request, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_summary_json")]
    internal static partial nint GetParallaxSummaryJson(SafeParallaxVideoHandle video, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_render_frame")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static unsafe partial bool RenderParallaxFrame(SafeParallaxVideoHandle video, uint index, uint format,
        byte* buffer, nuint bufferLength, nuint stride, out nint error);

    [LibraryImport(LibraryName, EntryPoint = "seiza_parallax_free")]
    internal static partial void FreeParallax(nint video);
}
