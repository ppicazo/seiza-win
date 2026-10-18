using System.Text.Json;
using System.Text.Json.Serialization;

namespace Seiza.App.Models;

internal sealed record FlatQualityOptions
{
    public bool Enabled { get; init; }
    public double FullScale { get; init; } = 65535;
    public bool UseOptics { get; init; }
    public double FocalRatio { get; init; }
    public double PixelSizeUm { get; init; }
    public double Obstruction { get; init; }

    public void Validate()
    {
        if (!Enabled) return;
        if (!double.IsFinite(FullScale) || FullScale <= 0)
            throw new ArgumentException("Enter a positive saturation level in the flat's raw sample units.");
        if (UseOptics && (!double.IsFinite(FocalRatio) || FocalRatio <= 0 ||
            !double.IsFinite(PixelSizeUm) || PixelSizeUm <= 0 || !double.IsFinite(Obstruction) || Obstruction < 0 || Obstruction >= 1))
            throw new ArgumentException("Dust distance needs a positive focal ratio and binned pixel size, and an obstruction below 100%.");
    }
}

internal sealed record FlatQualityMeasureRequest(string Path, double FullScale, bool LargeFeatures,
    double? FocalRatio, double? PixelSizeUm, double? Obstruction);
internal sealed record FlatQualityScreenRequest(FlatQualityMeasurement[] Measurements);

// Preserve complete native geometry when sending measurements back for screening.
// Extension data keeps additive native fields lossless too.
internal sealed record FlatQualityMeasurement
{
    public required int AlgorithmVersion { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double FullScale { get; init; }
    public required double Level { get; init; }
    public required double[] ChannelLevels { get; init; }
    public required double ClippedFraction { get; init; }
    public required double Noise { get; init; }
    public required double DetectionDepth { get; init; }
    public required FlatIllumination Illumination { get; init; }
    public required int FeatureCount { get; init; }
    public required FlatFeature[] Features { get; init; }
    public required FlatVignetting[] Vignetting { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
internal sealed record FlatIllumination(int Columns, int Rows, double[] Response);
internal sealed record FlatFeature(double X, double Y, int Left, int Top, int Right, int Bottom,
    int Pixels, double Depth, string Shape, double Radius, double? Hole, double? DistanceMm, FlatSeenIn? SeenIn)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
internal sealed record FlatSeenIn(int Frames, int Of);
internal sealed record FlatVignetting(string Edge, double X, double Y, double Area, double Depth);
internal sealed record FlatFinding(string Code, string Grade, double Value, double Limit)
{
    [JsonIgnore] private string Label => Code switch
    {
        "clipped" => "Clipped pixels", "bright" => "Brightest channel level", "dim" => "Dimmest channel level",
        "illumination" => "Illumination difference", "features" => "Compact dips",
        "changing_features" => "Changing dips", "carried_features" => "Changing dips carried into the master",
        _ => Code.Replace('_', ' '),
    };
    [JsonIgnore] public string Description => Code is "clipped" or "bright" or "dim" or "illumination"
        ? $"{Label}: {Value:P2} (limit {Limit:P2})"
        : $"{Label}: {Value:G6} (limit {Limit:G6})";
}
internal sealed record FlatVerdict(string Grade, FlatFinding[] Findings);
internal sealed record FlatQualityReport(int SchemaVersion, FlatVerdict[] Frames, FlatVerdict Master,
    int SteadyFeatures, int ChangingFeatures, int CarriedFeatures);
internal sealed record FlatQualityReview(string[] Paths, FlatQualityMeasurement[] Measurements,
    FlatQualityReport Report, string? GroupTitle = null)
{
    [JsonIgnore] public string Summary => $"{Paths.Length} flats reviewed; " +
        $"{Report.Frames.Count(frame => frame.Grade == "suspect")} suspect, {Report.Frames.Count(frame => frame.Grade == "poor")} poor.";
    [JsonIgnore] public string DustSummary => $"Compact dust: {Report.SteadyFeatures} steady, {Report.ChangingFeatures} changing, " +
        $"{Report.CarriedFeatures} changing dips may survive in the combined master. Steady dust is what flats correct; dust alone is not a reason to reject a flat.";

    public string CompleteReport() => (GroupTitle is null ? "" : GroupTitle + Environment.NewLine) + Summary +
        Environment.NewLine + DustSummary + Environment.NewLine +
        "Selection unchanged. This reviews the flats, not which flat session matches a light." + Environment.NewLine +
        JsonSerializer.Serialize(this, FlatQualityJsonContext.Default.FlatQualityReview);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(FlatQualityMeasureRequest))]
[JsonSerializable(typeof(FlatQualityScreenRequest))]
[JsonSerializable(typeof(FlatQualityMeasurement))]
[JsonSerializable(typeof(FlatQualityReport))]
[JsonSerializable(typeof(FlatQualityReview))]
internal sealed partial class FlatQualityJsonContext : JsonSerializerContext;
