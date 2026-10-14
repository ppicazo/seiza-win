using Seiza.App.Models;

namespace Seiza.App.Services;

// Protect both the immutable native video's inputs and the current draft's
// artwork/masks, even when preparation or refitting has not applied the draft.
internal static class ParallaxInputPaths
{
    internal static IEnumerable<string?> FromInputs(ParallaxInputs inputs) =>
        [inputs.Image, inputs.Starless, inputs.Stars, inputs.DustMap, inputs.Objects,
            inputs.ObjectDistances, inputs.StarDistances, inputs.GaiaCache, inputs.RcAstroExecutable];

    internal static IEnumerable<string?> FromVideo(ParallaxVideoSettings video) =>
        [video.Watermark.Image, video.IntroCard, video.OutroCard, .. video.Tour.Select(stop => stop.Card)];

    internal static IEnumerable<string?> FromScene(ParallaxSceneSettings scene) =>
        scene.Planes?.Select(plane => plane.Mask) ?? [];

    internal static IEnumerable<string?> FromComposition(ParallaxComposition composition) =>
        [composition.WatermarkImage, composition.IntroCard, composition.OutroCard,
            .. composition.Stops.Select(stop => stop.Card), .. FromScene(composition.Scene)];

    internal static string[] Capture(IEnumerable<string?> paths) => paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => LiveStackPath.NormalizeForComparison(path!))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

// Replacing a selected map/artwork or preparing a new scene must not make an
// original input safe to overwrite later in the same document's lifetime.
internal sealed class ParallaxInputHistory
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    internal IEnumerable<string> Paths => _paths;

    internal void Remember(IEnumerable<string?> paths)
    {
        foreach (string? path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try { _paths.Add(LiveStackPath.NormalizeForComparison(path)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Invalid draft paths remain editable. They cannot be opened
                // as native inputs and preparation will report their errors.
            }
        }
    }
}
