using Seiza.App.Models;

namespace Seiza.App.Services;

internal static class ParallaxExportOutput
{
    internal static string ValidateFile(string path, ParallaxExportFormat format, IEnumerable<string?> protectedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string destination = LiveStackPath.NormalizeForComparison(path);
        string extension = Path.GetExtension(destination);
        bool supported = format switch
        {
            ParallaxExportFormat.Mp4 => extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase),
            ParallaxExportFormat.Mov => extension.Equals(".mov", StringComparison.OrdinalIgnoreCase),
            ParallaxExportFormat.Matroska => extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!supported) throw new ArgumentException("The output filename must match the selected MP4, MOV or MKV format.", nameof(path));
        ValidateProtected(destination, protectedPaths);
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("The export output directory does not exist.");
        return destination;
    }

    internal static string ValidateSequence(string path, IEnumerable<string?> protectedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string destination = Path.TrimEndingDirectorySeparator(LiveStackPath.NormalizeForComparison(path));
        ValidateProtected(destination, protectedPaths);
        // Never clear a previous sequence or write into a source directory.
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("PNG sequence export needs a new folder. Existing folders and files are never replaced.");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("Choose an existing parent folder for the PNG sequence.");
        return destination;
    }

    private static void ValidateProtected(string destination, IEnumerable<string?> protectedPaths)
    {
        string? identity = WindowsFileIdentity.TryGet(destination);
        foreach (string? source in protectedPaths)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (LiveStackPath.Equals(destination, source) || identity is not null &&
                string.Equals(identity, WindowsFileIdentity.TryGet(source), StringComparison.Ordinal))
                throw new SeizaCoreException("The export output must not overwrite a source image, input file, artwork or encoder executable.");
        }
    }
}
