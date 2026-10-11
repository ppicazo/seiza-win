namespace Seiza.App.Models;

internal static class LiveStackPath
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public static string NormalizeForComparison(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string ordinaryPath = RemoveExtendedPrefix(path);
        return Path.GetFullPath(ordinaryPath);
    }

    public static bool Equals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(
                NormalizeForComparison(left),
                NormalizeForComparison(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(
                RemoveExtendedPrefix(left),
                RemoveExtendedPrefix(right),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public static bool IsWithinDirectory(string path, string directory)
    {
        try
        {
            string fullPath = NormalizeForComparison(path);
            string normalizedDirectory = NormalizeForComparison(directory);
            string fullDirectory = Path.TrimEndingDirectorySeparator(normalizedDirectory);
            string directoryPrefix = Path.EndsInDirectorySeparator(normalizedDirectory)
                ? normalizedDirectory
                : normalizedDirectory + Path.DirectorySeparatorChar;
            return string.Equals(
                    fullPath,
                    fullDirectory,
                    StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(
                    directoryPrefix,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string RemoveExtendedPrefix(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[ExtendedUncPrefix.Length..];
        }
        if (path.StartsWith(ExtendedPathPrefix, StringComparison.OrdinalIgnoreCase) &&
            path.Length >= ExtendedPathPrefix.Length + 3 &&
            char.IsAsciiLetter(path[ExtendedPathPrefix.Length]) &&
            path[ExtendedPathPrefix.Length + 1] == ':' &&
            path[ExtendedPathPrefix.Length + 2] is '\\' or '/')
        {
            return path[ExtendedPathPrefix.Length..];
        }
        return path;
    }
}
