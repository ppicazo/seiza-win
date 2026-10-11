using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class LiveStackCoordinatorTests
{
    [Fact]
    public void SharedAlignmentReferenceIsExcludedWithoutExcludingTheChannelLight()
    {
        var configuration = new LiveStackRunConfiguration
        {
            InitialReferencePath = @"C:\captures\channel.fits",
            AlignmentReferencePath = @"C:\captures\shared.fits",
        };
        string[] excluded = configuration.MonitorExcludedPaths();
        Assert.Equal(Path.GetFullPath(configuration.AlignmentReferencePath), Assert.Single(excluded));
        Assert.DoesNotContain(Path.GetFullPath(configuration.InitialReferencePath), excluded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SharedAlignmentReferenceCannotBeAMasterOrOutput(bool master)
    {
        string path = @"C:\captures\shared.fits";
        var configuration = new LiveStackRunConfiguration
        {
            WatchFolder = @"C:\captures",
            SessionRootDirectory = @"C:\sessions",
            AlignmentReferencePath = path,
            OutputPath = master ? null : path.ToUpperInvariant(),
            Calibration = new ImageStackCalibration { FlatPath = master ? path.ToUpperInvariant() : null },
        };
        Assert.Throws<ArgumentException>(configuration.Validate);
    }

    [Fact]
    public void InitialReferenceIsNotAStaticMonitorExclusion()
    {
        string root = Path.Combine(Path.GetTempPath(), $"seiza-live-config-{Guid.NewGuid():N}");
        string reference = Path.Combine(root, "first-light.fits");
        string output = Path.Combine(root, "stack.fits");
        string bias = Path.Combine(root, "master-bias.fits");
        string dark = Path.Combine(root, "master-dark.fits");
        string flat = Path.Combine(root, "master-flat.fits");
        var configuration = new LiveStackRunConfiguration
        {
            InitialReferencePath = reference,
            OutputPath = output,
            Calibration = new ImageStackCalibration
            {
                BiasPath = bias,
                DarkPath = dark,
                FlatPath = flat,
            },
        };

        string[] excluded = configuration.MonitorExcludedPaths();

        Assert.DoesNotContain(Path.GetFullPath(reference), excluded, StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(
            new[] { output, bias, dark, flat }.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);
        Assert.True(expected.SetEquals(excluded));
    }
}
