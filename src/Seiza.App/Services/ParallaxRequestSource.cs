using Seiza.App.Models;

namespace Seiza.App.Services;

// The display source can itself be starless. Use the private unscreened stars
// copy for solving whenever manual layers exist, independently of camera size.
internal static class ParallaxRequestSource
{
    internal static ParallaxRequest Create(ParallaxComposition composition, bool preview,
        string snapshotPath, bool automaticSeparation, string? starless, string? stars,
        ParallaxInputs inputs)
    {
        inputs.Image = !automaticSeparation && stars is not null ? stars : snapshotPath;
        inputs.Starless = automaticSeparation ? null : starless;
        inputs.Stars = automaticSeparation ? null : stars;
        var scene = composition.Scene.DeepClone();
        scene.DistanceFocus = ParallaxGeometry.ToCrop(scene.DistanceFocus, scene.Crop);
        return new() { Inputs = inputs, Scene = scene, Video = composition.VideoSettings(preview) };
    }

    internal static async Task EnsureWcsAsync(ParallaxRequest request,
        Func<string, Task<WcsResult>> blindSolve, CancellationToken cancellationToken)
    {
        // Preserve an existing document/scene solution; never replace it by
        // blind-solving a different layer unnecessarily.
        if (request.Inputs.Wcs is not null) return;
        cancellationToken.ThrowIfCancellationRequested();
        WcsResult wcs = await blindSolve(request.Inputs.Image);
        cancellationToken.ThrowIfCancellationRequested();
        request.Inputs.Wcs = wcs;
    }

    internal static async Task<SolveResult?> EnsureSolutionAsync(ParallaxRequest request, string originalPath,
        Func<string, Task<SolveResult>> solve, CancellationToken cancellationToken)
    {
        if (request.Inputs.Wcs is not null) return null;
        // Astronomy snapshots lose their headers; try the aligned original
        // first. Raster snapshots may normalize EXIF orientation, so their
        // private stars/snapshot remain the safe pixel-aligned solve source.
        string extension = Path.GetExtension(originalPath).ToLowerInvariant();
        string[] paths = extension is ".fits" or ".fit" or ".fts" or ".xisf"
            ? [originalPath, request.Inputs.Image] : [request.Inputs.Image];
        SolveResult? result = null;
        for (int i = 0; i < paths.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { result = await solve(paths[i]); }
            catch (SeizaCoreException) when (i + 1 < paths.Length) { continue; }
            cancellationToken.ThrowIfCancellationRequested();
            request.Inputs.Wcs = result.Wcs;
            return result;
        }
        return null;
    }
}
