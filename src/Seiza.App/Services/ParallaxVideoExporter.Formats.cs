using System.Runtime.InteropServices.WindowsRuntime;
using Seiza.App.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Seiza.App.Services;

internal static partial class ParallaxVideoExporter
{
    public static Task ExportAsync(
        IParallaxVideoFrameSource video,
        string destinationPath,
        ParallaxExportOptions options,
        IEnumerable<string?>? protectedPaths = null,
        IProgress<ParallaxExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(options);
        Validate(video, options.Codec);
        if (!Enum.IsDefined(options.Format)) throw new ArgumentOutOfRangeException(nameof(options));
        string[] protectedFiles = ParallaxInputPaths.Capture(protectedPaths ?? []);
        return options.Format switch
        {
            ParallaxExportFormat.Mp4 => ExportWithPreparationAsync(video, destinationPath, options.Codec,
                PrepareEncodingAsync, progress, protectedFiles, cancellationToken),
            ParallaxExportFormat.PngSequence => ExportPngSequenceAsync(video, destinationPath, protectedFiles, progress, cancellationToken),
            _ => ExportFfmpegAsync(video, destinationPath, options, protectedFiles, progress, cancellationToken),
        };
    }

    private static async Task ExportPngSequenceAsync(IParallaxVideoFrameSource video, string destinationPath,
        string[] protectedPaths, IProgress<ParallaxExportProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string destination = ParallaxExportOutput.ValidateSequence(destinationPath, protectedPaths);
        string staging = Path.Combine(Path.GetDirectoryName(destination)!, ".seiza-parallax-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        bool published = false;
        try
        {
            byte[] buffer = GC.AllocateUninitializedArray<byte>(checked(video.Width * video.Height * 4));
            for (int index = 0; index < video.FrameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int frame = index;
                await Task.Run(() => video.RenderBgraFrame(frame, buffer, cancellationToken), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(staging, FormattableString.Invariant($"frame-{index:D5}.png"));
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                using IRandomAccessStream stream = file.AsRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken).ConfigureAwait(false);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    checked((uint)video.Width), checked((uint)video.Height), 96, 96, buffer);
                await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
                progress?.Report(new(index + 1, video.FrameCount, 1));
            }
            cancellationToken.ThrowIfCancellationRequested();
            ParallaxExportOutput.ValidateSequence(destination, protectedPaths);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination); // Atomic, and never replaces an existing directory.
            published = true;
        }
        finally
        {
            if (!published)
            {
                // Only the unique sibling directory created by this export is owned.
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
