using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Seiza.App.Models;

public enum ParallaxMotion { FlyIn, Tour }

// Drafts and markers use uncropped source pixels. Only native requests and
// portable itineraries use pixels relative to the filmed rectangle.
public static class ParallaxGeometry
{
    public static bool ValidCrop(int[]? crop) => crop is null ||
        crop.Length == 4 && crop[0] >= 0 && crop[1] >= 0 && crop[2] >= 16 && crop[3] >= 16;
    public static bool CropInside(int[]? crop, int width, int height) => ValidCrop(crop) &&
        (crop is null || (long)crop[0] + crop[2] <= width && (long)crop[1] + crop[3] <= height);
    public static double[]? ToCrop(double[]? point, int[]? crop) => point is null ? null :
        [point[0] - (crop?[0] ?? 0), point[1] - (crop?[1] ?? 0)];
    public static double[]? ToSource(double[]? point, int[]? crop) => point is null ? null :
        [point[0] + (crop?[0] ?? 0), point[1] + (crop?[1] ?? 0)];
    public static (int Width, int Height) ImageSize(int width, int height, int longSide)
    {
        if (width <= 0 || height <= 0 || longSide is < 16 or > 3840 || longSide % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(longSide), "Image dimensions must be positive and the long side even, between 16 and 3840.");
        int shortSide = Math.Clamp((int)Math.Round(longSide * ((double)Math.Min(width, height) / Math.Max(width, height)) / 2,
            MidpointRounding.AwayFromZero) * 2, 16, longSide);
        return width >= height ? (longSide, shortSide) : (shortSide, longSide);
    }
    public static int[] CropFromDrag(double ax, double ay, double bx, double by, int sourceWidth, int sourceHeight, double? aspect = null)
    {
        ax = Math.Clamp(ax, 0, sourceWidth); ay = Math.Clamp(ay, 0, sourceHeight);
        bx = Math.Clamp(bx, 0, sourceWidth); by = Math.Clamp(by, 0, sourceHeight);
        double width = Math.Abs(bx - ax), height = Math.Abs(by - ay);
        if (aspect is > 0 && double.IsFinite(aspect.Value))
        {
            width = Math.Min(width, height * aspect.Value); height = width / aspect.Value;
        }
        int w = (int)Math.Floor(width), h = (int)Math.Floor(height);
        return [(int)Math.Floor(bx >= ax ? ax : ax - w), (int)Math.Floor(by >= ay ? ay : ay - h), w, h];
    }
}

public sealed record ParallaxStop
{
    [JsonIgnore] public Guid Id { get; set; } = Guid.NewGuid();
    public string? Name { get; set; }
    public double[]? Focus { get; set; }
    public double Dolly { get; set; }
    public double Zoom { get; set; } = 1;
    public double RotateDegrees { get; set; }
    public double Pan { get; set; }
    public double Travel { get; set; } = 4;
    public double Hold { get; set; } = 1;
    public double SpinDegrees { get; set; }
    public double Push { get; set; }
    public string? Title { get; set; }
    // An image shown during this stop in place of its title, whether or not
    // titles are shown: a title card with its transparency kept.
    public string? Card { get; set; }
    // Null centres the stop on the plane currently under its focus. A given
    // depth preserves imported/planned framing in parsecs; moving the point
    // drops it so the new point uses its own plane unless explicitly overridden.
    public double? DepthParsecs { get; set; }
    [JsonIgnore] public string DisplayName => Name ?? (Focus is null ? "Whole Image" : "Custom Point");
    public ParallaxStop DeepClone() => this with { Focus = Focus?.ToArray() };
}

public sealed record ParallaxTourOptions
{
    public int? Targets { get; set; } = 5;
    public double Hold { get; set; } = 1.5;
    public double Motion { get; set; } = 1;
}

public sealed record ParallaxTourPlan
{
    public int SchemaVersion { get; set; } = 1;
    public double[] Focus { get; set; } = [];
    public string FocusName { get; set; } = string.Empty;
    public double Seconds { get; set; }
    public List<ParallaxStop> Tour { get; set; } = [];
    // Optional for upstream plans and tours saved before these controls existed.
    public double? TourGlide { get; set; }
    public bool? TourTitles { get; set; }
    public bool? TourLoop { get; set; }
    // Optional source rectangle for a saved itinerary's crop-relative points.
    // Full-image saves include [0,0,width,height] to disambiguate older files.
    public int[]? Crop { get; set; }
    [JsonIgnore] public bool RestoreCrop { get; set; }
    // Planned on the prepared scene, whose depths its stops carry, rather than
    // from the catalogue alone.
    [JsonIgnore] public bool PlannedOnScene { get; set; }
    // File itineraries may explicitly author a target depth even when they
    // were not generated on this currently prepared scene.
    [JsonIgnore] public bool PreserveStopDepths { get; set; }
    public string ToJson() => ParallaxJson.SerializePlan(this);
    public static ParallaxTourPlan FromJson(string json) => ParallaxJson.DeserializePlan(json);

