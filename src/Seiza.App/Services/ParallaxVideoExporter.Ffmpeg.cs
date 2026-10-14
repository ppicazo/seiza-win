using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static partial class ParallaxVideoExporter
{
    internal static string ResolveFfmpeg(string? selectedPath)
    {
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            string path = Path.GetFullPath(selectedPath);
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
            throw new FileNotFoundException("Choose an existing FFmpeg .exe for MOV/MKV export. MP4 and PNG sequence do not need FFmpeg.", path);
        }
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(entry.Trim('"'), "ffmpeg.exe"));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        throw new FileNotFoundException("MOV/MKV export needs FFmpeg. Choose an installed ffmpeg.exe or add it to PATH; nothing is downloaded. MP4 and PNG sequence work without it.");
    }

    internal static ProcessStartInfo FfmpegStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static string[] FfmpegArguments(IParallaxVideoFrameSource video, ParallaxExportOptions options,
        string encoder, string staging)
    {
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", FormattableString.Invariant($"{video.Width}x{video.Height}"),
            "-framerate", video.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0", "-an", "-c:v", encoder,
            // Match the native MP4 path: no B-frame reordering or reordered
            // presentation timestamps for Windows' Matroska reader to infer.
            "-bf", "0", "-r", video.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
        ];
        if (encoder == "libopenh264")
        {
            arguments.AddRange(["-b:v", Math.Max(2_000_000L, (long)video.Width * video.Height * video.FramesPerSecond / 2).ToString(CultureInfo.InvariantCulture),
                "-allow_skip_frames", "0"]);
        }
        else arguments.AddRange(["-preset", "medium", "-crf", options.Codec == ParallaxVideoCodec.Hevc ? "18" : "16"]);
        if (encoder == "libx264") arguments.AddRange(["-x264-params", "force-cfr=1"]);
        if (options.Codec == ParallaxVideoCodec.Hevc) arguments.AddRange(["-tag:v", "hvc1", "-x265-params", "log-level=error"]);
        arguments.AddRange(["-vf", "scale=out_color_matrix=bt709:out_range=tv,setparams=colorspace=bt709:color_primaries=bt709:color_trc=iec61966-2-1:range=tv", "-pix_fmt", "yuv420p",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "iec61966-2-1", "-color_range", "tv",
            "-frames:v", video.FrameCount.ToString(CultureInfo.InvariantCulture)]);
        if (options.Format == ParallaxExportFormat.Mov) arguments.AddRange(["-movflags", "+faststart"]);
        arguments.AddRange(["-f", options.Format == ParallaxExportFormat.Mov ? "mov" : "matroska", "file:" + Path.GetFullPath(staging)]);
        return arguments.ToArray();
    }

    private static async Task ExportFfmpegAsync(IParallaxVideoFrameSource video, string destinationPath,
        ParallaxExportOptions options, string[] protectedPaths, IProgress<ParallaxExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string executable = ResolveFfmpeg(options.FfmpegPath);
        string[] protectedFiles = ParallaxInputPaths.Capture(protectedPaths.Append(executable));
        string destination = ParallaxExportOutput.ValidateFile(destinationPath, options.Format, protectedFiles);
        string encoder = await ProbeEncoderAsync(executable, options.Codec, cancellationToken).ConfigureAwait(false);
        string staging = Path.Combine(Path.GetDirectoryName(destination)!, $".seiza-parallax-{Guid.NewGuid():N}{Path.GetExtension(destination)}");
        bool ownsStaging = false;
        try
        {
            using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) ownsStaging = true;
            using var process = new Process { StartInfo = FfmpegStartInfo(executable, FfmpegArguments(video, options, encoder, staging)) };
            StartFfmpeg(process);
            Task<string> errors = ReadBoundedAsync(process.StandardError, 8192);
            Task<string> standardOutput = ReadBoundedAsync(process.StandardOutput, 8192);
            using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(static state => StopProcess((Process)state!), process);
            try
            {
                byte[] buffer = GC.AllocateUninitializedArray<byte>(checked(video.Width * video.Height * 4));
                for (int index = 0; index < video.FrameCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int frame = index;
                    await Task.Run(() => video.RenderBgraFrame(frame, buffer, cancellationToken), cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                    progress?.Report(new(index + 1, video.FrameCount, 1));
                }
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                string detail = await errors.ConfigureAwait(false);
                await standardOutput.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg could not encode this movie: " + detail.Trim());
                if (new FileInfo(staging).Length == 0) throw new InvalidOperationException("FFmpeg produced an empty movie.");
            }
            catch (IOException error) when (!cancellationToken.IsCancellationRequested)
            {
                StopProcess(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("FFmpeg stopped accepting video frames: " + (await errors.ConfigureAwait(false)).Trim(), error);
            }
            finally
            {
                StopProcess(process);
                try { process.StandardInput.Close(); } catch (IOException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                await standardOutput.ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ParallaxExportOutput.ValidateFile(destination, options.Format, protectedFiles);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            if (ownsStaging)
            {
                try { File.Delete(staging); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            // Cancellation may first surface as a broken pipe after killing the child.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async Task<string> ProbeEncoderAsync(string executable, ParallaxVideoCodec codec, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = FfmpegStartInfo(executable, ["-hide_banner", "-encoders"]) };
        StartFfmpeg(process);
        process.StandardInput.Close();
        Task<string> encoders = ReadBoundedAsync(process.StandardOutput, 128 * 1024);
        Task<string> errors = ReadBoundedAsync(process.StandardError, 8192);
        using CancellationTokenRegistration registration = timeout.Token.UnsafeRegister(static state => StopProcess((Process)state!), process);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string listing = await encoders.ConfigureAwait(false);
            string details = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg encoder check failed: " + details.Trim());
            HashSet<string> names = listing.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            if (codec == ParallaxVideoCodec.Hevc && names.Contains("libx265")) return "libx265";
            if (codec == ParallaxVideoCodec.H264)
            {
                if (names.Contains("libx264")) return "libx264";
                if (names.Contains("libopenh264")) return "libopenh264";
            }
            throw new InvalidOperationException(codec == ParallaxVideoCodec.Hevc
                ? "This FFmpeg has no libx265 HEVC encoder. Choose H.264 or another FFmpeg installation."
                : "This FFmpeg has neither libx264 nor libopenh264. Choose an FFmpeg installation with an H.264 encoder.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("FFmpeg did not finish its encoder check within 15 seconds.");
        }
        finally
        {
            StopProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await encoders.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
    }

    private static void StartFfmpeg(Process process)
    {
        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException("Could not start the selected FFmpeg executable. MOV/MKV require an installed FFmpeg; MP4 and PNG sequence do not.", error);
        }
    }

    private static void StopProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        char[] buffer = new char[4096];
        var text = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            int keep = Math.Min(count, limit - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
        }
        return text.ToString();
    }
}
