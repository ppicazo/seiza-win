using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxFramingTests
{
    [Fact]
    public void ShippedCoreSupportsFramingAndWholeImagePullBack()
    {
        Assert.True(ParallaxCore.SupportsFraming);
        Assert.True(ParallaxCore.SupportsPullBackToWholeImage);
    }

    [Fact]
    public void PullBackCenteringUsesCombinedOverallAndStopMagnification()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        fixture.Request.Video.Framing = "fit";
        fixture.Request.Video.Zoom = 2;
        fixture.Request.Video.Tour = [new() { Travel = 0, Hold = 0.1 },
            new() { Focus = [80, 48], Travel = 0.3, Hold = 0.1, PullBackToWholeImage = true }];
        using var overall = ParallaxCore.Prepare(fixture.Request);
        var settings = fixture.Request.Video with { Zoom = 1,
            Tour = fixture.Request.Video.Tour.Select(s => s.DeepClone() with { Zoom = 2 }).ToList() };
        using var perStop = ParallaxCore.Reconfigure(overall, settings);
        foreach (int frame in new[] { 0, overall.FrameCount / 2, overall.FrameCount - 1 })
            Assert.Equal(overall.RenderBgraFrame(frame), perStop.RenderBgraFrame(frame));
    }

    [Theory]
    [InlineData(180, 0)]
    [InlineData(360, 0.2)]
    public void TwoStopTurnsReportTransitionZoomWithoutChangingEndpointLenses(double turn, double glide)
    {
        if (!ParallaxCore.SupportsPullBackToWholeImage) return;
        using var fixture = new ParallaxCoreTests.Fixture();
        fixture.Request.Video.Framing = "fill";
        fixture.Request.Video.TourGlide = glide;
        fixture.Request.Video.Tour = [new() { Travel = 0, Hold = 0.2 },
            new() { Travel = 0.5, Hold = 0.2, RotateDegrees = turn }];
        using var video = ParallaxCore.Prepare(fixture.Request);
        Assert.True(video.Summary.Fit.Inside);
        Assert.Equal(1, video.Summary.Fit.Zoom.Used);
        Assert.All(video.Summary.Fit.Stops, stop => Assert.Equal(1, stop.Zoom.Used));
        Assert.True(video.Summary.Fit.MotionZoom!.Used > 1);
        Assert.Contains(video.Summary.Fit.Adjustments, text => text.StartsWith("Transition zoom:", StringComparison.Ordinal));
        using var refit = ParallaxCore.Reconfigure(video, fixture.Request.Video);
        Assert.Equal(video.RenderBgraFrame(0), refit.RenderBgraFrame(0));
        Assert.Equal(video.RenderBgraFrame(video.FrameCount - 1), refit.RenderBgraFrame(refit.FrameCount - 1));
    }

    [Fact]
    public void PublishedCoreUsesFullSourceDustCoordinatesForNonOriginCrop()
    {
        if (!ParallaxCore.SupportsPullBackToWholeImage) return;
        using var fixture = new ParallaxCoreTests.Fixture();
        fixture.Request.Scene.Crop = [16, 16, 32, 32];
        fixture.Request.Video.Tour = [];
        fixture.Request.Inputs.DustMap = fixture.Request.Inputs.Starless;
        using var video = ParallaxCore.Prepare(fixture.Request);
        using var replaced = ParallaxCore.WithDustMap(video, fixture.Request.Inputs.DustMap);
        float[] opacity = replaced.DustMap(96, 64)!;
        Assert.Equal(96 * 64, opacity.Length);
        Assert.Equal(0, opacity[0]);
        Assert.True(opacity[24 * 96 + 24] > 0);
        Assert.Equal(0, opacity[63 * 96 + 95]);
        // The core resamples maps; painting/readback need not preserve a
        // gradient bit-for-bit. Check an asymmetric mask away from its seam.
        float[] mask = Enumerable.Range(0, 96 * 64).Select(i => i % 96 >= 32 ? 0.75f : 0f).ToArray();
        using var painted = ParallaxCore.WithDustMap(replaced, mask, 96, 64);
        float[] restored = painted.DustMap(96, 64)!;
        Assert.Equal(0, restored[24 * 96 + 24]);
        Assert.InRange(restored[24 * 96 + 40], 0.7499f, 0.7501f);
        Assert.Equal(0, restored[0]);
        Assert.Equal(0, restored[63 * 96 + 95]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.2)]
    public void WholeImagePullBackPreservesOpeningTargetAndGlobalLensWhenSupported(double glide)
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        fixture.Request.Video.Framing = "fill"; fixture.Request.Video.TourGlide = glide;
        fixture.Request.Video.Tour[1].RotateDegrees = 0; fixture.Request.Video.Tour[1].Pan = 0;
        fixture.Request.Video.Tour[1].Dolly = 0.8; fixture.Request.Video.Tour[1].Zoom = 3;
        var settings = fixture.Request.Video with { Tour = fixture.Request.Video.Tour.Select(s => s.DeepClone()).ToList() };
        var target = settings.Tour[1].Focus!.ToArray(); settings.Tour[1].PullBack();
        if (!ParallaxCore.SupportsPullBackToWholeImage)
        {
            var error = Assert.Throws<NotSupportedException>(() => ParallaxCore.Prepare(fixture.Request with { Video = settings }));
            Assert.Contains("#283", error.Message);
            return;
        }
        using var original = ParallaxCore.Prepare(fixture.Request);
        using var pulled = ParallaxCore.Reconfigure(original, settings);
        Assert.Equal(original.RenderBgraFrame(0), pulled.RenderBgraFrame(0));
        Assert.Equal(original.Summary.Fit.Zoom.Used, pulled.Summary.Fit.Zoom.Used);
        Assert.Equal(target, settings.Tour[1].Focus);
        Assert.Equal(pulled.RenderBgraFrame(0), pulled.RenderBgraFrame(pulled.FrameCount - 1));
        Assert.True(pulled.Summary.Fit.Inside);
    }
    [Fact]
    public void WideCustomPointRefitsAreStableOnFramingCapableCores()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        fixture.Request.Video.RotateDegrees = [0, 0]; fixture.Request.Video.Framing = "fit";
        fixture.Request.Video.Tour[1].RotateDegrees = 0; fixture.Request.Video.Tour[1].Pan = 0;
        fixture.Request.Video.Tour[1].Dolly = 0.8; fixture.Request.Video.Tour[1].Zoom = 3;
        if (!ParallaxCore.SupportsFraming)
        {
            Assert.Throws<NotSupportedException>(() => ParallaxCore.Prepare(fixture.Request));
            return;
        }
        using var scene = ParallaxCore.Prepare(fixture.Request);
        var settings = fixture.Request.Video with { Tour = fixture.Request.Video.Tour.Select(s => s.DeepClone()).ToList() };
        settings.Tour[1].Dolly = 0; settings.Tour[1].Zoom = 0.5;
        using var wide = ParallaxCore.Reconfigure(scene, settings);
        byte[] expected = wide.RenderBgraFrame(wide.FrameCount - 1);
        Assert.False(expected.SequenceEqual(scene.RenderBgraFrame(scene.FrameCount - 1)));
        for (int i = 0; i < 3; i++)
        {
            using var refit = ParallaxCore.Reconfigure(wide, settings);
            Assert.Equal(expected, refit.RenderBgraFrame(refit.FrameCount - 1));
        }
    }
    [Theory]
    [InlineData(9213, 6943, 3840, 3840, 2894)]
    [InlineData(4123, 4123, 1920, 1920, 1920)]
    [InlineData(4152, 6237, 1920, 1278, 1920)]
    [InlineData(2400, 1350, 1920, 1920, 1080)]
    [InlineData(1, 1000000, 3840, 16, 3840)]
    public void ImageShapeUsesEvenDimensionsAndTheActualLongSide(int w, int h, int longSide, int expectedW, int expectedH)
        => Assert.Equal((expectedW, expectedH), ParallaxGeometry.ImageSize(w, h, longSide));

    [Fact]
    public void CropControlsExportShapeButPreviewNeverRequestsFullResolutionImagePreset()
    {
        var composition = new ParallaxComposition { SourceWidth = 4123, SourceHeight = 4123, MatchImageShape = true, Framing = "fit" };
        Assert.Equal("1920x1920", composition.VideoSettings(false).Size);
        Assert.Equal("960x960", composition.VideoSettings(true).Size);
        composition.Scene.Crop = [1431, 1165, 2400, 1350];
        Assert.Equal("1920x1080", composition.VideoSettings(false).Size);
        Assert.Equal("960x540", composition.VideoSettings(true).Size);
        Assert.Equal("fit", composition.VideoSettings(true).Framing);
        composition.Framing = "fill";
        Assert.Equal("fill", composition.VideoSettings(true).Framing);
    }

    [Fact]
    public void AllNativePointsAreTranslatedExactlyOnceAndTheWcsStaysUncropped()
    {
        var composition = new ParallaxComposition { SourceWidth = 4123, SourceHeight = 4123, Motion = ParallaxMotion.Tour, Focus = [2500, 1800] };
        composition.Scene.Crop = [1431, 1165, 2400, 1350];
        composition.Scene.DistanceFocus = [2000, 1500];
        composition.Stops[1].Focus = [2000, 1600];
        composition.Labels.Add(new() { X = 2100, Y = 1400 });
        var wcs = new WcsResult { Crpix = [2061, 2061] };
        var request = ParallaxRequestSource.Create(composition, true, "source.png", false, "starless.tif", "stars.tif", new() { Wcs = wcs, DustMap = "full-source-dust.png" });
        Assert.Equal([569.0, 335.0], request.Scene.DistanceFocus!);
        Assert.Equal([1069.0, 635.0], request.Video.Focus!);
        Assert.Equal([569.0, 435.0], request.Video.Tour[1].Focus!);
        Assert.Equal(669, request.Video.Labels[0].X); Assert.Equal(235, request.Video.Labels[0].Y);
        Assert.Same(wcs, request.Inputs.Wcs); Assert.Equal([2061.0, 2061.0], wcs.Crpix);
        Assert.Equal("full-source-dust.png", request.Inputs.DustMap);
        Assert.Equal([2000.0, 1500.0], composition.Scene.DistanceFocus);
        Assert.Equal([2500.0, 1800.0], composition.Focus);
        using var videoJson = JsonDocument.Parse(request.Video.ToJson());
        Assert.False(videoJson.RootElement.TryGetProperty("crop", out _));
        using var prepareJson = JsonDocument.Parse(request.ToJson());
        Assert.Equal(1431, prepareJson.RootElement.GetProperty("crop")[0].GetInt32());
    }

    [Theory]
    [InlineData(-1, 0, 100, 100)]
    [InlineData(0, 0, 15, 100)]
    [InlineData(0, 0, 100, 15)]
    [InlineData(950, 0, 100, 100)]
    [InlineData(int.MaxValue, 0, int.MaxValue, 100)]
    public void InvalidOrOutOfBoundsCropCannotPrepare(int x, int y, int w, int h)
    {
        var composition = new ParallaxComposition { SourceWidth = 1000, SourceHeight = 1000 };
        composition.Scene.Crop = [x, y, w, h];
        Assert.NotNull(composition.ValidationMessage);
        Assert.False(ParallaxGeometry.CropInside(composition.Scene.Crop, 1000, 1000));
    }

    [Fact]
    public void CropDeepCloneAndSceneInvalidationAreIndependentOfCameraFraming()
    {
        var composition = new ParallaxComposition { SourceWidth = 1000, SourceHeight = 1000 };
        composition.Scene.Crop = [10, 20, 100, 200];
        var changed = composition.DeepClone();
        changed.Framing = "fit";
        Assert.True(changed.Scene.ContentEquals(composition.Scene));
        Assert.False(changed.ContentEquals(composition));
        changed.Scene.Crop![0] = 30;
        Assert.False(changed.Scene.ContentEquals(composition.Scene));
        Assert.Equal(10, composition.Scene.Crop[0]);
    }

    [Fact]
    public void CroppedTourFileRoundTripPreservesTargetsDepthsAndItsCropContext()
    {
        var source = new ParallaxTourPlan
        {
            Focus = [1500, 1300], FocusName = "Target", Tour =
            [new() { Focus = null, Travel = 0 }, new() { Focus = [2000, 1500], DepthParsecs = 800, Card = "card.png" }],
        };
        int[] crop = [1431, 1165, 2400, 1350];
        var portable = source.InCropCoordinates(crop);
        Assert.Equal([69.0, 135.0], portable.Focus);
        var restored = ParallaxTourPlan.FromJson(portable.ToJson()).InSourceCoordinates(crop);
        Assert.Equal(source.Focus, restored.Focus);
        Assert.Equal(source.Tour[1].Focus, restored.Tour[1].Focus);
        Assert.Equal(800, restored.Tour[1].DepthParsecs); Assert.Equal("card.png", restored.Tour[1].Card);
        Assert.Equal(crop, restored.Crop); Assert.True(restored.RestoreCrop);
        restored.PreserveStopDepths = true;
        var composition = new ParallaxComposition(); composition.ApplyTour(restored);
        Assert.Equal(crop, composition.Scene.Crop);
        Assert.Equal([569.0, 335.0], composition.VideoSettings(false).Tour[1].Focus!);
        Assert.Equal([2000.0, 1500.0], source.Tour[1].Focus!);
    }

    [Fact]
    public void DrawCropClampsToImageAndLocksInsideTheDraggedRectangle()
    {
        Assert.Equal([0, 0, 1000, 800], ParallaxGeometry.CropFromDrag(-20, -30, 2000, 3000, 1000, 800));
        Assert.Equal([100, 100, 800, 450], ParallaxGeometry.CropFromDrag(100, 100, 900, 700, 1000, 800, 16.0 / 9));
        Assert.Equal([100, 250, 800, 450], ParallaxGeometry.CropFromDrag(900, 700, 100, 100, 1000, 800, 16.0 / 9));
        Assert.False(ParallaxGeometry.ValidCrop(ParallaxGeometry.CropFromDrag(10, 10, 11, 11, 1000, 800)));
    }

    [Fact]
    public void ScenePlanningCarriesFramingAndDoesNotCarryCropAgain()
    {
        using var json = JsonDocument.Parse(ParallaxJson.SerializeSceneTourSettings("1920x1080", new(), "fit"));
        Assert.Equal("fit", json.RootElement.GetProperty("framing").GetString());
        Assert.False(json.RootElement.TryGetProperty("crop", out _));
    }
}