    public ParallaxTourPlan InSourceCoordinates(int[]? crop) => MapCoordinates(crop, toSource: true);
    public ParallaxTourPlan InCropCoordinates(int[]? crop) => MapCoordinates(crop, toSource: false);
    private ParallaxTourPlan MapCoordinates(int[]? crop, bool toSource) => this with
    {
        Focus = (toSource ? ParallaxGeometry.ToSource(Focus, crop) : ParallaxGeometry.ToCrop(Focus, crop))!,
        Crop = crop?.ToArray(),
        RestoreCrop = toSource,
        Tour = Tour.Select(stop => stop.DeepClone() with
        {
            Focus = toSource ? ParallaxGeometry.ToSource(stop.Focus, crop) : ParallaxGeometry.ToCrop(stop.Focus, crop),
        }).ToList(),
    };

    // File itineraries carry cards beside the tour, not beside the running
    // executable. Keep the context-free overloads for the native planning API.
    public static ParallaxTourPlan FromJson(string json, string itineraryPath)
    {
        string folder = ItineraryFolder(itineraryPath);
        return FromJson(json).WithCardPaths(card => Path.IsPathFullyQualified(card) ? card : Path.GetFullPath(card, folder))
            with { PreserveStopDepths = true };
    }

    public string ToJson(string itineraryPath)
    {
        string folder = ItineraryFolder(itineraryPath);
        return WithCardPaths(card =>
        {
            // Keep external drive/UNC assets rooted; only assets carried in the
            // tour's own folder become portable relative paths. A relative
            // draft path still means the current working directory until saved.
            string absolute = Path.IsPathFullyQualified(card) ? card : Path.GetFullPath(card);
            string relative = Path.GetRelativePath(folder, absolute);
            return Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? absolute :
                relative.Replace(Path.DirectorySeparatorChar, '/');
        }).ToJson();
    }

    private static string ItineraryFolder(string itineraryPath) => Path.GetDirectoryName(Path.GetFullPath(itineraryPath))!;

    private ParallaxTourPlan WithCardPaths(Func<string, string> map) => this with
    {
        Focus = Focus.ToArray(),
        Crop = Crop?.ToArray(),
        Tour = Tour.Select(stop => stop.DeepClone() with { Card = stop.Card is null ? null : map(stop.Card) }).ToList(),
    };

    public void Validate()
    {
        if (SchemaVersion != 1 || !ParallaxGeometry.ValidCrop(Crop) || !ParallaxComposition.ValidPoint(Focus) || Focus.Length != 2 || Tour is null || Tour.Count < 2)
            throw new InvalidDataException("The tour plan has an unsupported schema or invalid stops.");
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, Stops = Tour };
        composition.Scene.DistanceFocus = Focus;
        composition.TourGlide = TourGlide ?? composition.TourGlide;
        composition.TourLoop = TourLoop ?? composition.TourLoop;
        if (composition.ValidationMessage is string message)
            throw new InvalidDataException($"Invalid tour plan: {message}");
    }
}

public sealed record ParallaxSceneSettings
{
    public int[]? Crop { get; set; }
    public double[]? DistanceFocus { get; set; }
    public double? DistanceParsecs { get; set; }
    public double? UnmatchedDistanceParsecs { get; set; }
    public bool Online { get; set; }
    public double GaiaMaxMagnitude { get; set; } = 16;
    public int? MaxStars { get; set; }
    public string SmallStars { get; set; } = "field";
    public bool KeepGalaxies { get; set; }
    public bool Dust { get; set; } = true;
    public double DustOpacity { get; set; } = 3;
    public double MinimumScaleArcsecPerPixel { get; set; } = 0.1;
    public double MaximumScaleArcsecPerPixel { get; set; } = 20;
    // Catalogued nebulae and clouds of dust at distances of their own move on
    // planes of their own. Left out when off, the core's default.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AutoPlanes { get; set; }
    // Distances given for catalogued objects, by name, in place of the
    // catalogue's, for the nebula's distance and its planes. Null when none.
    public List<ParallaxDistanceOverride>? DistanceOverrides { get; set; }
    // Painted masks defining regions of the starless image at given depths.
    // Null leaves the native default (no manually defined planes) unchanged.
    public List<ParallaxDepthPlane>? Planes { get; set; }
    public ParallaxSceneSettings DeepClone() => this with
    {
        DistanceFocus = DistanceFocus?.ToArray(),
        Crop = Crop?.ToArray(),
        DistanceOverrides = DistanceOverrides?.Select(given => given with { }).ToList(),
        Planes = Planes?.Select(plane => plane with { }).ToList(),
    };
    public bool ContentEquals(ParallaxSceneSettings? other) => other is not null &&
        ParallaxJson.SerializeScene(this) == ParallaxJson.SerializeScene(other);

