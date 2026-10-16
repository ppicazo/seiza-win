using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxTourControlTests
{
    [Fact]
    public void PullBackOnlyChangesDollyLensPanAndPush()
    {
        var stop = new ParallaxStop { Name = "Target", Focus = [123,456], DepthParsecs = 300,
            Dolly = 0.99, Zoom = 0.5, Pan = 0.3, Push = 0.4, Hold = 3, Travel = 5,
            RotateDegrees = 15, SpinDegrees = 360, Title = "A title", Card = "card.png" };
        var before = stop.DeepClone(); stop.PullBack();
        Assert.Equal(before with { Dolly = 0, Zoom = 1, Pan = 0, Push = 0, Focus = stop.Focus, PullBackToWholeImage = true }, stop);
        Assert.Equal(before.Focus, stop.Focus);
    }
    [Fact]
    public void CameraEditsOptInButOldItinerariesKeepAuthoredCentering()
    {
        var plan = new ParallaxTourPlan { Focus = [100,200], Tour = [new() { Travel = 0 }, new() { Focus = [123,456] }] };
        Assert.DoesNotContain("pullBackToWholeImage", plan.ToJson());
        plan.Tour[1].EditCamera(s => s.Zoom = 0.5);
        var saved = plan.InCropCoordinates([10,20,1000,1000]);
        var restored = ParallaxTourPlan.FromJson(saved.ToJson()).InSourceCoordinates(saved.Crop);
        Assert.False(restored.Tour[0].PullBackToWholeImage);
        Assert.True(restored.Tour[1].PullBackToWholeImage);
        Assert.Equal(plan.Tour[1].Focus, restored.Tour[1].Focus);
        Assert.Equal(0.5, restored.Tour[1].Zoom);
    }
    [Fact]
    public void LoopClosureAccountsForDifferentPullBackModes()
    {
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, TourLoop = true };
        composition.Stops[1].Dolly = 0;
        Assert.Equal(0, composition.LoopClosingDuration);
        composition.Stops[1].PullBackToWholeImage = true;
        Assert.True(composition.LoopClosingDuration > 0);
    }
    [Fact]
    public void HalfZoomAndNinetyNinePercentDollyArePerStopNotOverallLimits()
    {
        var composition = new ParallaxComposition { Motion = ParallaxMotion.Tour, Framing = "fit" };
        composition.Stops[1].Dolly = 0.99; composition.Stops[1].Zoom = 0.5;
        Assert.Null(composition.ValidationMessage);
        Assert.Equal(0.5, composition.VideoSettings(false).Tour[1].Zoom);
        Assert.Equal(0.99, composition.VideoSettings(false).Tour[1].Dolly);
        composition.Zoom = 0.5;
        Assert.NotNull(composition.ValidationMessage);
    }
}
