using System.Runtime.InteropServices;
using Seiza.App.Interop;

namespace Seiza.App.Services;

/// <summary>Probe exports, not version strings: unreleased APIs may share a version.</summary>
internal sealed record StackingCoreCapabilities(
    bool SupportsReferenceImage,
    bool SupportsReferenceImageCandidates)
{
    private static readonly Lazy<StackingCoreCapabilities> Detected = new(Detect);

    public static StackingCoreCapabilities Current => Detected.Value;

    public void RequireReferenceImage(bool needsFallback = false)
    {
        if ((!SupportsReferenceImage && !SupportsReferenceImageCandidates) ||
            (needsFallback && !SupportsReferenceImageCandidates))
        {
            throw new SeizaCoreException(
                needsFallback
                    ? "The installed Seiza core does not support automatic alignment-reference fallback. " +
                      "This feature requires the upcoming published stacking crates."
                    : "The installed Seiza core does not support a shared alignment reference image. " +
                      "This feature requires the upcoming published stacking crates.");
        }
    }

    private static StackingCoreCapabilities Detect()
    {
        // Use the same assembly-aware lookup as LibraryImport, rather than
        // inspecting a separately named DLL or inferring from a crate pin.
        if (!NativeLibrary.TryLoad("seiza_cabi", typeof(NativeMethods).Assembly, null, out nint library))
        {
            return new(false, false);
        }
        try
        {
            return new(
                NativeLibrary.TryGetExport(library, "seiza_live_stacker_open_fits_with_reference_image", out _),
                NativeLibrary.TryGetExport(library, "seiza_live_stacker_open_fits_with_reference_image_candidates", out _));
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
