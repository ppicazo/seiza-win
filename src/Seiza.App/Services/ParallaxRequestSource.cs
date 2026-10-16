using Seiza.App.Models;

namespace Seiza.App.Services;

// Keep the rendered input and solve policy separate: original astronomy headers
// are useful, but a known starless source must only solve its aligned stars.
internal static class ParallaxRequestSource
{
    internal static bool SameSource(string source, string selected) =>
        string.Equals(Path.GetFullPath(source), Path.GetFullPath(selected), StringComparison.OrdinalIgnoreCase) ||
        WindowsFileIdentity.TryGet(source) is { } identity && identity == WindowsFileIdentity.TryGet(selected);

    internal static string? SolveIssue(bool hasWcs, bool sourceIsStarless, string? stars, bool layersAligned) =>
        hasWcs || !sourceIsStarless ? null : stars is null
            ? "The opened image is starless. Choose an aligned Stars image in Source before solving."
            : !layersAligned ? "The opened image is starless. Enable Layers match source alignment and stretch to solve the selected stars layer." : null;
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
        Func<string, Task<SolveResult>> solve, CancellationToken cancellationToken,
        bool sourceIsStarless = false, bool layersAligned = true, string? snapshotPath = null)
    {
        if (request.Inputs.Wcs is not null) return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (SolveIssue(false, sourceIsStarless, request.Inputs.Stars, layersAligned) is { } issue)
            throw new InvalidOperationException(issue);
        // Astronomy snapshots lose their headers; try the aligned original
        // first. Raster snapshots may normalize EXIF orientation, so their
        // private stars/snapshot remain the safe pixel-aligned solve source.
        string extension = Path.GetExtension(originalPath).ToLowerInvariant();
        string safeImage = layersAligned ? request.Inputs.Image : snapshotPath ?? originalPath;
        string[] paths = sourceIsStarless ? [request.Inputs.Stars!] :
            extension is ".fits" or ".fit" or ".fts" or ".xisf" ? [originalPath, safeImage] : [safeImage];
        paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        SolveResult? result = null;
        for (int i = 0; i < paths.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { result = await solve(paths[i]); }
            catch (SeizaCoreException) when (i + 1 < paths.Length) { continue; }
            catch (SeizaCoreException error) when (!layersAligned && request.Inputs.Stars is not null)
            { throw new SeizaCoreException(error.Message + " Enable Layers match source alignment and stretch in Source to solve the selected stars layer."); }
            cancellationToken.ThrowIfCancellationRequested();
            request.Inputs.Wcs = result.Wcs;
            return result;
        }
        return null;
    }
}
