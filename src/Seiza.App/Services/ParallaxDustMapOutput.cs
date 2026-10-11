using Seiza.App.Models;

namespace Seiza.App.Services;

// Native dust-map writing selects the encoder from the path extension and does
// not know the scene's source files. Guard the final path before staging it.
internal static class ParallaxDustMapOutput
{
    internal static string ValidateDestination(string destinationPath, IEnumerable<string?> protectedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        string destination = LiveStackPath.NormalizeForComparison(destinationPath);
        string? destinationIdentity = WindowsFileIdentity.TryGet(destination);
        foreach (string? source in protectedPaths)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (LiveStackPath.Equals(destination, source) ||
                (destinationIdentity is not null &&
                 string.Equals(destinationIdentity, WindowsFileIdentity.TryGet(source), StringComparison.Ordinal)))
                throw new SeizaCoreException("The dust-map output must not overwrite a source image or input file.");
        }
        return destination;
    }

    internal static bool Write(string destinationPath, IReadOnlyList<string> protectedPaths,
        Func<string, bool> writeStagingFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeStagingFile);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = ValidateDestination(destinationPath, protectedPaths);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The dust map needs an output directory.", nameof(destinationPath));
        // Keep the extension: native code uses it to choose PNG/TIFF/FITS/XISF.
        string staging = Path.Combine(directory, $".seiza-dust-{Guid.NewGuid():N}{Path.GetExtension(destination)}");
        bool ownsStaging = false;
        try
        {
            using (new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                ownsStaging = true;
            bool written = writeStagingFile(staging);
            cancellationToken.ThrowIfCancellationRequested();
            if (!written) return false;
            // Recheck in case an existing destination changed during the write.
            ValidateDestination(destination, protectedPaths);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
            return true;
        }
        finally
        {
            if (ownsStaging)
            {
                try { File.Delete(staging); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
