using System.Text.Json;
using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxModelTests
{
    [Fact]
    public void PlannerRoundTripPreservesUnwrappedRollAndIncomingTiming()
    {
        const string json = """{"schemaVersion":1,"focus":[100,80],"focusName":"Target","seconds":7,"tour":[{"name":"Whole Image","dolly":0,"zoom":1,"rotateDegrees":20,"pan":0,"travel":99,"hold":1},{"name":"Target","focus":[100,80],"dolly":0.4,"zoom":1.2,"rotateDegrees":380,"pan":0.6,"travel":4,"hold":2}]}""";
        ParallaxTourPlan plan = ParallaxTourPlan.FromJson(json);
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, Stops = plan.Tour };
        composition.Scene.DistanceFocus = plan.Focus;
        Assert.Equal(7, composition.Duration);
        Assert.Equal(5, composition.ArrivalAt(1));
        composition.Stops[1].Name = "Renamed";
        var request = new ParallaxRequest { Inputs = new() { Image = "source.png" }, Scene = composition.Scene, Video = composition.VideoSettings(false) };
        using var document = JsonDocument.Parse(request.ToJson());
        JsonElement root = document.RootElement;
        Assert.Equal(380, root.GetProperty("tour")[1].GetProperty("rotateDegrees").GetDouble());
        Assert.Equal(0.6, root.GetProperty("tour")[1].GetProperty("pan").GetDouble());
        Assert.False(root.GetProperty("tour")[1].TryGetProperty("id", out _));
        Assert.False(root.TryGetProperty("autoTour", out _));
        Assert.False(root.TryGetProperty("inputs", out _));
        Assert.Equal(100, root.GetProperty("distanceFocus")[0].GetDouble());
        composition.SetTravelTurn(1, clockwise: true);
        Assert.Equal(-340, composition.Stops[1].RotateDegrees);
        composition.SetTravelTurn(1, clockwise: false);
        Assert.Equal(380, composition.Stops[1].RotateDegrees);
        composition.Stops[1].Hold = 0;
        Assert.Equal(5, composition.Duration);
    }

    [Fact]
    public void SpinPushTitlesAndLoopRoundTripWithoutPersistingUiIdentity()
    {
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, TourGlide = 0.35, TourTitles = true, TourLoop = true };
        composition.Stops[1].RotateDegrees = 45;
        composition.Stops[1].Push = 0.6;
        composition.Stops[1].Title = "The Iris Nebula";
        composition.SetSpin(1, clockwise: true);
        Assert.Equal(45, composition.Stops[1].RotateDegrees);
        var plan = new ParallaxTourPlan
        {
            Focus = [20, 30], FocusName = "Scene", Seconds = composition.Duration, Tour = composition.Stops,
            TourGlide = composition.TourGlide, TourTitles = composition.TourTitles, TourLoop = composition.TourLoop,
        };
        ParallaxTourPlan decoded = ParallaxTourPlan.FromJson(plan.ToJson());
        Assert.Equal(-360, decoded.Tour[1].SpinDegrees);
        Assert.Equal(0.6, decoded.Tour[1].Push);
        Assert.Equal(0.35, decoded.TourGlide);
        Assert.Equal(45, decoded.Tour[1].RotateDegrees);
        Assert.Equal("The Iris Nebula", decoded.Tour[1].Title);
        Assert.True(decoded.TourTitles); Assert.True(decoded.TourLoop);
        Assert.NotEqual(composition.Stops[1].Id, decoded.Tour[1].Id);
        ParallaxStop old = JsonSerializer.Deserialize("""{"rotateDegrees":720,"hold":2}""", ParallaxJsonContext.Default.ParallaxStop)!;
        Assert.Equal(720, old.RotateDegrees);
        Assert.Equal(0, old.SpinDegrees); Assert.Equal(0, old.Push); Assert.Null(old.Title);
        Assert.Equal(4, old.Travel); Assert.Equal(1, old.Zoom);
        ParallaxStop unnamed = JsonSerializer.Deserialize("""{"focus":[20,30]}""", ParallaxJsonContext.Default.ParallaxStop)!;
        Assert.Equal("Custom Point", unnamed.DisplayName);
    }

    [Fact]
    public void LoopClosureUsesIncomingTravelAndExistingReturnIsNotDuplicated()
    {
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, TourLoop = true };
        composition.Stops[0].Hold = 0.1;
        composition.Stops[1].Travel = 0.3;
        Assert.Equal(3.3, composition.LoopClosingDuration, precision: 8);
        ParallaxStop closing = composition.Stops[0].DeepClone();
        closing.Id = Guid.NewGuid(); closing.Travel = 0.7; closing.RotateDegrees = 720;
        composition.Stops.Add(closing);
        Assert.Equal(0, composition.LoopClosingDuration);
        composition.Motion = ParallaxMotion.FlyIn;
        composition.TourTitles = true;
        Assert.False(composition.VideoSettings(false).TourLoop);
        Assert.False(composition.VideoSettings(false).TourTitles);
    }

    [Fact]
    public void RequestNamesTheDustMapOnlyWhenOneIsChosen()
    {
        var request = new ParallaxRequest { Inputs = new() { Image = "image.png" } };
        using (var document = JsonDocument.Parse(request.ToJson()))
            Assert.False(document.RootElement.TryGetProperty("dustMap", out _));
        request.Inputs.DustMap = "dust.xisf";
        using var named = JsonDocument.Parse(request.ToJson());
        Assert.Equal("dust.xisf", named.RootElement.GetProperty("dustMap").GetString());
        Assert.Equal("{\"path\":\"dust.xisf\"}", ParallaxJson.SerializeDustMapPath("dust.xisf"));
        Assert.Equal("{}", ParallaxJson.SerializeDustMapPath(null));
        using var write = JsonDocument.Parse(ParallaxJson.SerializeDustMapWrite("dust.fits", true));
        Assert.Equal("dust.fits", write.RootElement.GetProperty("path").GetString());
        Assert.True(write.RootElement.GetProperty("counted").GetBoolean());
    }

    [Fact]
    public void RequestAsksForDepthPlanesAndGivenDistancesOnlyWhenSet()
    {
        var request = new ParallaxRequest { Inputs = new() { Image = "image.png" } };
        using (var plain = JsonDocument.Parse(request.ToJson()))
        {
            Assert.False(plain.RootElement.TryGetProperty("autoPlanes", out _));
            Assert.False(plain.RootElement.TryGetProperty("distanceOverrides", out _));
        }
        request.Scene.AutoPlanes = true;
        request.Scene.SetDistanceOverride("vdB 2", 700);
        using var asked = JsonDocument.Parse(request.ToJson());
        Assert.True(asked.RootElement.GetProperty("autoPlanes").GetBoolean());
        JsonElement given = asked.RootElement.GetProperty("distanceOverrides");
        Assert.Equal(1, given.GetArrayLength());
        Assert.Equal("vdB 2", given[0].GetProperty("object").GetString());
        Assert.Equal(700, given[0].GetProperty("distanceParsecs").GetDouble());
    }

    [Fact]
    public void AGivenDistanceReplacesOneForTheSameObjectAndResetDropsIt()
    {
        var scene = new ParallaxSceneSettings();
        scene.SetDistanceOverride("LDN 1167", 300);
        scene.SetDistanceOverride("vdB 2", 700);
        ParallaxSceneSettings before = scene.DeepClone();
        // The core finds objects by name whatever their case.
        scene.SetDistanceOverride(" ldn 1167", 250);
        Assert.Equal(["ldn 1167", "vdB 2"], scene.DistanceOverrides!.Select(given => given.ObjectName));
        Assert.Equal(250, scene.DistanceOverride("LDN 1167"));
        Assert.Equal(300, before.DistanceOverride("LDN 1167"));
        Assert.Null(scene.DistanceOverride("NGC 7822"));
        scene.RemoveDistanceOverride("LDN 1167");
        Assert.Equal(["vdB 2"], scene.DistanceOverrides!.Select(given => given.ObjectName));
        scene.RemoveDistanceOverride("VDB 2");
        Assert.Null(scene.DistanceOverrides);
    }

    [Fact]
    public void DepthPlanesAndGivenDistancesAreSceneFields()
    {
        // The session prepares the scene again when its settings differ.
        var scene = new ParallaxSceneSettings();
        ParallaxSceneSettings planes = scene.DeepClone();
        planes.AutoPlanes = true;
        Assert.False(scene.ContentEquals(planes));
        ParallaxSceneSettings given = scene.DeepClone();
        given.SetDistanceOverride("vdB 2", 700);
        Assert.False(scene.ContentEquals(given));
        ParallaxSceneSettings again = given.DeepClone();
        again.SetDistanceOverride("vdB 2", 700);
        Assert.True(given.ContentEquals(again));
        again.SetDistanceOverride("vdB 2", 650);
        Assert.False(given.ContentEquals(again));
        again.RemoveDistanceOverride("vdB 2");
        Assert.True(scene.ContentEquals(again));

        var composition = new ParallaxComposition();
        composition.Scene.SetDistanceOverride("vdB 2", 0);
        Assert.Equal("Distances must be positive, finite parsec values.", composition.ValidationMessage);
        composition.Scene.SetDistanceOverride("vdB 2", double.NaN);
        Assert.Equal("Distances must be positive, finite parsec values.", composition.ValidationMessage);
        composition.Scene.DistanceOverrides = [new() { ObjectName = " ", DistanceParsecs = 700 }];
        Assert.Equal("The composition is incomplete.", composition.ValidationMessage);
        composition.Scene.DistanceOverrides = null;
        Assert.Null(composition.ValidationMessage);
    }

    [Fact]
    public void SummaryNamesEachPlanesObjectWhenOneMovesIt()
    {
        const string json = """
            {"schemaVersion":1,"frames":12,"fps":24,"width":66,"height":48,"backgroundDistanceParsecs":900,
             "planes":[
               {"name":"vdB 2","distanceParsecs":716.7,"basis":"measured, parallax","coverage":0.002,"absorbs":false,"object":"vdB 2"},
               {"name":"dust cloud 1","distanceParsecs":300,"basis":"Gaia star counts, 70 stars in front, 250-340 pc","coverage":0.05,"absorbs":true,"object":null}]}
            """;
        ParallaxSummary summary = JsonSerializer.Deserialize(json, ParallaxJsonContext.Default.ParallaxSummary)!;
        Assert.Equal(2, summary.Planes.Length);
        Assert.Equal(new ParallaxPlane { Name = "vdB 2", DistanceParsecs = 716.7, Basis = "measured, parallax", Coverage = 0.002, ObjectName = "vdB 2" }, summary.Planes[0]);
        Assert.True(summary.Planes[1].Absorbs);
        Assert.Null(summary.Planes[1].ObjectName);
        ParallaxSummary older = JsonSerializer.Deserialize("""{"schemaVersion":1,"frames":12}""", ParallaxJsonContext.Default.ParallaxSummary)!;
        Assert.Empty(older.Planes);
    }

    [Fact]
    public void APlannedStopOnADepthPlaneTakesThePlaneUnderItsPoint()
    {
        // The planner gives a stop on a plane its depth. The app leaves it out,
        // so the stop centres on whatever plane the scene shows at its point.
        const string json = """{"schemaVersion":1,"focus":[100,80],"focusName":"Target","seconds":7,"tour":[{"name":"Whole Image","travel":0,"hold":1},{"name":"Sh2-170","focus":[90,70],"dolly":0.88,"zoom":3.2,"pan":0.12,"travel":5,"hold":2,"title":"Sh2-170","depthParsecs":2790}]}""";
        ParallaxTourPlan plan = ParallaxTourPlan.FromJson(json);
        Assert.Equal(0.12, plan.Tour[1].Pan);
        using var saved = JsonDocument.Parse(plan.ToJson());
        Assert.False(saved.RootElement.GetProperty("tour")[1].TryGetProperty("depthParsecs", out _));
    }

    [Fact]
    public void CompleteReconfigurationNeverContainsSceneFields()
    {
        var composition = new ParallaxComposition();
        using var document = JsonDocument.Parse(composition.VideoSettings(false).ToJson());
        foreach (string key in new[] { "start", "dolly", "truck", "truckAngleDegrees", "pan", "zoom", "zoomEnd", "rotateDegrees", "easing", "quality", "growthLimit", "fadeFrom", "tour", "tourGlide", "tourTitles", "tourLoop", "seconds", "fps", "size", "overlay", "overlayDensity", "labels", "labelColor", "watermark" })
            Assert.True(document.RootElement.TryGetProperty(key, out _), key);
        foreach (string key in new[] { "image", "wcs", "distanceFocus", "distanceParsecs", "smallStars", "online", "catalogDirectory", "autoTour", "dustMap", "autoPlanes", "distanceOverrides" })
            Assert.False(document.RootElement.TryGetProperty(key, out _), key);
        Assert.False(document.RootElement.GetProperty("watermark").GetBoolean());
        composition.CreditEnabled = true;
        using var enabled = JsonDocument.Parse(composition.VideoSettings(false).ToJson());
        Assert.Equal(composition.Credit, enabled.RootElement.GetProperty("watermark").GetString());
    }

    [Fact]
    public void TheWatermarkStaysPlainUntilALogoOrPlacementIsSet()
    {
        var composition = new ParallaxComposition { CreditEnabled = true };
        JsonElement Watermark()
        {
            using var document = JsonDocument.Parse(composition.VideoSettings(false).ToJson());
            return document.RootElement.GetProperty("watermark").Clone();
        }
        Assert.Equal(composition.Credit, Watermark().GetString());
        // A logo, or a corner or opacity of the user's, sends the object form.
        composition.WatermarkImage = @"C:\Logos\seiza.png";
        composition.WatermarkCorner = "topLeft";
        JsonElement styled = Watermark();
        Assert.Equal(composition.Credit, styled.GetProperty("text").GetString());
        Assert.Equal(@"C:\Logos\seiza.png", styled.GetProperty("image").GetString());
        Assert.Equal("topLeft", styled.GetProperty("corner").GetString());
        Assert.Equal(0.06, styled.GetProperty("size").GetDouble());
        Assert.Equal(0.8, styled.GetProperty("opacity").GetDouble());
        // A logo without credit text sends no text.
        composition.CreditEnabled = false;
        Assert.False(Watermark().TryGetProperty("text", out _));
        composition.WatermarkImage = null;
        composition.WatermarkCorner = "bottomRight";
        Assert.Equal(JsonValueKind.False, Watermark().ValueKind);
        composition.WatermarkOpacity = 0.5;
        Assert.Equal(0.5, Watermark().GetProperty("opacity").GetDouble());
        // The object form reads back as written.
        ParallaxVideoSettings read = JsonSerializer.Deserialize(
            """{"watermark":{"text":true,"image":"logo.png","corner":"bottomLeft","size":0.1,"opacity":1}}""",
            ParallaxJsonContext.Default.ParallaxVideoSettings)!;
        Assert.Equal(new ParallaxWatermark("Rendered with seiza.fyi") { Image = "logo.png", Corner = "bottomLeft", Size = 0.1, Opacity = 1 },
            read.Watermark);
    }

    [Fact]
    public void TitleCardsAndAStopsCardGoToTheCoreAndSavedTours()
    {
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour };
        using (var plain = JsonDocument.Parse(composition.VideoSettings(false).ToJson()))
        {
            Assert.False(plain.RootElement.TryGetProperty("introCard", out _));
            Assert.False(plain.RootElement.TryGetProperty("outroCard", out _));
            Assert.Equal(3, plain.RootElement.GetProperty("cardSeconds").GetDouble());
            Assert.False(plain.RootElement.GetProperty("tour")[1].TryGetProperty("card", out _));
        }
        composition.IntroCard = "opening.png";
        composition.OutroCard = "closing.png";
        composition.CardSeconds = 4;
        composition.Stops[1].Card = "stop.png";
        using (var carded = JsonDocument.Parse(composition.VideoSettings(true).ToJson()))
        {
            Assert.Equal("opening.png", carded.RootElement.GetProperty("introCard").GetString());
            Assert.Equal("closing.png", carded.RootElement.GetProperty("outroCard").GetString());
            Assert.Equal(4, carded.RootElement.GetProperty("cardSeconds").GetDouble());
            Assert.Equal("stop.png", carded.RootElement.GetProperty("tour")[1].GetProperty("card").GetString());
        }
        var plan = new ParallaxTourPlan { Focus = [20, 30], FocusName = "Scene", Seconds = composition.Duration, Tour = composition.Stops };
        Assert.Equal("stop.png", ParallaxTourPlan.FromJson(plan.ToJson()).Tour[1].Card);
        // A copy of the draft keeps the card apart from the original.
        ParallaxComposition copy = composition.DeepClone();
        copy.Stops[1].Card = null;
        Assert.Equal("stop.png", composition.Stops[1].Card);
    }

    [Theory]
    [InlineData(0.0, 0.8, 3.0, "bottomRight")]
    [InlineData(1.5, 0.8, 3.0, "bottomRight")]
    [InlineData(0.06, -0.1, 3.0, "bottomRight")]
    [InlineData(0.06, 0.8, 0.0, "bottomRight")]
    [InlineData(0.06, 0.8, 3.0, "middle")]
    [InlineData(double.NaN, 0.8, 3.0, "bottomRight")]
    public void ValidationRejectsBadWatermarkAndCardSettings(double size, double opacity, double seconds, string corner)
    {
        var composition = new ParallaxComposition();
        Assert.Null(composition.ValidationMessage);
        composition.WatermarkSize = size; composition.WatermarkOpacity = opacity;
        composition.CardSeconds = seconds; composition.WatermarkCorner = corner;
        Assert.Contains("watermark", composition.ValidationMessage);
    }

    [Fact]
    public void TheFitNamesAStopFlownLessFar()
    {
        // The core always sends the move's own values; the reader leaves a
        // missing one null.
        const string json = """{"schemaVersion":1,"frames":12,"fit":{"zoom":{"asked":1,"used":1},"pan":{"asked":0,"used":0},"lead":{"asked":1,"used":1},"truck":{"asked":0,"used":0},"inside":true,"stops":[{"zoom":{"asked":1,"used":1},"pan":{"asked":0,"used":0},"dolly":{"asked":0,"used":0}},{"zoom":{"asked":3.2,"used":3.99},"pan":{"asked":0.12,"used":0.66},"dolly":{"asked":0.88,"used":0.34}}]}}""";
        ParallaxSummary summary = JsonSerializer.Deserialize(json, ParallaxJsonContext.Default.ParallaxSummary)!;
        Assert.Equal(0.34, summary.Fit.Stops[1].Dolly!.Used);
        Assert.Contains("Stop 2 dolly: 0.88 → 0.34", summary.Fit.Adjustments);
        Assert.DoesNotContain(summary.Fit.Adjustments, line => line.StartsWith("Stop 1", StringComparison.Ordinal));
        // An older core gives no dolly.
        ParallaxSummary older = JsonSerializer.Deserialize(
            """{"schemaVersion":1,"frames":12,"fit":{"zoom":{"asked":1,"used":1},"pan":{"asked":0,"used":0},"lead":{"asked":1,"used":1},"truck":{"asked":0,"used":0},"inside":true,"stops":[{"zoom":{"asked":1,"used":1.5},"pan":{"asked":0,"used":0}}]}}""",
            ParallaxJsonContext.Default.ParallaxSummary)!;
        Assert.Null(older.Fit.Stops[0].Dolly);
        Assert.Equal("Stop 1 zoom: 1.00 → 1.50", Assert.Single(older.Fit.Adjustments));
    }

    [Theory]
    [InlineData(1920, 1080, "960x540")]
    [InlineData(1080, 1920, "540x960")]
    [InlineData(3840, 2160, "960x540")]
    [InlineData(100, 98, "100x98")]
    public void PreviewBoundsPreservePresetAspectRatio(int width, int height, string expected)
    {
        var composition = new ParallaxComposition { Width = width, Height = height, Quality = "high" };
        Assert.Equal(expected, composition.VideoSettings(true).Size);
        Assert.Equal("standard", composition.VideoSettings(true).Quality);
        Assert.Equal("high", composition.VideoSettings(false).Quality);
    }

    [Theory]
    [InlineData(16, 3840, "16x3840")]
    [InlineData(3840, 16, "3840x16")]
    [InlineData(32, 3840, "16x1920")]
    [InlineData(3840, 32, "1920x16")]
    [InlineData(18, 3840, "16x3414")]
    [InlineData(3840, 18, "3414x16")]
    [InlineData(66, 3840, "16x930")]
    [InlineData(3840, 66, "930x16")]
    [InlineData(68, 3840, "16x904")]
    [InlineData(3840, 68, "904x16")]
    [InlineData(1922, 1080, "958x538")]
    [InlineData(1080, 1922, "538x958")]
    public void CustomPreviewUsesOneScaleWithOnlyAnEvenPixelRoundingDifference(int width, int height, string expected)
    {
        var composition = new ParallaxComposition { Width = width, Height = height };
        Assert.Null(composition.ValidationMessage);
        Assert.Equal(expected, composition.VideoSettings(true).Size);
        Assert.Equal($"{width}x{height}", composition.VideoSettings(false).Size);
        int[] preview = composition.VideoSettings(true).Size.Split('x').Select(int.Parse).ToArray();
        Assert.InRange(preview[0], 16, width);
        Assert.InRange(preview[1], 16, height);
        Assert.Equal(0, preview[0] % 2);
        Assert.Equal(0, preview[1] % 2);
        Assert.InRange(preview[0] * preview[1], 16 * 16, 960 * 960);
        int shortSide = Math.Min(width, height), longSide = Math.Max(width, height);
        int previewShort = Math.Min(preview[0], preview[1]), previewLong = Math.Max(preview[0], preview[1]);
        double scaledLong = (double)longSide * previewShort / shortSide;
        Assert.InRange(Math.Abs(previewLong - scaledLong), 0, 1.000001);
        if (previewShort > 16) Assert.InRange(previewLong, 16, 960);
    }

    [Fact]
    public void HistoryDeepCloneKeepsEditableCollectionsAndArraysIndependent()
    {
        var original = new ParallaxComposition { Focus = [1, 2] };
        original.Scene.DistanceFocus = [3, 4]; original.Stops[1].Focus = [5, 6];
        original.Labels.Add(new() { Text = "Before" });
        var snapshot = original.DeepClone();
        Assert.True(original.ContentEquals(snapshot));
        Assert.True(original.Scene.ContentEquals(snapshot.Scene));
        Assert.Equal(original.Stops[1].Id, snapshot.Stops[1].Id);
        original.Focus[0] = 7; original.Scene.DistanceFocus[0] = 8; original.Stops[1].Focus![0] = 9;
        original.Labels[0].Text = "After";
        Assert.Equal(1, snapshot.Focus![0]); Assert.Equal(3, snapshot.Scene.DistanceFocus![0]);
        Assert.Equal(5, snapshot.Stops[1].Focus![0]); Assert.Equal("Before", snapshot.Labels[0].Text);
        Assert.False(original.ContentEquals(snapshot)); Assert.False(original.Scene.ContentEquals(snapshot.Scene));
    }

    [Fact]
    public void ValidationRejectsBadNativeBoundsAndNonfiniteValues()
    {
        Assert.Null(new ParallaxComposition().ValidationMessage);
        Assert.NotNull(new ParallaxComposition { Zoom = 0.5 }.ValidationMessage);
        Assert.NotNull(new ParallaxComposition { GrowthLimit = 0.5 }.ValidationMessage);
        Assert.NotNull(new ParallaxComposition { Width = 1921 }.ValidationMessage);
        Assert.NotNull(new ParallaxComposition { Seconds = double.NaN }.ValidationMessage);
        Assert.NotNull(new ParallaxComposition { Focus = [1] }.ValidationMessage);
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour };
        composition.Stops[1].Pan = double.NaN;
        Assert.NotNull(composition.ValidationMessage);
        composition.Stops[1].Pan = 0; composition.Scene.DistanceParsecs = 0;
        Assert.NotNull(composition.ValidationMessage);
    }

    [Fact]
    public void InvalidDraftNumbersRemainComparableForUndoButNeverBecomeNativeJson()
    {
        var composition = new ParallaxComposition { Dolly = double.NaN };
        composition.Scene.DistanceParsecs = double.PositiveInfinity;
        Assert.True(composition.ContentEquals(composition.DeepClone()));
        Assert.True(composition.Scene.ContentEquals(composition.Scene.DeepClone()));
        Assert.False(new ParallaxComposition().ContentEquals(composition));
        Assert.Throws<ArgumentException>(() => composition.VideoSettings(false).ToJson());
    }

    [Fact]
    public void ActivityReportsNetworkWaitsWithoutInventingAnOverallPercentage()
    {
        DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(1000);
        var activity = new ParallaxActivity("Loading images…", start);
        activity.Receive(new() { Kind = "note", Message = "fetching Gaia DR3 distances within 1.61 deg of (315.1375, +67.7775) in 4 cone(s)" }, start.AddSeconds(5));
        Assert.Equal(4, activity.Regions);
        Assert.Null(activity.WaitingMessage(start.AddSeconds(10)));
        Assert.Contains("0:20 since its last update", activity.WaitingMessage(start.AddSeconds(25)));
        activity.Receive(new() { Kind = "note", Message = "  1 cone(s) fetched" }, start.AddSeconds(30));
        Assert.Equal(1, activity.Downloaded);
        Assert.Contains("1 new sky regions downloaded", activity.Detail(start.AddSeconds(35)));
        activity.Receive(new() { Kind = "note", Message = "10574 Gaia stars" }, start.AddSeconds(40));
        Assert.Contains("Hipparcos", activity.NetworkPhase);
        activity.Receive(new() { Kind = "note", Message = "5384 matched to Gaia, 5339 with a distance" }, start.AddSeconds(50));
        Assert.Null(activity.NetworkPhase); Assert.Null(activity.WaitingMessage(start.AddSeconds(80)));
        activity.Receive(new() { Kind = "warning", Message = "A catalogue warning" }, start.AddSeconds(60));
        Assert.True(activity.Entries[^1].Warning);
        activity.CancellationRequested = true;
        Assert.Contains("Cancel requested", activity.Detail(start.AddSeconds(70)));
        activity.FinishedAt = start.AddSeconds(71);
        Assert.Null(activity.WaitingMessage(start.AddSeconds(100)));
    }

    [Fact]
    public void ActivityRetainsOnlyTheLastHundredEvents()
    {
        var activity = new ParallaxActivity("Start");
        for (int index = 0; index < 150; index++) activity.Receive(new() { Kind = "note", Message = $"Step {index}" });
        Assert.Equal(100, activity.Entries.Count);
        Assert.Equal("Step 50", activity.Entries[0].Message);
        Assert.Equal("Step 149", activity.Entries[^1].Message);
    }

    [Fact]
    public void TourPlanRejectsUnsupportedSchemaAndBrokenCoordinates()
    {
        Assert.Throws<InvalidDataException>(() => ParallaxTourPlan.FromJson("""{"schemaVersion":2,"focus":[1,2],"tour":[{},{}]}"""));
        Assert.Throws<InvalidDataException>(() => ParallaxTourPlan.FromJson("""{"focus":[1],"tour":[{},{}]}"""));
    }
}
