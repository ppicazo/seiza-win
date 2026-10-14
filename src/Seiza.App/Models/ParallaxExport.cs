namespace Seiza.App.Models;

internal enum ParallaxVideoCodec
{
    H264,
    Hevc,
}

internal enum ParallaxExportFormat
{
    Mp4,
    Mov,
    Matroska,
    PngSequence,
}

internal sealed record ParallaxExportOptions(
    ParallaxExportFormat Format,
    ParallaxVideoCodec Codec = ParallaxVideoCodec.H264,
    string? FfmpegPath = null);

internal sealed record ParallaxExportProgress(int CompletedFrames, int TotalFrames, int AllocatedBuffers = 0)
{
    public double Fraction => TotalFrames > 0 ? (double)CompletedFrames / TotalFrames : 0;
}

/// <summary>Pull-based display pixels; a movie is never materialized as a frame array.</summary>
internal interface IParallaxVideoFrameSource
{
    int Width { get; }
    int Height { get; }
    int FramesPerSecond { get; }
    int FrameCount { get; }

    void RenderBgraFrame(int index, Span<byte> destination, CancellationToken cancellationToken);
}
