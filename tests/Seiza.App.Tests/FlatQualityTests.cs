using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class FlatQualityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Seiza-flat-review-" + Guid.NewGuid().ToString("N"));
    public FlatQualityTests() => Directory.CreateDirectory(_root);
    internal static string WriteFlat(string directory, string name, float level = 30000, int side = 256, bool disk = false)
    {
        string path = Path.Combine(directory, name + ".fits");
        int bytes = side * side * 4;
        byte[] fits = new byte[2880 + ((bytes + 2879) / 2880) * 2880];
        Array.Fill(fits, (byte)' ', 0, 2880);
        string[] cards = ["SIMPLE  =                    T", "BITPIX  =                  -32", "NAXIS   =                    2",
            $"NAXIS1  = {side,20}", $"NAXIS2  = {side,20}", "IMAGETYP= 'Flat Frame'", "EXPTIME =                    2", "END"];
        for (int i = 0; i < cards.Length; i++) Encoding.ASCII.GetBytes(cards[i].PadRight(80)).CopyTo(fits, i * 80);
        var random = new Random(817);
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                double radius = Math.Sqrt(Math.Pow(x - side * 0.5, 2) + Math.Pow(y - side * 0.5, 2));
                float value = level >= 65535 ? level : level + (float)(random.NextDouble() - 0.5) * 40;
                if (disk && radius < 35) value *= 0.8f;
                BinaryPrimitives.WriteSingleBigEndian(fits.AsSpan(2880 + (y * side + x) * 4, 4), value);
            }
        File.WriteAllBytes(path, fits);
        return path;
    }

    [Fact]
    public async Task NativeReviewFlagsClippingAndKeepsOrderedSourcesAndTypedLimits()
    {
        string[] paths = [WriteFlat(_root, "good"), WriteFlat(_root, "dim", 2500), WriteFlat(_root, "clipped", 65535)];
        byte[][] originals = paths.Select(File.ReadAllBytes).ToArray();
        var review = await FlatQualityService.ReviewAsync(paths, new() { Enabled = true }, null, default);
        Assert.Equal(paths, review.Paths);
        Assert.Equal("ok", review.Report.Frames[0].Grade);
        Assert.Equal("poor", review.Report.Frames[2].Grade);
        Assert.Contains(review.Report.Frames[2].Findings, f => f.Code == "clipped" && f.Value > f.Limit);
        Assert.Contains("poor", review.Summary);
        Assert.Contains("illumination", review.CompleteReport());
        Assert.Contains("limit", review.CompleteReport());
        for (int i = 0; i < paths.Length; i++) Assert.Equal(originals[i], File.ReadAllBytes(paths[i]));
    }

    [Fact]
    public async Task DustGeometrySurvivesMeasurementSerializationAndScreening()
    {
        string path = WriteFlat(_root, "disk", side: 512, disk: true);
        var review = await FlatQualityService.ReviewAsync([path], new() { Enabled = true, UseOptics = true, FocalRatio = 5, PixelSizeUm = 3.76 }, null, default);
        var measured = Assert.Single(review.Measurements);
        Assert.True(measured.AlgorithmVersion > 0);
        Assert.Contains(measured.Features, f => f.Shape == "disk" && f.Radius > 0 && f.DistanceMm > 0);
        string json = JsonSerializer.Serialize(measured, FlatQualityJsonContext.Default.FlatQualityMeasurement);
        var roundtrip = JsonSerializer.Deserialize(json, FlatQualityJsonContext.Default.FlatQualityMeasurement)!;
        Assert.Equal(measured.Features, roundtrip.Features);
        Assert.Equal(measured.Illumination.Response, roundtrip.Illumination.Response);
        var report = FlatQualityService.ScreenFrames([roundtrip]);
        Assert.Equal(review.Report.Frames[0].Grade, report.Frames[0].Grade);
        Assert.Contains("distanceMm", review.CompleteReport());
    }

    [Fact]
    public async Task MissingOrIncompatibleFrameNeverReturnsAPartialPassingReview()
    {
        string first = WriteFlat(_root, "first");
        await Assert.ThrowsAsync<FileNotFoundException>(() => FlatQualityService.ReviewAsync(
            [first, Path.Combine(_root, "missing.fits")], new() { Enabled = true }, null, default));
        // The identity read can detect a missing source before the native call.
    }

    [Fact]
    public async Task MixedDimensionsAreRefusedByNativeScreening()
    {
        string first = WriteFlat(_root, "first");
        string second = WriteFlat(_root, "second", side: 128);
        await Assert.ThrowsAsync<SeizaCoreException>(() => FlatQualityService.ReviewAsync([first, second], new() { Enabled = true }, null, default));
    }

    [Fact]
    public async Task CancellationAndSourceMutationDoNotPublishAReport()
    {
        string first = WriteFlat(_root, "first");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FlatQualityService.ReviewAsync([first], new() { Enabled = true },
            new InlineProgress(_ => cancel.Cancel()), cancel.Token));
        await Assert.ThrowsAsync<IOException>(() => FlatQualityService.ReviewAsync([first], new() { Enabled = true },
            new InlineProgress(_ => File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1))), default));
    }

    [Theory]
    [InlineData(0, false, 5, 3.76, 0)]
    [InlineData(-1, false, 5, 3.76, 0)]
    [InlineData(65535, true, 0, 3.76, 0)]
    [InlineData(65535, true, 5, 0, 0)]
    [InlineData(65535, true, 5, 3.76, 1)]
    public void InvalidUnitsOrGeometryAreRejected(double scale, bool optics, double focal, double pixel, double obstruction)
    {
        Assert.Throws<ArgumentException>(() => new FlatQualityOptions { Enabled = true, FullScale = scale, UseOptics = optics,
            FocalRatio = focal, PixelSizeUm = pixel, Obstruction = obstruction }.Validate());
    }

    [Fact]
    public void DisabledGeometryIsNotSentAndNormalizedUnitsAreAccepted()
    {
        new FlatQualityOptions { Enabled = true, FullScale = 1 }.Validate();
        string json = JsonSerializer.Serialize(new FlatQualityMeasureRequest("flat.fits", 1, true, null, null, null), FlatQualityJsonContext.Default.FlatQualityMeasureRequest);
        Assert.DoesNotContain("focalRatio", json);
        Assert.DoesNotContain("obstruction", json);
    }

    private sealed class InlineProgress(Action<CalibrationPreparationProgress> action) : IProgress<CalibrationPreparationProgress>
    { public void Report(CalibrationPreparationProgress value) => action(value); }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
