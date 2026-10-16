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
        Assert.Equal(before with { Dolly = 0, Zoom = 1, Pan = 0, Push = 0, Focus = stop.Focus }, stop);
        Assert.Equal(before.Focus, stop.Focus);
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
