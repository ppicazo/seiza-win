using System.Buffers.Binary;
using Seiza.App.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Xunit;

namespace Seiza.App.Windows.Tests;

public sealed class ParallaxDustMapSnapshotTests
{
    [Fact]
    public async Task PaintedSnapshotRetainsSixteenBitOpacityInOpaqueRgbAndDoesNotMutateTheDraft()
    {
        using var files = new Scratch();
        const int width = 9, height = 7;
        float[] opacity = Enumerable.Range(0, width * height).Select(index => index / (float)(width * height - 1)).ToArray();
        float[] original = opacity.ToArray();
        string path = Path.Combine(files.Directory, "painted.png");
        await ParallaxDustMapSnapshot.SaveAsync(opacity, width, height, path, CancellationToken.None);
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.Equal((uint)width, decoder.PixelWidth);
        Assert.Equal((uint)height, decoder.PixelHeight);
        byte[] decoded = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba16, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        Assert.Equal(opacity.Length * 8, decoded.Length);
        for (int index = 0; index < opacity.Length; index++)
        {
            ushort expected = (ushort)Math.Round(opacity[index] * ushort.MaxValue);
            for (int channel = 0; channel < 3; channel++)
            {
                ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(decoded.AsSpan(index * 8 + channel * 2, 2));
                Assert.Equal(expected, actual);
                Assert.InRange(Math.Abs(actual / (double)ushort.MaxValue - opacity[index]), 0, 1.0 / ushort.MaxValue);
            }
            Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(decoded.AsSpan(index * 8 + 6, 2)));
        }
        Assert.Equal(original, opacity);
    }

    [Fact]
    public async Task ExistingSnapshotIsNeverOverwritten()
    {
        using var files = new Scratch();
        string path = Path.Combine(files.Directory, "existing.png");
        byte[] before = [1, 3, 5, 7, 9];
        await File.WriteAllBytesAsync(path, before);
        await Assert.ThrowsAsync<IOException>(() => ParallaxDustMapSnapshot.SaveAsync([0.5f], 1, 1, path, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(System.IO.Directory.EnumerateFiles(files.Directory));
    }

    [Fact]
    public async Task CanceledSnapshotCreatesNothingAndLeavesAnyExistingFileAlone()
    {
        using var files = new Scratch();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string missing = Path.Combine(files.Directory, "missing.png");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParallaxDustMapSnapshot.SaveAsync([0.5f], 1, 1, missing, cancellation.Token));
        Assert.False(File.Exists(missing));
        string existing = Path.Combine(files.Directory, "existing.png");
        byte[] before = [9, 5, 1];
        await File.WriteAllBytesAsync(existing, before);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParallaxDustMapSnapshot.SaveAsync([0.5f], 1, 1, existing, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(existing));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-0.001f)]
    [InlineData(1.001f)]
    public async Task InvalidOpacityCreatesNoSnapshot(float opacity)
    {
        using var files = new Scratch();
        string path = Path.Combine(files.Directory, "invalid.png");
        await Assert.ThrowsAsync<ArgumentException>(() => ParallaxDustMapSnapshot.SaveAsync([opacity], 1, 1, path, CancellationToken.None));
        Assert.Empty(System.IO.Directory.EnumerateFiles(files.Directory));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(769, 1, 769)]
    [InlineData(1, 769, 769)]
    [InlineData(2, 2, 3)]
    public async Task InvalidDimensionsCreateNoSnapshot(int width, int height, int length)
    {
        using var files = new Scratch();
        string path = Path.Combine(files.Directory, "invalid.png");
        await Assert.ThrowsAsync<ArgumentException>(() => ParallaxDustMapSnapshot.SaveAsync(new float[length], width, height, path, CancellationToken.None));
        Assert.Empty(System.IO.Directory.EnumerateFiles(files.Directory));
    }

    private sealed class Scratch : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "SeizaDustSnapshotTests-" + Guid.NewGuid().ToString("N"));
        internal Scratch() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
