namespace Seiza.App.Services;

internal static class ImageFileTypes
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
        Array.AsReadOnly(new[]
        {
            ".fits", ".fit", ".fts", ".xisf", ".jpg", ".jpeg", ".jfif", ".png", ".tif", ".tiff",
        });

    private static readonly HashSet<string> SupportedExtensionSet =
        new(SupportedExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AstronomyExtensionSet = new(
        [".fits", ".fit", ".fts", ".xisf"],
        StringComparer.OrdinalIgnoreCase);

    public static bool IsSupportedImage(string path) =>
        IsSupportedExtension(Path.GetExtension(path));

    public static bool IsAstronomyImage(string path) =>
        AstronomyExtensionSet.Contains(Path.GetExtension(path));

    public static bool IsSupportedExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && SupportedExtensionSet.Contains(extension);
}
