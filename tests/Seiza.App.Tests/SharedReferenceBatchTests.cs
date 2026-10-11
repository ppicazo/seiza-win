using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class SharedReferenceBatchTests : IDisposable
{
    private static readonly string[] Filters = ["Ha", "OIII"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));

    public SharedReferenceBatchTests() => Directory.CreateDirectory(_directory);

    [StackReferenceImageTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedCanvasIsNotIntegratedAndBothFiltersWriteTheSameGrid(bool removeTransients)
    {
        string[] lights = [Path.Combine(_directory, "light-1.fits"), Path.Combine(_directory, "light-2.fits")];
        for (int index = 0; index < lights.Length; index++)
        {
            StackingOptionsNativeTests.WriteStarField(lights[index], index, bayer: false);
        }
        string reference = Path.Combine(_directory, "common.fits");
        WriteLargerCanvas(lights[0], reference);
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(reference));
        var progress = new List<ImageStackProgress>();
        ImageStackJob[] jobs = Filters.Select(filter => new ImageStackJob(
            new ImageStackGroup(filter, null, lights),
            new ImageStackRequest(lights, Path.Combine(_directory, $"stack-{filter}.fits"),
                new ImageStackOptions { RemoveTransients = removeTransients }, new ImageStackCalibration())
            {
                AlignmentReferencePath = reference,
                ChannelReferenceCandidates = [lights[0]],
            })).ToArray();

        ImageStackBatchResult result = await ImageStackService.StackBatchAsync(
            jobs, new InlineProgress<ImageStackProgress>(progress.Add));

        Assert.Equal(4, result.AcceptedFrames);
        Assert.Equal(0, result.RejectedFrames);
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, stack =>
        {
            Assert.Equal(2, stack.AcceptedFrames);
            Assert.Equal(lights[0], stack.ReferenceSelection!.ChannelReferencePath);
            Assert.Empty(stack.ReferenceSelection.Skipped);
            Assert.Equal(2, stack.Dispositions.Count);
            Assert.Equal(lights, stack.Dispositions.Select(disposition => disposition.Source));
            Assert.Null(stack.TransientRemovalNote);
            ImageMetadata metadata = SeizaCore.Render(stack.OutputPath).Metadata;
            Assert.Equal(288, metadata.Width);
            Assert.Equal(280, metadata.Height);
            Assert.Equal(56.5, metadata.Headers["CRVAL1"].GetDouble());
            Assert.Equal(24.0, metadata.Headers["CRVAL2"].GetDouble());
            Assert.Equal("RA---TAN", metadata.Headers["CTYPE1"].GetString());
        });
        Assert.Contains(progress, update => update.CompletedFrames == 0 && update.AcceptedFrames == 0);
        Assert.Equal(4, progress[^1].CompletedFrames);
        Assert.All(progress, update => Assert.InRange(update.AcceptedFrames, 0, 4));
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(reference)));
    }

    [StackReferenceImageFact]
    public async Task ASelectedLightCanSupplyTheCommonGridButIsPushedExactlyOnce()
    {
        string[] lights = [Path.Combine(_directory, "light-1.fits"), Path.Combine(_directory, "light-2.fits")];
        for (int index = 0; index < lights.Length; index++)
        {
            StackingOptionsNativeTests.WriteStarField(lights[index], index, bayer: false);
        }
        var job = new ImageStackJob(new ImageStackGroup("all", null, lights),
            new ImageStackRequest(lights, Path.Combine(_directory, "stack.fits"),
                new ImageStackOptions { RemoveTransients = false }, new ImageStackCalibration())
            {
                AlignmentReferencePath = lights[0],
                ChannelReferenceCandidates = [lights[1]],
            });

        ImageStackResult result = Assert.Single((await ImageStackService.StackBatchAsync([job], null)).Results);

        Assert.Equal(2, result.AcceptedFrames);
        Assert.Equal(new[] { lights[1], lights[0] }, result.Dispositions.Select(disposition => disposition.Source));
        Assert.Equal(lights[1], result.ReferenceSelection!.ChannelReferencePath);
    }

    [StackReferenceImageCandidatesFact]
    public async Task FallbackLightIsIntegratedFirstAndAllOtherLightsAreOfferedExactlyOnce()
    {
        string good = Path.Combine(_directory, "good.fits");
        string other = Path.Combine(_directory, "other.fits");
        string turned = Path.Combine(_directory, "turned.fits");
        string common = Path.Combine(_directory, "common.fits");
        StackingOptionsNativeTests.WriteStarField(good, 0, bayer: false);
        StackingOptionsNativeTests.WriteStarField(other, 1, bayer: false);
        WriteQuarterTurn(good, turned);
        WriteLargerCanvas(good, common);
        string[] lights = [turned, good, other];
        var job = new ImageStackJob(new ImageStackGroup("all", null, lights),
            new ImageStackRequest(lights, Path.Combine(_directory, "stack.fits"),
                new ImageStackOptions { RemoveTransients = false }, new ImageStackCalibration())
            {
                AlignmentReferencePath = common,
                ChannelReferenceCandidates = [turned, good],
            });

        ImageStackResult result = Assert.Single((await ImageStackService.StackBatchAsync([job], null)).Results);

        Assert.Equal(good, result.ReferenceSelection!.ChannelReferencePath);
        Assert.Equal(turned, Assert.Single(result.ReferenceSelection.Skipped).Path);
        Assert.Equal(new[] { good, turned, other }, result.Dispositions.Select(disposition => disposition.Source));
        Assert.True(result.Dispositions[0].Accepted);
        Assert.False(result.Dispositions[1].Accepted);
        Assert.True(result.Dispositions[2].Accepted);
        Assert.Equal(2, result.AcceptedFrames);
        Assert.Equal(1, result.RejectedFrames);
    }

    [StackReferenceImageFact]
    public async Task ManualReferenceDoesNotFallbackOrReplaceAnExistingOutputOnFailure()
    {
        string good = Path.Combine(_directory, "good.fits");
        string turned = Path.Combine(_directory, "turned.fits");
        string output = Path.Combine(_directory, "existing.fits");
        StackingOptionsNativeTests.WriteStarField(good, 0, bayer: false);
        WriteQuarterTurn(good, turned);
        File.WriteAllText(output, "previous output");
        string[] lights = [turned, good];
        var job = new ImageStackJob(new ImageStackGroup("all", null, lights),
            new ImageStackRequest(lights, output, new ImageStackOptions(), new ImageStackCalibration())
            {
                AlignmentReferencePath = good,
            });

        await Assert.ThrowsAsync<SeizaCoreException>(() => ImageStackService.StackBatchAsync([job], null));

        Assert.Equal("previous output", File.ReadAllText(output));
    }

    [Fact]
    public async Task SourceProtectionRunsBeforeAnyNativeCallOrOutputWrite()
    {
        string reference = Path.Combine(_directory, "common.fits");
        File.WriteAllText(reference, "protected reference");
        string[] lights = [Path.Combine(_directory, "light-1.fits"), Path.Combine(_directory, "light-2.fits")];
        var job = new ImageStackJob(new ImageStackGroup("all", null, lights),
            new ImageStackRequest(lights, reference, new ImageStackOptions(), new ImageStackCalibration())
            {
                AlignmentReferencePath = reference,
            });

        await Assert.ThrowsAsync<ArgumentException>(() => ImageStackService.StackBatchAsync([job], null));

        Assert.Equal("protected reference", File.ReadAllText(reference));
    }

    private static void WriteLargerCanvas(string light, string reference)
    {
        const int headerBytes = 2880;
        const int width = 288;
        const int height = 280;
        int dataBytes = width * height * sizeof(float);
        byte[] fits = new byte[headerBytes + ((dataBytes + 2879) / 2880) * 2880];
        Array.Fill(fits, (byte)' ', 0, headerBytes);
        string[] cards =
        [
            "SIMPLE  =                    T", "BITPIX  =                  -32", "NAXIS   =                    2",
            $"NAXIS1  = {width,20}", $"NAXIS2  = {height,20}",
            "CTYPE1  = 'RA---TAN'", "CTYPE2  = 'DEC--TAN'", "CRVAL1  =                 56.5", "CRVAL2  =                 24.0",
            "CRPIX1  =                144.5", "CRPIX2  =                140.5", "CD1_1   =               -0.001", "CD1_2   =                    0",
            "CD2_1   =                    0", "CD2_2   =                0.001", "END",
        ];
        for (int index = 0; index < cards.Length; index++)
        {
            Encoding.ASCII.GetBytes(cards[index].PadRight(80)).CopyTo(fits, index * 80);
        }
        byte[] source = File.ReadAllBytes(light);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = headerBytes + (y * width + x) * sizeof(float);
                if (x is >= 16 and < 272 && y is >= 12 and < 268)
                {
                    source.AsSpan(headerBytes + ((y - 12) * 256 + x - 16) * sizeof(float), sizeof(float))
                        .CopyTo(fits.AsSpan(offset, sizeof(float)));
                }
                else
                {
                    BinaryPrimitives.WriteSingleBigEndian(fits.AsSpan(offset, sizeof(float)), 1000);
                }
            }
        }
        File.WriteAllBytes(reference, fits);
    }

    private static void WriteQuarterTurn(string sourcePath, string destination)
    {
        const int headerBytes = 2880;
        const int side = 256;
        byte[] source = File.ReadAllBytes(sourcePath);
        byte[] turned = (byte[])source.Clone();
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                source.AsSpan(headerBytes + ((side - x - 1) * side + y) * sizeof(float), sizeof(float))
                    .CopyTo(turned.AsSpan(headerBytes + (y * side + x) * sizeof(float), sizeof(float)));
            }
        }
        File.WriteAllBytes(destination, turned);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