    public double? DistanceOverride(string name) =>
        DistanceOverrides?.FirstOrDefault(given => SameObject(given.ObjectName, name))?.DistanceParsecs;

    // Each object takes one distance: a new one replaces the old.
    public void SetDistanceOverride(string name, double parsecs)
    {
        ArgumentNullException.ThrowIfNull(name);
        var given = new ParallaxDistanceOverride { ObjectName = name.Trim(), DistanceParsecs = parsecs };
        int index = DistanceOverrides?.FindIndex(old => SameObject(old.ObjectName, name)) ?? -1;
        if (index >= 0)
        {
            DistanceOverrides![index] = given;
            // Repair duplicates from externally supplied drafts while keeping
            // the first row's position stable for the inspector.
            for (int i = DistanceOverrides.Count - 1; i > index; i--)
                if (SameObject(DistanceOverrides[i].ObjectName, name)) DistanceOverrides.RemoveAt(i);
        }
        else (DistanceOverrides ??= []).Add(given);
    }

    public void RemoveDistanceOverride(string name)
    {
        DistanceOverrides?.RemoveAll(given => SameObject(given.ObjectName, name));
        if (DistanceOverrides is { Count: 0 }) DistanceOverrides = null;
    }

    // The core finds objects by name whatever their case.
    internal static bool SameObject(string? first, string? second) =>
        string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed record ParallaxDistanceOverride
{
    [JsonPropertyName("object")] public string ObjectName { get; set; } = string.Empty;
    public double DistanceParsecs { get; set; }
}

public sealed record ParallaxDepthPlane
{
    [JsonIgnore] public Guid Id { get; set; } = Guid.NewGuid();
    public string Mask { get; set; } = string.Empty;
    public double DistanceParsecs { get; set; } = 400;
    public string? Name { get; set; }
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(Mask) : Name;
}

public sealed record ParallaxLabel
{
    [JsonIgnore] public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; }
    public double Y { get; set; }
    public double Radius { get; set; }
    public string Text { get; set; } = "Label";
}

// The watermark: a line of text, an image such as a logo, or both, in one
// corner. With no image and the corner, size and opacity at the core's
// defaults it is sent as false or the text alone, as before images.
[JsonConverter(typeof(ParallaxWatermarkConverter))]
public sealed record ParallaxWatermark(string? Text)
{
    public const string DefaultCorner = "bottomRight";
    public const double DefaultSize = 0.06;
    public const double DefaultOpacity = 0.8;
    public static readonly string[] Corners = ["bottomRight", "bottomLeft", "topRight", "topLeft"];
    public static ParallaxWatermark Disabled { get; } = new((string?)null);
    public static ParallaxWatermark FromText(string text) => new(text);
    public string? Image { get; init; }
    public string Corner { get; init; } = DefaultCorner;
    // The image's height as a share of the frame's shorter side.
    public double Size { get; init; } = DefaultSize;
    public double Opacity { get; init; } = DefaultOpacity;
    [JsonIgnore] public bool Plain => Image is null && Corner == DefaultCorner && Size == DefaultSize && Opacity == DefaultOpacity;
}

// Reconfigure takes COMPLETE camera/output settings; omitted fields take native
// defaults, not values from the old handle. Never put scene fields in this type.
public sealed record ParallaxVideoSettings
{
    public string? Framing { get; set; } = "fill";
    public double[]? Focus { get; set; }
    public string Start { get; set; } = "whole";
    public double Dolly { get; set; } = 0.4;
    public double Truck { get; set; }
    public double TruckAngleDegrees { get; set; }
    public double Pan { get; set; }
    public double Zoom { get; set; } = 1;
    public double ZoomEnd { get; set; } = 1;
    public double[] RotateDegrees { get; set; } = [0, 0];
    public string Easing { get; set; } = "inOut";
    public string Quality { get; set; } = "standard";
    public double GrowthLimit { get; set; } = 4;
    public double FadeFrom { get; set; } = 6;
    public List<ParallaxStop> Tour { get; set; } = [];
    public double TourGlide { get; set; } = 0.2;
    public bool TourTitles { get; set; }
    public bool TourLoop { get; set; }
    public double Seconds { get; set; } = 8;
    public int Fps { get; set; } = 30;
    public string Size { get; set; } = "1920x1080";
    public bool Overlay { get; set; }
    public double OverlayDensity { get; set; } = 0.6;
    public List<ParallaxLabel> Labels { get; set; } = [];
    public string LabelColor { get; set; } = "#F0F4F8";
    public ParallaxWatermark Watermark { get; set; } = ParallaxWatermark.Disabled;
    // Title cards over the start and the end, each shown for CardSeconds.
    public string? IntroCard { get; set; }
    public string? OutroCard { get; set; }
    public double CardSeconds { get; set; } = 3;
    public string ToJson() => ParallaxJson.SerializeVideo(this);
}

