using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Seiza.App.Windows.Tests;

public sealed class ParallaxAdditionalExportTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PngSequencePublishesNumberedLosslessTopDownFramesWithOneBuffer()
    {
        using var directory = new ScratchDirectory();
        string destination = Path.Combine(directory.Path, "frames");
        var source = new Frames();
        var reports = new List<ParallaxExportProgress>();
        await ParallaxVideoExporter.ExportAsync(source, destination,
            new ParallaxExportOptions(ParallaxExportFormat.PngSequence),
            progress: new InlineProgress<ParallaxExportProgress>(reports.Add));

        Assert.Equal(Enumerable.Range(0, source.FrameCount), source.Rendered);
        Assert.Equal(Enumerable.Range(0, source.FrameCount).Select(index => $"frame-{index:D5}.png"),
            Directory.EnumerateFiles(destination).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(reports, report => Assert.Equal(1, report.AllocatedBuffers));
        Assert.Equal(1, reports[^1].Fraction);
        foreach (int index in Enumerable.Range(0, source.FrameCount))
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(Path.Combine(destination, $"frame-{index:D5}.png"));
            using var stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            Assert.Equal((uint)source.Width, decoder.PixelWidth);
            Assert.Equal((uint)source.Height, decoder.PixelHeight);
            byte[] pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
            byte[] expected = new byte[pixels.Length];
            Frames.Fill(index, expected, source.Width, source.Height);
            Assert.Equal(expected, pixels);
        }
        AssertNoStaging(directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PngSequenceNeverReplacesAnExistingDirectory(bool withSourceImage)
    {
        using var directory = new ScratchDirectory();
        string destination = Path.Combine(directory.Path, "existing-source-folder");
        Directory.CreateDirectory(destination);
        string sourcePath = Path.Combine(destination, "frame-00000.png");
        if (withSourceImage) await File.WriteAllTextAsync(sourcePath, "original source, not a generated sequence");
        var source = new Frames();
        await Assert.ThrowsAsync<IOException>(() => ParallaxVideoExporter.ExportAsync(source, destination,
            new ParallaxExportOptions(ParallaxExportFormat.PngSequence), [sourcePath]));
        Assert.Empty(source.Rendered);
        if (withSourceImage) Assert.Equal("original source, not a generated sequence", await File.ReadAllTextAsync(sourcePath));
        else Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        AssertNoStaging(directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCanceledPngSequenceNeverPublishesPartialFrames(bool cancel)
    {
        using var directory = new ScratchDirectory();
        string destination = Path.Combine(directory.Path, "frames");
        using var cancellation = new CancellationTokenSource();
        var source = new Frames { FailureAt = cancel ? null : 1 };
        var progress = new InlineProgress<ParallaxExportProgress>(_ => { if (cancel) cancellation.Cancel(); });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParallaxVideoExporter.ExportAsync(source, destination,
                new ParallaxExportOptions(ParallaxExportFormat.PngSequence), progress: progress, cancellationToken: cancellation.Token));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => ParallaxVideoExporter.ExportAsync(source, destination,
                new ParallaxExportOptions(ParallaxExportFormat.PngSequence), progress: progress));
        Assert.False(Directory.Exists(destination));
        AssertNoStaging(directory);
    }

    [Fact]
    public async Task PngSequenceRechecksAConcurrentDestinationBeforePublication()
    {
        using var directory = new ScratchDirectory();
        string destination = Path.Combine(directory.Path, "frames");
        string sentinel = Path.Combine(destination, "original.png");
        var source = new Frames();
        await Assert.ThrowsAsync<IOException>(() => ParallaxVideoExporter.ExportAsync(source, destination,
            new ParallaxExportOptions(ParallaxExportFormat.PngSequence), progress: new InlineProgress<ParallaxExportProgress>(report =>
            {
                if (report.CompletedFrames != report.TotalFrames) return;
                Directory.CreateDirectory(destination);
                File.WriteAllText(sentinel, "concurrent source");
            })));
        Assert.Equal("concurrent source", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFiles(destination));
        AssertNoStaging(directory);
    }

    [Fact]
    public async Task NativeMp4OptionsRejectProtectedInputsBeforeRendering()
    {
        using var directory = new ScratchDirectory();
        string destination = Path.Combine(directory.Path, "source.mp4");
        await File.WriteAllTextAsync(destination, "original input");
        var source = new Frames();
        await Assert.ThrowsAsync<SeizaCoreException>(() => ParallaxVideoExporter.ExportAsync(source, destination,
            new ParallaxExportOptions(ParallaxExportFormat.Mp4), [destination]));
        Assert.Empty(source.Rendered);
        Assert.Equal("original input", await File.ReadAllTextAsync(destination));
        AssertNoStaging(directory);
    }

    [Fact]
    public async Task NativeMp4OptionsRecheckAProtectedAliasBeforePublication()
    {
        using var directory = new ScratchDirectory();
        string input = Path.Combine(directory.Path, "artwork.png"), destination = Path.Combine(directory.Path, "movie.mp4");
        await File.WriteAllTextAsync(input, "original artwork");
        await Assert.ThrowsAsync<SeizaCoreException>(() => ParallaxVideoExporter.ExportAsync(new Frames(), destination,
            new ParallaxExportOptions(ParallaxExportFormat.Mp4), [input], new InlineProgress<ParallaxExportProgress>(report =>
            {
                if (report.CompletedFrames == report.TotalFrames)
                    Assert.True(CreateHardLink(destination, input, 0), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
            })));
        Assert.Equal("original artwork", await File.ReadAllTextAsync(input));
        Assert.Equal("original artwork", await File.ReadAllTextAsync(destination));
        AssertNoStaging(directory);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MissingOptionalFfmpegPreservesExistingOutputWithoutRendering(int formatValue)
    {
        var format = (ParallaxExportFormat)formatValue;
        using var directory = new ScratchDirectory();
        string destination = Path.Combine(directory.Path, format == ParallaxExportFormat.Mov ? "movie.mov" : "movie.mkv");
        await File.WriteAllTextAsync(destination, "previous output");
        var source = new Frames();
        FileNotFoundException error = await Assert.ThrowsAsync<FileNotFoundException>(() => ParallaxVideoExporter.ExportAsync(source, destination,
            new ParallaxExportOptions(format, FfmpegPath: Path.Combine(directory.Path, "missing-ffmpeg.exe"))));
        Assert.Contains("FFmpeg", error.Message, StringComparison.Ordinal);
        Assert.Empty(source.Rendered);
        Assert.Equal("previous output", await File.ReadAllTextAsync(destination));
        AssertNoStaging(directory);
    }

    [Fact]
    public async Task InvalidFfmpegExecutableReportsProbeFailureWithoutChangingOutput()
    {
        using var directory = new ScratchDirectory();
        string destination = Path.Combine(directory.Path, "movie.mov");
        await File.WriteAllTextAsync(destination, "previous output");
        var source = new Frames();
        // A harmless OS executable, deliberately not FFmpeg. No shell or script.
        string executable = Path.Combine(Environment.SystemDirectory, "where.exe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ParallaxVideoExporter.ExportAsync(source, destination,
            new ParallaxExportOptions(ParallaxExportFormat.Mov, FfmpegPath: executable)));
        Assert.Empty(source.Rendered);
        Assert.Equal("previous output", await File.ReadAllTextAsync(destination));
        AssertNoStaging(directory);
    }

    [Fact]
    public async Task FfmpegExecutableAliasIsProtectedBeforeTheProcessStarts()
    {
        using var directory = new ScratchDirectory();
        string executable = Path.Combine(directory.Path, "ffmpeg.exe");
        string destination = Path.Combine(directory.Path, "movie.mov");
        await File.WriteAllTextAsync(executable, "scratch executable sentinel; must never be started or replaced");
        Assert.True(CreateHardLink(destination, executable, 0), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        var source = new Frames();
        await Assert.ThrowsAsync<SeizaCoreException>(() => ParallaxVideoExporter.ExportAsync(source, destination,
            new ParallaxExportOptions(ParallaxExportFormat.Mov, FfmpegPath: executable)));
        Assert.Empty(source.Rendered);
        Assert.Equal("scratch executable sentinel; must never be started or replaced", await File.ReadAllTextAsync(executable));
        Assert.Equal(await File.ReadAllTextAsync(executable), await File.ReadAllTextAsync(destination));
        AssertNoStaging(directory);
    }

    [Theory]
    [InlineData(1, "mov")]
    [InlineData(2, "matroska")]
    public void FfmpegArgumentsAreFixedTokensWithAnExplicitFileMuxer(int formatValue, string muxer)
    {
        var format = (ParallaxExportFormat)formatValue;
        string destination = Path.Combine(Path.GetTempPath(), "-option:input name." + (format == ParallaxExportFormat.Mov ? "mov" : "mkv"));
        string[] arguments = ParallaxVideoExporter.FfmpegArguments(new Frames(), new(format), "libx264", destination);
        Assert.Equal("-f", arguments[^3]);
        Assert.Equal(muxer, arguments[^2]);
        Assert.Equal("file:" + Path.GetFullPath(destination), arguments[^1]);
        Assert.Contains("-nostdin", arguments);
        Assert.Contains("-y", arguments);
        Assert.Contains("pipe:0", arguments);
        ProcessStartInfo info = ParallaxVideoExporter.FfmpegStartInfo(@"C:\encoder folder\ffmpeg.exe", arguments);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Empty(info.Arguments);
        Assert.Equal(arguments, info.ArgumentList);
    }

    [FfmpegFact]
    public async Task OptionalFfmpegWritesDecodableMovAndMkvAndCancelsWithoutLeavingAChild()
    {
        using var directory = new ScratchDirectory();
        string executable = Environment.GetEnvironmentVariable("SEIZA_PARALLAX_FFMPEG")!;
        foreach (ParallaxExportFormat format in new[] { ParallaxExportFormat.Mov, ParallaxExportFormat.Matroska })
        {
            string destination = Path.Combine(directory.Path, format == ParallaxExportFormat.Mov ? "movie.mov" : "movie.mkv");
            var source = new Frames();
            var reports = new List<ParallaxExportProgress>();
            await ParallaxVideoExporter.ExportAsync(source, destination, new ParallaxExportOptions(format, FfmpegPath: executable),
                progress: new InlineProgress<ParallaxExportProgress>(reports.Add));
            if (Environment.GetEnvironmentVariable("SEIZA_PARALLAX_EXPORT_DIAGNOSTICS") is string diagnostics && !string.IsNullOrWhiteSpace(diagnostics))
            {
                Directory.CreateDirectory(Path.GetFullPath(diagnostics));
                File.Copy(destination, Path.Combine(Path.GetFullPath(diagnostics), Path.GetFileName(destination)), overwrite: true);
            }
            MediaClip clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(destination));
            VideoEncodingProperties encoding = clip.GetVideoEncodingProperties();
            output.WriteLine($"Windows {format} frame-rate metadata: {encoding.FrameRate.Numerator}/{encoding.FrameRate.Denominator}");
            Assert.Equal((uint)source.Width, encoding.Width);
            Assert.Equal((uint)source.Height, encoding.Height);
            Assert.Equal(MediaEncodingSubtypes.H264, encoding.Subtype, ignoreCase: true);
            Assert.InRange(clip.OriginalDuration.TotalSeconds, 0.49, 0.51);
            Assert.Empty(clip.EmbeddedAudioTracks);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.Zero, source.Width, source.Height, VideoFramePrecision.NearestFrame);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(thumbnail);
            byte[] pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
            int top = (source.Height / 4 * source.Width + source.Width / 2) * 4;
            int bottom = (source.Height * 3 / 4 * source.Width + source.Width / 2) * 4;
            Assert.InRange(pixels[top], 0, 45); Assert.InRange(pixels[top + 2], 170, 230);
            Assert.InRange(pixels[bottom], 170, 230); Assert.InRange(pixels[bottom + 2], 0, 45);
            foreach (int index in new[] { 6, 11 })
            {
                using var timed = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(index / 24.0), source.Width, source.Height, VideoFramePrecision.NearestFrame);
                BitmapDecoder timedDecoder = await BitmapDecoder.CreateAsync(timed);
                byte[] timedPixels = (await timedDecoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
                output.WriteLine($"Windows {format} frame near {index}/24s: green={timedPixels[top + 1]}, expected~{20 + index * 12}");
                Assert.InRange(timedPixels[top + 1], 20 + index * 12 - 18, 20 + index * 12 + 18);
            }
            if (format == ParallaxExportFormat.Mov)
                Assert.Equal(24.0, (double)encoding.FrameRate.Numerator / encoding.FrameRate.Denominator, 4);
            else
            {
                // Windows MF reports about half the actual MKV rate on this
                // reader, even with no B frames and explicit CFR. Verify the
                // container's nanoseconds/frame and independently decode every
                // timestamp rather than treating that metadata as authoritative.
                ulong duration = MatroskaDefaultDuration(await File.ReadAllBytesAsync(destination));
                Assert.InRange(duration, 41_666_665ul, 41_666_667ul);
                output.WriteLine($"Matroska DefaultDuration={duration}ns (~24fps); Windows MF reported FPS is not used for MKV timing validation.");
            }
            string inspection = await InspectWithFfmpegAsync(executable, destination);
            MatchCollection frames = Regex.Matches(inspection, @"\bn:\s*(\d+)\s+pts:\s*(-?\d+)\s+pts_time:\s*([-0-9.]+)", RegexOptions.CultureInvariant);
            Assert.Equal(source.FrameCount, frames.Count);
            for (int index = 0; index < frames.Count; index++)
            {
                Assert.Equal(index, int.Parse(frames[index].Groups[1].Value, CultureInfo.InvariantCulture));
                double timestamp = double.Parse(frames[index].Groups[3].Value, CultureInfo.InvariantCulture);
                Assert.InRange(timestamp, index / 24.0 - 0.001, index / 24.0 + 0.001);
            }
            Assert.Contains("frame_rate: 24/1", inspection, StringComparison.Ordinal);
            Assert.Contains("color_primaries:bt709 color_trc:iec61966-2-1", inspection, StringComparison.Ordinal);
            if (Environment.GetEnvironmentVariable("SEIZA_PARALLAX_EXPORT_DIAGNOSTICS") is string inspectionDirectory && !string.IsNullOrWhiteSpace(inspectionDirectory))
                await File.WriteAllTextAsync(Path.Combine(Path.GetFullPath(inspectionDirectory), Path.GetFileName(destination) + ".ffmpeg-inspection.txt"), inspection);
            Assert.Equal(Enumerable.Range(0, source.FrameCount), source.Rendered);
            Assert.All(reports, report => Assert.Equal(1, report.AllocatedBuffers));
            output.WriteLine($"FFmpeg {format}: {encoding.Width}x{encoding.Height}, {clip.OriginalDuration.TotalSeconds:F3}s, H.264, silent.");
        }

        string canceled = Path.Combine(directory.Path, "canceled.mov");
        await File.WriteAllTextAsync(canceled, "previous movie");
        using var cancellation = new CancellationTokenSource();
        string processName = Path.GetFileNameWithoutExtension(executable);
        HashSet<int> existing = ProcessIds(processName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParallaxVideoExporter.ExportAsync(
            new Frames { FrameCount = 1200 }, canceled, new ParallaxExportOptions(ParallaxExportFormat.Mov, FfmpegPath: executable),
            progress: new InlineProgress<ParallaxExportProgress>(_ => cancellation.Cancel()), cancellationToken: cancellation.Token));
        Assert.Equal("previous movie", await File.ReadAllTextAsync(canceled));
        Assert.Subset(existing, ProcessIds(processName));

        string failed = Path.Combine(directory.Path, "failed.mkv");
        await File.WriteAllTextAsync(failed, "previous failed destination");
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ParallaxVideoExporter.ExportAsync(
            new Frames { FailureAt = 1 }, failed, new ParallaxExportOptions(ParallaxExportFormat.Matroska, FfmpegPath: executable)));
        Assert.Equal("Synthetic frame failure.", failure.Message);
        Assert.Equal("previous failed destination", await File.ReadAllTextAsync(failed));
        Assert.Subset(existing, ProcessIds(processName));
        AssertNoStaging(directory);
    }

    private static async Task<string> InspectWithFfmpegAsync(string executable, string path)
    {
        using var process = new Process
        {
            StartInfo = ParallaxVideoExporter.FfmpegStartInfo(executable,
                ["-hide_banner", "-nostdin", "-i", "file:" + path, "-map", "0:v:0", "-vf", "showinfo", "-f", "null", "-"]),
        };
        Assert.True(process.Start());
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            string details = await errors;
            await standardOutput;
            Assert.Equal(0, process.ExitCode);
            return details;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static ulong MatroskaDefaultDuration(byte[] bytes)
    {
        // DefaultDuration (0x23E383) is nanoseconds per frame. Its first
        // occurrence in these generated single-track fixtures is TrackEntry.
        int offset = bytes.AsSpan().IndexOf(new byte[] { 0x23, 0xE3, 0x83 });
        Assert.True(offset >= 0, "Matroska track has no DefaultDuration.");
        offset += 3;
        int marker = 0x80, length = 1;
        while ((bytes[offset] & marker) == 0) { marker >>= 1; length++; Assert.InRange(length, 1, 8); }
        ulong size = (uint)(bytes[offset] & (marker - 1));
        for (int index = 1; index < length; index++) size = (size << 8) | bytes[offset + index];
        Assert.InRange(size, 1ul, 8ul);
        offset += length;
        ulong duration = 0;
        for (ulong index = 0; index < size; index++) duration = (duration << 8) | bytes[offset + checked((int)index)];
        return duration;
    }

    [FfmpegFact]
    public async Task OptionalFfmpegHevcProducesTaggedMovOrReportsMissingEncoder()
    {
        using var directory = new ScratchDirectory();
        string executable = Environment.GetEnvironmentVariable("SEIZA_PARALLAX_FFMPEG")!;
        string destination = Path.Combine(directory.Path, "hevc.mov");
        await File.WriteAllTextAsync(destination, "previous movie");
        try
        {
            await ParallaxVideoExporter.ExportAsync(new Frames(), destination,
                new ParallaxExportOptions(ParallaxExportFormat.Mov, ParallaxVideoCodec.Hevc, executable));
            byte[] movie = await File.ReadAllBytesAsync(destination);
            Assert.Equal(12, ParallaxVideoExporterTests.CountVideoSamples(movie));
            Assert.Contains("hvc1", System.Text.Encoding.Latin1.GetString(movie), StringComparison.Ordinal);
            output.WriteLine("FFmpeg HEVC MOV encoded 12 frames with hvc1 tagging; no Windows HEVC decoder is required for this check.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("no libx265", StringComparison.Ordinal))
        {
            Assert.Equal("previous movie", await File.ReadAllTextAsync(destination));
            output.WriteLine("Selected FFmpeg lacks libx265; verified actionable failure and output preservation.");
        }
        AssertNoStaging(directory);
    }

    private static HashSet<int> ProcessIds(string name)
    {
        var ids = new HashSet<int>();
        foreach (Process process in Process.GetProcessesByName(name))
        {
            using (process) ids.Add(process.Id);
        }
        return ids;
    }

    private static void AssertNoStaging(ScratchDirectory directory) => Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path, ".seiza-parallax-*"));

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, nint securityAttributes);

    private sealed class Frames : IParallaxVideoFrameSource
    {
        public int Width => 64;
        public int Height => 48;
        public int FramesPerSecond => 24;
        public int FrameCount { get; init; } = 12;
        internal int? FailureAt { get; init; }
        internal List<int> Rendered { get; } = [];
        public void RenderBgraFrame(int index, Span<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == FailureAt) throw new InvalidOperationException("Synthetic frame failure.");
            Rendered.Add(index);
            Fill(index, destination, Width, Height);
        }
        internal static void Fill(int index, Span<byte> destination, int width, int height)
        {
            for (int pixel = 0; pixel < width * height; pixel++)
            {
                bool bottom = pixel / width >= height / 2;
                destination[pixel * 4] = bottom ? (byte)200 : (byte)(10 + index);
                destination[pixel * 4 + 1] = (byte)(20 + index * 12 % 220);
                destination[pixel * 4 + 2] = bottom ? (byte)10 : (byte)200;
                destination[pixel * 4 + 3] = 255;
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class ScratchDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SeizaAdditionalExportTests-" + Guid.NewGuid().ToString("N"));
        internal ScratchDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SEIZA_PARALLAX_FFMPEG")))
                Skip = "Set SEIZA_PARALLAX_FFMPEG to an installed ffmpeg.exe for real MOV/MKV and cancellation checks.";
        }
    }
}
