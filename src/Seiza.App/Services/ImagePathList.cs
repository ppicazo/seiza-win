using Microsoft.VisualBasic.FileIO;

namespace Seiza.App.Services;

internal sealed record ImagePathListResult(IReadOnlyList<string> Paths, int SkippedCount);

internal static class ImagePathList
{
    public static ImagePathListResult Read(string listPath, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(listPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        List<string> paths = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;

        void AddPath(string value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value = value.Trim();
            if (value.Length == 0) return;
            try
            {
                string path = Path.GetFullPath(value, directory);
                if (!ImageFileTypes.IsSupportedImage(path) || !File.Exists(path))
                {
                    skipped++;
                    return;
                }
                if (seen.Add(path)) paths.Add(path);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                skipped++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetExtension(fullPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            using TextFieldParser parser = new(fullPath);
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            int pathColumn = 0;
            bool lookingForHeader = true;
            while (!parser.EndOfData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] fields;
                try
                {
                    fields = parser.ReadFields() ?? [];
                }
                catch (MalformedLineException exception)
                {
                    throw new InvalidDataException($"Invalid CSV at line {exception.LineNumber}.", exception);
                }
                if (lookingForHeader)
                {
                    int header = Array.FindIndex(fields, field =>
                        field.Trim().ToLowerInvariant() is "path" or "filepath" or "file_path" or "file" or "filename");
                    if (header >= 0)
                    {
                        pathColumn = header;
                        lookingForHeader = false;
                        // SubframeSelector exports settings before the table.
                        // Those rows are metadata, not skipped image entries.
                        skipped = 0;
                        continue;
                    }
                }
                if (fields.Length <= pathColumn || string.IsNullOrWhiteSpace(fields[pathColumn])) skipped++;
                else AddPath(fields[pathColumn]);
                // Once headerless image data begins, never reinterpret a later
                // metadata cell named "file" as a new column header.
                if (paths.Count > 0) lookingForHeader = false;
            }
        }
        else
        {
            foreach (string line in File.ReadLines(fullPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string value = line.Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                AddPath(value);
            }
        }
        return new ImagePathListResult(paths.AsReadOnly(), skipped);
    }
}
