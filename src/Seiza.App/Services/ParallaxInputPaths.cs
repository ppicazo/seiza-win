using Seiza.App.Models;

namespace Seiza.App.Services;

// Protect both the immutable native video's inputs and the current draft's
// artwork, even when a camera refit has not yet applied those draft changes.
internal static class ParallaxInputPaths
{
    internal static IEnumerable<string?> FromInputs(ParallaxInputs inputs) =>
        [inputs.Image, inputs.Starless, inputs.Stars, inputs.DustMap, inputs.Objects,
            inputs.ObjectDistances, inputs.StarDistances, inputs.GaiaCache, inputs.RcAstroExecutable];

    internal static IEnumerable<string?> FromVideo(ParallaxVideoSettings video) =>
        [video.Watermark.Image, video.IntroCard, video.OutroCard, .. video.Tour.Select(stop => stop.Card)];

    internal static IEnumerable<string?> FromComposition(ParallaxComposition composition) =>
        [composition.WatermarkImage, composition.IntroCard, composition.OutroCard,
            .. composition.Stops.Select(stop => stop.Card)];

    internal static string[] Capture(IEnumerable<string?> paths) => paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => LiveStackPath.NormalizeForComparison(path!))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
