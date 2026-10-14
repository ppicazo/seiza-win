using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace Seiza.App.Services;

// A painted map is retained in the Session's private directory, so changing
// scene settings can prepare it again instead of silently losing the painting.
internal static class ParallaxDustMapSnapshot
{
    internal static async Task SaveAsync(float[] opacity, int width, int height, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opacity);
        if (width < 1 || height < 1 || width > 768 || height > 768 || opacity.Length != checked(width * height) ||
            opacity.Any(value => !float.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentException("The painted dust map needs finite opacity between zero and one, at most 768 pixels per side.", nameof(opacity));
        cancellationToken.ThrowIfCancellationRequested();
        // WinRT's PNG encoder accepts Rgba16 but not Gray16 input. Equal RGB
        // channels and opaque alpha retain a grayscale map's full precision.
        byte[] pixels = new byte[checked(opacity.Length * 8)];
        for (int i = 0; i < opacity.Length; i++)
        {
            ushort value = (ushort)Math.Round(opacity[i] * ushort.MaxValue);
            for (int channel = 0; channel < 3; channel++)
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 8 + channel * 2, 2), value);
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i * 8 + 6, 2), ushort.MaxValue);
        }
        bool created = false;
        try
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            created = true;
            using var stream = output.AsRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Rgba16, BitmapAlphaMode.Straight,
                (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            if (created) File.Delete(path);
            throw;
        }
    }
}
