namespace Seiza.App.Services;

internal static class AtomicOutputFile
{
    public static void Write(
        string destinationPath,
        Action<string> writeStagingFile,
        CancellationToken cancellationToken) =>
        Write(destinationPath, writeStagingFile, null, cancellationToken);

    public static void Write(
        string destinationPath,
        Action<string> writeStagingFile,
        Action? validateDestination,
        CancellationToken cancellationToken)
    {
        string destination = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The output path needs a parent directory.", nameof(destinationPath));
        string staging = Path.Combine(directory, $".seiza-stack-{Guid.NewGuid():N}.fits");
        try
        {
            writeStagingFile(staging);
            cancellationToken.ThrowIfCancellationRequested();
            // Sources or directory aliases may have appeared while the native
            // writer was running. Recheck the final name after staging closes.
            validateDestination?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(staging);
            }
            catch
            {
                // Never mask a stacking, cancellation, or publication failure.
            }
        }
    }
}
