using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxDepthSettingsTests
{
    [Fact]
    public void CustomPlanesAreSceneSettingsAndExplicitStopDepthReachesBothVideoRequests()
    {
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour };
        composition.Scene.Planes = [new() { Mask = @"C:\Masks\painted cloud α.png", DistanceParsecs = 150, Name = "Painted cloud" }];
        composition.Stops[1].DepthParsecs = 1200;
        foreach (bool preview in new[] { false, true })
        {
            var request = new ParallaxRequest { Inputs = new() { Image = "source.png" }, Scene = composition.Scene, Video = composition.VideoSettings(preview) };
            using var document = JsonDocument.Parse(request.ToJson());
            JsonElement plane = document.RootElement.GetProperty("planes")[0];
            Assert.Equal(composition.Scene.Planes[0].Mask, plane.GetProperty("mask").GetString());
            Assert.Equal(150, plane.GetProperty("distanceParsecs").GetDouble());
            Assert.Equal("Painted cloud", plane.GetProperty("name").GetString());
            Assert.False(plane.TryGetProperty("id", out _));
            Assert.False(plane.TryGetProperty("displayName", out _));
            Assert.Equal(1200, document.RootElement.GetProperty("tour")[1].GetProperty("depthParsecs").GetDouble());
            using var videoOnly = JsonDocument.Parse(request.Video.ToJson());
            Assert.False(videoOnly.RootElement.TryGetProperty("planes", out _));
        }
        composition.Scene.Planes = null;
        composition.Stops[1].DepthParsecs = null;
        using var defaults = JsonDocument.Parse(new ParallaxRequest { Scene = composition.Scene, Video = composition.VideoSettings(false) }.ToJson());
        Assert.False(defaults.RootElement.TryGetProperty("planes", out _));
        Assert.False(defaults.RootElement.GetProperty("tour")[1].TryGetProperty("depthParsecs", out _));
    }

    [Fact]
    public void CustomPlaneCloningAndEqualityKeepUndoSnapshotsIndependent()
    {
        var original = new ParallaxComposition { Motion = ParallaxMotion.Tour };
        original.Scene.Planes = [new() { Mask = "mask.png", DistanceParsecs = 150, Name = "Cloud" }];
        original.Stops[1].DepthParsecs = 1200;
        ParallaxComposition copy = original.DeepClone();
        Assert.True(original.ContentEquals(copy));
        Assert.NotSame(original.Scene.Planes, copy.Scene.Planes);
        Assert.NotSame(original.Scene.Planes[0], copy.Scene.Planes![0]);
        Assert.Equal(original.Scene.Planes[0].Id, copy.Scene.Planes[0].Id);
        copy.Scene.Planes[0].Id = Guid.NewGuid();
        Assert.True(original.ContentEquals(copy));
        copy.Scene.Planes[0].Mask = "other.png";
        copy.Scene.Planes[0].DistanceParsecs = 250;
        copy.Stops[1].DepthParsecs = null;
        Assert.False(original.ContentEquals(copy));
        Assert.False(original.Scene.ContentEquals(copy.Scene));
        Assert.Equal("mask.png", original.Scene.Planes[0].Mask);
        Assert.Equal(150, original.Scene.Planes[0].DistanceParsecs);
        Assert.Equal(1200, original.Stops[1].DepthParsecs);
    }

    [Fact]
    public void ExplicitStopDepthSurvivesFileItinerariesClonesAndClearing()
    {
        const string json = """{"schemaVersion":1,"focus":[48,32],"seconds":6,"tour":[{"travel":0,"hold":1},{"focus":[35,27],"travel":4,"hold":1,"depthParsecs":2790}]}""";
        string itinerary = Path.Combine(Path.GetTempPath(), "SeizaDepthTour", "tour.json");
        ParallaxTourPlan plan = ParallaxTourPlan.FromJson(json, itinerary);
        ParallaxTourPlan saved = ParallaxTourPlan.FromJson(plan.ToJson(itinerary), itinerary);
        Assert.Equal(2790, saved.Tour[1].DepthParsecs);
        ParallaxStop copy = saved.Tour[1].DeepClone();
        copy.DepthParsecs = null;
        Assert.Equal(2790, saved.Tour[1].DepthParsecs);
        using var clear = JsonDocument.Parse((saved with { Tour = [saved.Tour[0], copy] }).ToJson());
        Assert.False(clear.RootElement.GetProperty("tour")[1].TryGetProperty("depthParsecs", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void StopDepthMustBePositiveAndFinite(double depth)
    {
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour };
        composition.Stops[1].DepthParsecs = depth;
        Assert.NotNull(composition.ValidationMessage);
        // Invalid numerical drafts must remain structurally comparable for undo.
        Assert.True(composition.ContentEquals(composition.DeepClone()));
        composition.Stops[1].DepthParsecs = null;
        Assert.Null(composition.ValidationMessage);
        composition.Stops[1].DepthParsecs = 0.01;
        Assert.Null(composition.ValidationMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void CustomPlaneDepthMustBePositiveAndFinite(double depth)
    {
        var composition = new ParallaxComposition();
        composition.Scene.Planes = [new() { Mask = "mask.png", DistanceParsecs = depth }];
        Assert.Equal("Distances must be positive, finite parsec values.", composition.ValidationMessage);
        Assert.True(composition.ContentEquals(composition.DeepClone()));
        composition.Scene.Planes[0].DistanceParsecs = 150;
        Assert.Null(composition.ValidationMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ACustomPlaneNeedsAMaskPath(string mask)
    {
        var composition = new ParallaxComposition();
        composition.Scene.Planes = [new() { Mask = mask, DistanceParsecs = 150 }];
        Assert.Equal("The composition is incomplete.", composition.ValidationMessage);
    }

    [Fact]
    public void ArbitraryObjectDistancesTrimAndReplaceDuplicateNamesWithoutTouchingSnapshots()
    {
        var scene = new ParallaxSceneSettings
        {
            DistanceOverrides = [new() { ObjectName = "Custom target", DistanceParsecs = 100 },
                new() { ObjectName = " Other target ", DistanceParsecs = 300 },
                new() { ObjectName = " CUSTOM TARGET ", DistanceParsecs = 200 }],
        };
        var composition = new ParallaxComposition { Scene = scene };
        Assert.Equal("Each named object can have only one distance override.", composition.ValidationMessage);
        ParallaxSceneSettings before = scene.DeepClone();
        scene.SetDistanceOverride(" custom target ", 500);
        Assert.Equal(2, scene.DistanceOverrides!.Count);
        Assert.Equal("custom target", scene.DistanceOverrides[0].ObjectName);
        Assert.Equal(500, scene.DistanceOverride("CUSTOM TARGET"));
        Assert.Equal(100, before.DistanceOverride("custom target"));
        Assert.Equal(3, before.DistanceOverrides!.Count);
        Assert.Null(composition.ValidationMessage);
        scene.RemoveDistanceOverride(" CUSTOM TARGET ");
        Assert.Single(scene.DistanceOverrides);
    }

    [Fact]
    public void MaskInputProtectionCapturesEveryPlaneAndSurvivesDraftMutation()
    {
        string mask = Path.Combine(Path.GetTempPath(), "mask.png");
        string second = Path.Combine(Path.GetTempPath(), "mask-two.tif");
        var composition = new ParallaxComposition
        {
            Scene = new() { Planes = [new() { Mask = mask, DistanceParsecs = 150 }, new() { Mask = second, DistanceParsecs = 600 }] },
        };
        string[] captured = ParallaxInputPaths.Capture(ParallaxInputPaths.FromComposition(composition));
        composition.Scene.Planes![0].Mask = "changed.png";
        Assert.Contains(mask, captured);
        Assert.Contains(second, captured);
        Assert.DoesNotContain(Path.GetFullPath("changed.png"), captured);
        Assert.Equal(["changed.png", second], ParallaxInputPaths.FromScene(composition.Scene));
    }
}