public sealed record ParallaxComposition
{
    public ParallaxMotion Motion { get; set; }
    public List<ParallaxStop> Stops { get; set; } = [new() { Travel = 0 }, new() { Name = "Destination", Dolly = 0.4, Hold = 2 }];
    public ParallaxSceneSettings Scene { get; set; } = new();
    public string DistanceName { get; set; } = "Image center";
    public double[]? Focus { get; set; }
    public string FocusName { get; set; } = "Image center";
    public double Seconds { get; set; } = 8;
    public double Dolly { get; set; } = 0.4;
    public double Pan { get; set; }
    public double Rotation { get; set; }
    public double RotationStart { get; set; }
    public string Start { get; set; } = "whole";
    public double Truck { get; set; }
    public double TruckAngleDegrees { get; set; }
    public double Zoom { get; set; } = 1;
    public double ZoomEnd { get; set; } = 1;
    public string Easing { get; set; } = "inOut";
    public double TourGlide { get; set; } = 0.2;
    public bool TourTitles { get; set; }
    public bool TourLoop { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public string Framing { get; set; } = "fill";
    public bool MatchImageShape { get; set; }
    public int ImageLongSide { get; set; } = 1920;
    [JsonIgnore] public int SourceWidth { get; set; }
    [JsonIgnore] public int SourceHeight { get; set; }
    [JsonIgnore] public (int Width, int Height) OutputDimensions => MatchImageShape
        ? ParallaxGeometry.ImageSize(Scene.Crop?[2] ?? SourceWidth, Scene.Crop?[3] ?? SourceHeight, ImageLongSide)
        : (Width, Height);
    public int Fps { get; set; } = 30;
    public string Quality { get; set; } = "standard";
    public double GrowthLimit { get; set; } = 4;
    public double FadeFrom { get; set; } = 6;
    public bool Overlay { get; set; }
    public double OverlayDensity { get; set; } = 0.6;
    public List<ParallaxLabel> Labels { get; set; } = [];
    public string LabelColor { get; set; } = "#F0F4F8";
    public bool CreditEnabled { get; set; }
    public string Credit { get; set; } = "Rendered with seiza.fyi";
    // An image, such as a logo, in the credit's corner; the corner and opacity
    // apply to the credit text as well.
    public string? WatermarkImage { get; set; }
    public string WatermarkCorner { get; set; } = ParallaxWatermark.DefaultCorner;
    public double WatermarkSize { get; set; } = ParallaxWatermark.DefaultSize;
    public double WatermarkOpacity { get; set; } = ParallaxWatermark.DefaultOpacity;
    // Title cards: images with transparency over the start and the end.
    public string? IntroCard { get; set; }
    public string? OutroCard { get; set; }
    public double CardSeconds { get; set; } = 3;

    public ParallaxComposition DeepClone() => this with
    {
        Focus = Focus?.ToArray(), Scene = Scene.DeepClone(),
        Stops = Stops.Select(stop => stop.DeepClone()).ToList(),
        Labels = Labels.Select(label => label with { }).ToList(),
    };

    // Record equality is shallow for lists; compare values for undo/debounce.
    public bool ContentEquals(ParallaxComposition? other) => other is not null &&
        ParallaxJson.SerializeComposition(this) == ParallaxJson.SerializeComposition(other);

    [JsonIgnore] public double Duration => Motion == ParallaxMotion.Tour
        ? Stops.Select((stop, index) => stop.Hold + (index == 0 ? 0 : stop.Travel)).Sum() + LoopClosingDuration : Seconds;

    [JsonIgnore] public double LoopClosingDuration
    {
        get
        {
            if (Motion != ParallaxMotion.Tour || !TourLoop || Stops.Count < 2) return 0;
            ParallaxStop first = Stops[0], last = Stops[^1];
            if (PointsEqual(first.Focus, last.Focus) && first.Dolly == last.Dolly && first.Zoom == last.Zoom && first.Pan == last.Pan) return 0;
            return Stops.Skip(1).Average(stop => stop.Travel) + Math.Max(first.Hold * 3, 3);
        }
    }

    public double ArrivalAt(int index) => index < 0 || index >= Stops.Count ? 0 :
        Stops.Take(index).Sum(stop => stop.Hold) + Stops.Take(index + 1).Skip(1).Sum(stop => stop.Travel);

    // Replace the draft's tour with `plan`. A tour planned from the catalogue
    // alone also moves the distance reference to its focus, the target most
    // worth a visit, and its stops centre on the planes the scene shows at
    // their points. One planned on the prepared scene keeps the scene's
    // distance reference, as moving it would prepare the scene again, and its
    // stops keep the depths the scene gave them.
    public void ApplyTour(ParallaxTourPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Motion = ParallaxMotion.Tour;
        Stops = plan.Tour.Select(stop => stop.DeepClone() with
        {
            DepthParsecs = plan.PlannedOnScene || plan.PreserveStopDepths ? stop.DepthParsecs : null,
        }).ToList();
        if (!plan.PlannedOnScene) { Scene.DistanceFocus = plan.Focus.ToArray(); DistanceName = plan.FocusName; }
        if (plan.RestoreCrop || plan.Crop is not null) Scene.Crop = plan.Crop?.ToArray();
        TourGlide = plan.TourGlide ?? TourGlide; TourTitles = plan.TourTitles ?? TourTitles; TourLoop = plan.TourLoop ?? TourLoop;
    }

    public void SetSpin(int index, bool clockwise)
    {
        if (index < 0 || index >= Stops.Count) return;
        Stops[index].SpinDegrees = clockwise ? -360 : 360;
        if (Stops[index].Hold == 0) Stops[index].Hold = 4;
    }

    public void SetTravelTurn(int index, bool clockwise)
    {
        if (index <= 0 || index >= Stops.Count) return;
        Stops[index].RotateDegrees = Stops[index - 1].RotateDegrees + (clockwise ? -360 : 360);
    }

    [JsonIgnore] public string? ValidationMessage
    {
        get
        {
            if (Framing is not ("fill" or "fit")) return "Choose Fill frame or Show whole image.";
            if (!ParallaxGeometry.ValidCrop(Scene?.Crop) || SourceWidth > 0 && SourceHeight > 0 && !ParallaxGeometry.CropInside(Scene?.Crop, SourceWidth, SourceHeight))
                return "The crop must be inside the source image and at least 16 pixels each way.";
            if (MatchImageShape && ((Scene?.Crop?[2] ?? SourceWidth) <= 0 || (Scene?.Crop?[3] ?? SourceHeight) <= 0 ||
                ImageLongSide is < 16 or > 3840 || ImageLongSide % 2 != 0)) return "Choose an even image-shaped output size between 16 and 3840 pixels.";
            if ((!MatchImageShape && (Width is < 16 or > 3840 || Height is < 16 or > 3840 || Width % 2 != 0 || Height % 2 != 0)) || Fps is not (24 or 30 or 60))
                return "Choose even dimensions between 16 and 3840 pixels and 24, 30 or 60 fps.";
            if (Stops is null || Stops.Any(stop => stop is null) || Scene is null || Labels is null ||
                Scene.DistanceOverrides?.Any(given => given is null || string.IsNullOrWhiteSpace(given.ObjectName)) == true ||
                Scene.Planes?.Any(plane => plane is null || string.IsNullOrWhiteSpace(plane.Mask)) == true)
                return "The composition is incomplete.";
            if (!double.IsFinite(Duration) || Duration <= 0 || Duration > 600) return "The video must be longer than zero and at most 10 minutes.";
            if (new[] { Scene.DistanceParsecs, Scene.UnmatchedDistanceParsecs }
                .Concat(Scene.DistanceOverrides?.Select(given => (double?)given.DistanceParsecs) ?? [])
                .Concat(Scene.Planes?.Select(plane => (double?)plane.DistanceParsecs) ?? [])
                .Any(value => value is double distance && (!double.IsFinite(distance) || distance <= 0)))
                return "Distances must be positive, finite parsec values.";
            if (Scene.DistanceOverrides is { } overrides && overrides.Select(given => given.ObjectName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != overrides.Count)
                return "Each named object can have only one distance override.";
            if (!ValidPoint(Scene.DistanceFocus) || !double.IsFinite(Scene.GaiaMaxMagnitude) || Scene.GaiaMaxMagnitude is < 1 or > 25 ||
                !double.IsFinite(Scene.DustOpacity) || Scene.DustOpacity < 0 || Scene.MaxStars is < 0 || Scene.SmallStars is not ("field" or "drop") ||
                !double.IsFinite(Scene.MinimumScaleArcsecPerPixel) || !double.IsFinite(Scene.MaximumScaleArcsecPerPixel) ||
                Scene.MinimumScaleArcsecPerPixel <= 0 || Scene.MaximumScaleArcsecPerPixel < Scene.MinimumScaleArcsecPerPixel)
                return "Check the scene's distance reference, star limit, dust and solve settings.";
            if (!ValidPoint(Focus) || !Finite(Dolly, Pan, Rotation, RotationStart, Truck, TruckAngleDegrees, Zoom, ZoomEnd, TourGlide, GrowthLimit, FadeFrom) ||
                Dolly is < 0 or >= 1 || Pan is < 0 or > 1 || Zoom < 1 || ZoomEnd < 1 || TourGlide < 0 || GrowthLimit < 1 || FadeFrom <= 0 ||
                Start is not ("whole" or "focus") || Quality is not ("standard" or "high") || Easing is not ("inOut" or "linear"))
                return "Check the camera position, zoom, roll, glide and star appearance settings.";
            if (!double.IsFinite(OverlayDensity) || OverlayDensity is < 0 or > 1 || !Regex.IsMatch(LabelColor ?? string.Empty, "^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant) ||
                Labels.Any(label => label is null || !Finite(label.X, label.Y, label.Radius) || label.Radius < 0 || string.IsNullOrEmpty(label.Text)))
                return "Labels need valid positions, nonnegative radii and a color in #RRGGBB format.";
            if (!Finite(WatermarkSize, WatermarkOpacity, CardSeconds) || WatermarkSize is <= 0 or > 1 ||
                WatermarkOpacity is < 0 or > 1 || CardSeconds <= 0 || !ParallaxWatermark.Corners.Contains(WatermarkCorner))
                return "Check the watermark's corner, size and opacity, and the title cards' seconds.";
            if (Motion == ParallaxMotion.Tour)
            {
                if (Stops.Count < 2) return "A tour needs at least two stops.";
                if (Stops.Any(stop => !ValidPoint(stop.Focus) || !Finite(stop.Dolly, stop.Pan, stop.Zoom, stop.RotateDegrees, stop.Travel, stop.Hold, stop.SpinDegrees, stop.Push) ||
                    stop.Dolly is < 0 or >= 1 || stop.Pan is < 0 or > 1 || stop.Zoom <= 0 || stop.Push is < 0 or >= 1 || stop.Travel < 0 || stop.Hold < 0 ||
                    stop.DepthParsecs is double depth && (!double.IsFinite(depth) || depth <= 0)))
                    return "Every stop needs valid framing, spin, push and nonnegative times.";
            }
            return null;
        }
    }

    public ParallaxVideoSettings VideoSettings(bool preview)
    {
        (int width, int height) = OutputDimensions;
        width = Math.Max(16, width / 2 * 2); height = Math.Max(16, height / 2 * 2);
        if (preview && Math.Max(width, height) > 960)
        {
            bool landscape = width >= height;
            int longSide = Math.Max(width, height), shortSide = Math.Min(width, height);
            int previewShort = Math.Max(16, (int)(shortSide * (960.0 / longSide)) / 2 * 2);
            // Use the even short side to choose one common scale. Thin outputs
            // may need a preview longer than 960 to keep the minimum side at 16;
            // only the long side is rounded, by at most one pixel, afterwards.
            double scale = (double)previewShort / shortSide;
            int previewLong = Math.Min(longSide, (int)Math.Round(longSide * scale / 2, MidpointRounding.AwayFromZero) * 2);
            (width, height) = landscape ? (previewLong, previewShort) : (previewShort, previewLong);
        }
        return new()
        {
            Framing = Framing, Focus = ParallaxGeometry.ToCrop(Focus, Scene.Crop), Start = Start, Dolly = Dolly, Truck = Truck, TruckAngleDegrees = TruckAngleDegrees,
            Pan = Pan, Zoom = Zoom, ZoomEnd = ZoomEnd, RotateDegrees = [RotationStart, Rotation], Easing = Easing,
            Quality = preview ? "standard" : Quality, GrowthLimit = GrowthLimit, FadeFrom = FadeFrom,
            Tour = Motion == ParallaxMotion.Tour ? Stops.Select(stop => stop.DeepClone() with { Focus = ParallaxGeometry.ToCrop(stop.Focus, Scene.Crop) }).ToList() : [], TourGlide = TourGlide,
            TourTitles = Motion == ParallaxMotion.Tour && TourTitles, TourLoop = Motion == ParallaxMotion.Tour && TourLoop,
            Seconds = Duration, Fps = Fps, Size = FormattableString.Invariant($"{width}x{height}"), Overlay = Overlay,
            OverlayDensity = OverlayDensity, Labels = Labels.Select(label => label with { X = label.X - (Scene.Crop?[0] ?? 0), Y = label.Y - (Scene.Crop?[1] ?? 0) }).ToList(), LabelColor = LabelColor,
            Watermark = new ParallaxWatermark(CreditEnabled ? Credit : null)
            {
                Image = WatermarkImage, Corner = WatermarkCorner, Size = WatermarkSize, Opacity = WatermarkOpacity,
            },
            IntroCard = IntroCard, OutroCard = OutroCard, CardSeconds = CardSeconds,
        };
    }

    internal static bool ValidPoint(double[]? point) => point is null || point.Length == 2 && point.All(double.IsFinite);
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static bool PointsEqual(double[]? first, double[]? second) => first is null ? second is null : second is not null && first.SequenceEqual(second);
}

public sealed record ParallaxInputs
{
    public string Image { get; set; } = string.Empty;
    public string? Starless { get; set; }
    public string? Stars { get; set; }
    public WcsResult? Wcs { get; set; }
    public string? CatalogDirectory { get; set; }
    public string? GaiaCache { get; set; }
    public string? Objects { get; set; }
    public string? ObjectDistances { get; set; }
    public string? StarDistances { get; set; }
    public string? RcAstroExecutable { get; set; }
    public string? RcAstroHost { get; set; }
    // A dust map, white where opaque, in place of the dust counted from the
    // stars. A scene field: reconfiguration refuses it.
    public string? DustMap { get; set; }
}

public sealed record ParallaxRequest
{
    public ParallaxInputs Inputs { get; set; } = new();
    public ParallaxSceneSettings Scene { get; set; } = new();
    public ParallaxVideoSettings Video { get; set; } = new();
    public ParallaxTourOptions? AutoTour { get; set; }
    public string ToJson() => ParallaxJson.SerializeRequest(this);
}

public sealed record ParallaxFitValue
{
    public double Asked { get; init; }
    public double Used { get; init; }
    [JsonIgnore] public bool Changed => Math.Abs(Asked - Used) > 0.0001;
}
public sealed record ParallaxStopFit
{
    public ParallaxFitValue Zoom { get; init; } = new();
    public ParallaxFitValue Pan { get; init; } = new();
    // How far the camera flew in. The fit flies a stop less far, zooming in to
    // keep its target's size, where it would show much of the smooth fill
    // behind a depth plane. Null from a core too old to say.
    public ParallaxFitValue? Dolly { get; init; }
}
public sealed record ParallaxFit
{
    public ParallaxFitValue Zoom { get; init; } = new();
    public ParallaxFitValue Pan { get; init; } = new();
    public ParallaxFitValue Lead { get; init; } = new();
    public ParallaxFitValue Truck { get; init; } = new();
    public ParallaxStopFit[] Stops { get; init; } = [];
    public bool Inside { get; init; }
    [JsonIgnore] public string? EdgeWarning => Inside ? null : "Some frames extend beyond the image. Move the focus or tour stops inward, or reduce roll and pan.";
    [JsonIgnore] public IReadOnlyList<string> Adjustments
    {
        get
        {
            var result = new List<string>();
            void Add(string name, ParallaxFitValue value)
            {
                if (value.Changed) result.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {value.Asked:F2} → {value.Used:F2}"));
            }
            Add("Zoom", Zoom); Add("Pan", Pan); Add("Sideways lead", Lead); Add("Sideways motion", Truck);
            for (int i = 0; i < Stops.Length; i++)
            {
                Add($"Stop {i + 1} zoom", Stops[i].Zoom); Add($"Stop {i + 1} pan", Stops[i].Pan);
                if (Stops[i].Dolly is { } dolly) Add($"Stop {i + 1} dolly", dolly);
            }
            return result;
        }
    }
}
public sealed record ParallaxSummary
{
    public int[]? Crop { get; init; }
    public int SchemaVersion { get; init; }
    public int Frames { get; init; }
    public int Fps { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int DetectedStars { get; init; }
    public int WithDistance { get; init; }
    public double BackgroundDistanceParsecs { get; init; }
    public string BackgroundBasis { get; init; } = string.Empty;
    public double[] BackgroundFocus { get; init; } = [];
    public ParallaxFit Fit { get; init; } = new();
    public WcsResult Wcs { get; init; } = new();
    // Whether a given map, rather than the star counts, made the dust, and
    // how many stars its opaque places brought in front of the nebula.
    public bool DustGiven { get; init; }
    public int StarsInFrontOfDust { get; init; }
    // The depth planes lifted. Empty when automatic planes are off, or from a
    // core too old to say: the generated reader sets a missing init-only
    // property to null.
    public ParallaxPlane[] Planes { get => _planes; init => _planes = value ?? []; }
    private readonly ParallaxPlane[] _planes = [];
}
// A depth plane: its distance and how it was found, the share of the image
// it covers, whether it is a dark cloud that blocks light behind it, and the
// catalogued object a given distance moves it by (null for a cloud of dust
// the star counts alone place).
public sealed record ParallaxPlane
{
    public string Name { get; init; } = string.Empty;
    public double DistanceParsecs { get; init; }
    public string Basis { get; init; } = string.Empty;
    public double Coverage { get; init; }
    public bool Absorbs { get; init; }
    [JsonPropertyName("object")] public string? ObjectName { get; init; }
}
public sealed record ParallaxEvent
{
    public string Kind { get; init; } = string.Empty;
    public string? Message { get; init; }
    public double? Fraction { get; init; }
}

public sealed record ParallaxActivityEntry(Guid Id, TimeSpan Elapsed, string Message, bool Warning);

public sealed class ParallaxActivity
{
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset LastEventAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? NetworkPhase { get; private set; }
    public int? Regions { get; private set; }
    public int Downloaded { get; private set; }
    public bool CancellationRequested { get; set; }
    public List<ParallaxActivityEntry> Entries { get; } = [];

    public ParallaxActivity(string message, DateTimeOffset? now = null)
    {
        StartedAt = LastEventAt = now ?? DateTimeOffset.UtcNow;
        Entries.Add(new(Guid.NewGuid(), TimeSpan.Zero, message, false));
    }

    public void Receive(ParallaxEvent update, DateTimeOffset? now = null)
    {
        DateTimeOffset time = now ?? DateTimeOffset.UtcNow;
        LastEventAt = time;
        if (update.Message is not string raw) return;
        string message = raw.Trim();
        Entries.Add(new(Guid.NewGuid(), time - StartedAt, message, update.Kind == "warning"));
        if (Entries.Count > 100) Entries.RemoveAt(0);
        if (message.StartsWith("fetching Gaia DR3 distances", StringComparison.Ordinal))
        {
            NetworkPhase = "Looking up Gaia star distances…"; Downloaded = 0;
            Match match = Regex.Match(message, @"\d+(?= cone\(s\)$)", RegexOptions.CultureInvariant);
            Regions = match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : null;
        }
        else if (message.EndsWith("cone(s) fetched", StringComparison.Ordinal) && NetworkPhase is not null)
        {
            if (int.TryParse(message.Split(' ')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int count)) Downloaded = count;
        }
        else if (Regex.IsMatch(message, @"^\d+ Gaia stars(?: from .*)?$", RegexOptions.CultureInvariant)) NetworkPhase = "Checking bright-star distances (Hipparcos)…";
        else if (message.Contains("matched to Gaia", StringComparison.Ordinal) || message.Contains("no Hipparcos distances", StringComparison.Ordinal) || update.Kind != "warning") NetworkPhase = null;
    }

    public string Detail(DateTimeOffset now)
    {
        string elapsed = FormatTime((FinishedAt ?? now) - StartedAt);
        if (CancellationRequested) return $"{elapsed} elapsed · Cancel requested. Waiting for the current operation to return.";
        return Regions is int regions && NetworkPhase?.StartsWith("Looking up Gaia", StringComparison.Ordinal) == true
            ? $"{elapsed} elapsed · {Downloaded} new sky regions downloaded · {regions} regions cover this image" : $"{elapsed} elapsed";
    }

    public string? WaitingMessage(DateTimeOffset now) => FinishedAt is null && !CancellationRequested && NetworkPhase is not null && (now - LastEventAt).TotalSeconds >= 15
        ? $"Waiting for the catalogue service: {FormatTime(now - LastEventAt)} since its last update. Queries can take several minutes; completed Gaia regions are cached." : null;

    private static string FormatTime(TimeSpan duration)
    {
        int seconds = Math.Max(0, (int)duration.TotalSeconds);
        return FormattableString.Invariant($"{seconds / 60}:{seconds % 60:00}");
    }
}
