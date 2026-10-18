using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Seiza.App.Interop;
using Seiza.App.Models;

namespace Seiza.App.Services;

internal static partial class FlatQualityService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_calibration_measure_flat_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Measure(string request, out nint error);
    [LibraryImport("seiza_cabi", EntryPoint = "seiza_calibration_screen_flats_json", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Screen(string request, out nint error);

    public static async Task<FlatQualityReview> ReviewAsync(IReadOnlyList<string> paths, FlatQualityOptions options,
        IProgress<CalibrationPreparationProgress>? progress, CancellationToken token)
    {
        options.Validate();
        if (!options.Enabled || paths.Count == 0) throw new ArgumentException("Select raw flats and enable flat quality review.");
        string[] selected = paths.Select(Path.GetFullPath).ToArray();
        if (selected.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new ArgumentException("Flat review inputs must be unique.");
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var identities = selected.Select(Identity).ToArray();
                var measured = new FlatQualityMeasurement[selected.Length];
                for (int i = 0; i < selected.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new(CalibrationPreparationStage.Reviewing,
                        $"Measuring flat quality: {Path.GetFileName(selected[i])}", i, selected.Length, "flat", selected[i]));
                    var request = new FlatQualityMeasureRequest(selected[i], options.FullScale, true,
                        options.UseOptics ? options.FocalRatio : null, options.UseOptics ? options.PixelSizeUm : null,
                        options.UseOptics ? options.Obstruction : null);
                    measured[i] = MeasureFrame(request);
                    token.ThrowIfCancellationRequested();
                }
                FlatQualityReport report = ScreenFrames(measured);
                token.ThrowIfCancellationRequested();
                if (!identities.SequenceEqual(selected.Select(Identity)))
                    throw new IOException("A selected flat changed during review. No partial quality verdict is available.");
                return new FlatQualityReview(selected, measured, report);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    internal static FlatQualityMeasurement MeasureFrame(FlatQualityMeasureRequest request)
    {
        string json = JsonSerializer.Serialize(request, FlatQualityJsonContext.Default.FlatQualityMeasureRequest);
        return Read(Measure(json, out nint error), error, FlatQualityJsonContext.Default.FlatQualityMeasurement);
    }
    internal static FlatQualityReport ScreenFrames(FlatQualityMeasurement[] measurements)
    {
        string json = JsonSerializer.Serialize(new FlatQualityScreenRequest(measurements), FlatQualityJsonContext.Default.FlatQualityScreenRequest);
        var report = Read(Screen(json, out nint error), error, FlatQualityJsonContext.Default.FlatQualityReport);
        if (report.SchemaVersion != 1 || report.Frames is null || report.Frames.Length != measurements.Length || report.Master is null ||
            report.Frames.Append(report.Master).Any(v => v is null || v.Findings is null || v.Grade is not ("ok" or "suspect" or "poor")))
            throw new SeizaCoreException("The native flat quality report does not describe the complete selected set.");
        return report;
    }
    private static T Read<T>(nint result, nint error, JsonTypeInfo<T> type)
    {
        if (result == 0) throw NativeString.TakeError(error, "Flat quality review failed.");
        if (error != 0) NativeMethods.FreeString(error);
        return JsonSerializer.Deserialize(NativeString.TakeOwned(result, ""), type)
            ?? throw new SeizaCoreException("Invalid flat quality response.");
    }
    private static (long, DateTime, string?) Identity(string path)
    {
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc, WindowsFileIdentity.TryGet(path));
    }
}
