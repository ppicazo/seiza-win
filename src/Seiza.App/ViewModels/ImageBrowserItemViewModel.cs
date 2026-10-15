using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Seiza.App.Services;

namespace Seiza.App.ViewModels;

public partial class ImageBrowserItemViewModel(string path, string? browserRoot = null) : ObservableObject
{
    private Task? _thumbnailLoadTask;

    public string Path { get; } = path;

    public string FileName { get; } = System.IO.Path.GetFileName(path);

    public string DisplayFolderPath { get; } = FolderLabel(path, browserRoot);

    private static string FolderLabel(string path, string? root)
    {
        string folder = System.IO.Path.GetDirectoryName(path) ?? path;
        if (root is null) return folder;
        string relative = System.IO.Path.GetRelativePath(root, folder);
        return relative == "." ? "(root)" : relative;
    }

    [ObservableProperty]
    public partial BitmapImage? Thumbnail { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool Failed { get; private set; }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        // Regrouping keeps this VM but cancels its old prefetch. A fresh caller
        // must wait for that load to unwind and retry, not mistake it for a
        // successfully cached thumbnail.
        try
        {
            while (_thumbnailLoadTask is { } pending)
            {
                await pending.WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { return; }
        if (Thumbnail is not null || Failed) return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thumbnailLoadTask = completion.Task;
        IsLoading = true;
        try
        {
            byte[]? png = await ThumbnailCacheService.GetAsync(Path, cancellationToken);
            if (png is null)
            {
                Failed = true;
                return;
            }

            await ThumbnailPngReader.WithStreamAsync(png, async stream =>
            {
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                Thumbnail = image;
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The next caller can retry after the cancelled owner unwinds.
        }
        catch
        {
            Failed = true;
        }
        finally
        {
            IsLoading = false;
            _thumbnailLoadTask = null;
            completion.SetResult();
        }
    }
}
