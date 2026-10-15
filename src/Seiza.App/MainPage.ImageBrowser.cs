using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Seiza.App.Models;
using Seiza.App.Services;
using Seiza.App.ViewModels;

namespace Seiza.App;

public sealed partial class MainPage
{
    private const string BrowserScanCanceledMessage =
        "Scan canceled. Reopen the folder or change Include subfolders to retry.";
    private CancellationTokenSource? _browserDiscoveryCancellation;
    private CollectionViewSource? _browserViewSource;
    private ImageBrowserGroupViewModel[] _browserGroups = [];
    private readonly HashSet<string> _collapsedBrowserGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visibleBrowserPaths = new(StringComparer.OrdinalIgnoreCase);
    private ImageBrowserGrouping _browserGrouping = ImageBrowserGrouping.Folder;
    private string? _browserRoot;
    private string? _browserStatus;
    private bool _includeSubfolders = true;
    private bool _isScanningFolder;
    private bool _browserInitialized;

    private bool CanBrowseImages => _browserRoot is not null || BrowserItems.Count > 1;

    private void InitializeImageBrowser()
    {
        _browserInitialized = true;
        UpdateBrowserControls();
    }

    private CancellationTokenSource BeginBrowserDiscovery()
    {
        _browserDiscoveryCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _browserDiscoveryCancellation = cancellation;
        // An old render must not replace the newly requested collection while
        // directory discovery is still in flight.
        _loadCancellation?.Cancel();
        _loadGeneration++;
        ViewModel.IsLoading = false;
        return cancellation;
    }

    private bool IsCurrentBrowserDiscovery(CancellationTokenSource cancellation) =>
        !_disposed && ReferenceEquals(_browserDiscoveryCancellation, cancellation)
        && !cancellation.IsCancellationRequested;

    private void CancelBrowserDiscovery()
    {
        _browserDiscoveryCancellation?.Cancel();
        _browserDiscoveryCancellation = null;
        _isScanningFolder = false;
    }

    private void FinishBrowserDiscovery(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_browserDiscoveryCancellation, cancellation))
        {
            if (cancellation.IsCancellationRequested) _browserStatus = BrowserScanCanceledMessage;
            _browserDiscoveryCancellation = null;
            _isScanningFolder = false;
            if (!_disposed)
            {
                UpdateNavigationState();
                UpdateVisualState();
            }
        }
        cancellation.Dispose();
    }

    internal Task OpenFolderAsync(string folderPath) =>
        ScanBrowserFolderAsync(folderPath, _currentPath);

    private async Task ScanBrowserFolderAsync(
        string folderPath, string? preferredPath, bool preserveCurrentImage = false)
    {
        if (_disposed) return;
        var cancellation = BeginBrowserDiscovery();
        _isScanningFolder = true;
        _browserStatus = "Scanning folder…";
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            SetCollection([], preferredPath, root, cancelDiscovery: false);
            ImageDirectoryScanResult result = await ImageFileService.BrowseImagesAsync(
                root, _includeSubfolders, cancellation.Token);
            if (!IsCurrentBrowserDiscovery(cancellation)) return;

            string? selected = result.Paths.FirstOrDefault(path =>
                string.Equals(path, preferredPath, StringComparison.OrdinalIgnoreCase));
            _browserStatus = DescribeBrowserScan(result);
            SetCollection(result.Paths, selected, result.RootPath, cancelDiscovery: false);
            // With no retained selection, start at the first displayed item,
            // not the scanner's independent filesystem ordering.
            selected = _selectedIndex >= 0 ? _imagePaths[_selectedIndex] : null;
            // End the scan before rendering: navigation and grouping are usable
            // immediately, and LoadImageAsync has its own supersession token.
            _isScanningFolder = false;
            UpdateVisualState();
            UpdateNavigationState();
            bool currentImageIsReady = preserveCurrentImage && _bitmap is not null && _currentMetadata is not null
                && string.Equals(selected, _currentPath, StringComparison.OrdinalIgnoreCase);
            if (selected is not null && !currentImageIsReady)
            {
                await LoadImageAsync(selected);
            }
            else
            {
                ViewModel.ErrorMessage = null;
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && ReferenceEquals(_browserDiscoveryCancellation, cancellation))
            {
                _browserStatus = BrowserScanCanceledMessage;
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentBrowserDiscovery(cancellation))
            {
                _browserStatus = "Unable to read this folder.";
                ViewModel.FailLoading(DescribeException(exception));
            }
        }
        finally
        {
            FinishBrowserDiscovery(cancellation);
        }
    }

    private static string DescribeBrowserScan(ImageDirectoryScanResult result)
    {
        string status = result.Paths.Count == 0
            ? "No supported images in this view."
            : $"{result.Paths.Count:N0} image{(result.Paths.Count == 1 ? string.Empty : "s")}";
        if (result.SkippedReparseFolderCount > 0)
        {
            status += $" · {result.SkippedReparseFolderCount:N0} linked folders skipped";
        }
        if (result.InaccessibleFolderCount > 0)
        {
            status += $" · {result.InaccessibleFolderCount:N0} unreadable folders skipped";
        }
        return status;
    }

    private void SetCollection(
        IReadOnlyList<string> paths,
        string? selectedPath,
        string? folderRoot = null,
        bool cancelDiscovery = true)
    {
        if (cancelDiscovery) CancelBrowserDiscovery();
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation = null;
        if (!string.Equals(_browserRoot, folderRoot, StringComparison.OrdinalIgnoreCase))
        {
            _collapsedBrowserGroups.Clear();
        }
        _browserRoot = folderRoot;
        if (folderRoot is null) _browserStatus = null;
        _imagePaths.Clear();
        _imagePaths.AddRange(paths);
        // A new collection gets fresh labels; display-only regrouping below
        // reuses these exact view models and their decoded thumbnails.
        _syncingBrowserSelection = true;
        try
        {
            ImageBrowserList.ItemsSource = null;
            BrowserItems.Clear();
        }
        finally
        {
            _syncingBrowserSelection = false;
        }
        RebuildBrowserGroups(selectedPath);
        _isBrowserOpen = CanBrowseImages;
        UpdateNavigationState();
        UpdateVisualState();
    }

    private void RebuildBrowserGroups(string? selectedPath)
    {
        ImageBrowserGroupingResult layout = ImageBrowserGroupingModel.Create(
            _imagePaths, _browserRoot, _browserGrouping);
        var existing = BrowserItems.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        _thumbnailCancellation?.Cancel();
        _syncingBrowserSelection = true;
        try
        {
            ImageBrowserList.ItemsSource = null;
            _imagePaths.Clear();
            _imagePaths.AddRange(layout.OrderedPaths);
            BrowserItems.Clear();
            foreach (string path in _imagePaths)
            {
                BrowserItems.Add(existing.TryGetValue(path, out ImageBrowserItemViewModel? item)
                    ? item : new ImageBrowserItemViewModel(path, _browserRoot));
            }
            _selectedIndex = _imagePaths.FindIndex(path =>
                string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (_selectedIndex < 0 && _imagePaths.Count > 0) _selectedIndex = 0;

            if (_browserGrouping == ImageBrowserGrouping.None)
            {
                _browserGroups = [];
                _browserViewSource = new CollectionViewSource { Source = BrowserItems };
            }
            else
            {
                var byPath = BrowserItems.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
                _browserGroups = layout.Groups.Select(group =>
                {
                    string key = $"{_browserGrouping}:{group.Key}";
                    return new ImageBrowserGroupViewModel(key, group.Label, group.Paths.Select(path => byPath[path]),
                        !_collapsedBrowserGroups.Contains(key));
                }).ToArray();
                _browserViewSource = new CollectionViewSource { IsSourceGrouped = true, Source = _browserGroups };
            }
            RefreshBrowserVisibility();
            ImageBrowserList.ItemsSource = _browserViewSource.View;
            ImageBrowserList.SelectedItem = _selectedIndex >= 0 && _visibleBrowserPaths.Contains(_imagePaths[_selectedIndex])
                ? BrowserItems[_selectedIndex] : null;
        }
        finally
        {
            _syncingBrowserSelection = false;
        }
    }

    private void BrowserGrouping_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_browserInitialized || _disposed) return;
        string? selected = _selectedIndex >= 0 && _selectedIndex < _imagePaths.Count
            ? _imagePaths[_selectedIndex] : null;
        _browserGrouping = BrowserGroupingBox.SelectedIndex switch
        {
            1 => ImageBrowserGrouping.Filter,
            2 => ImageBrowserGrouping.FileType,
            3 => ImageBrowserGrouping.None,
            _ => ImageBrowserGrouping.Folder,
        };
        RebuildBrowserGroups(selected);
        UpdateNavigationState();
        UpdateVisualState();
        _ = PrefetchThumbnailsAroundSelectionAsync();
    }

    private void BrowserGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || sender is not Button { Tag: ImageBrowserGroupViewModel group }
            || !_browserGroups.Contains(group)) return;
        _syncingBrowserSelection = true;
        try
        {
            // Collection changes can synchronously clear ListView selection.
            // Keep the document/anchor selected even when its card is hidden.
            group.IsExpanded = !group.IsExpanded;
            if (group.IsExpanded) _collapsedBrowserGroups.Remove(group.Key);
            else _collapsedBrowserGroups.Add(group.Key);
            RefreshBrowserVisibility();
        }
        finally
        {
            _syncingBrowserSelection = false;
        }
        UpdateNavigationState();
        _ = PrefetchThumbnailsAroundSelectionAsync();
    }

    private void RefreshBrowserVisibility()
    {
        _visibleBrowserPaths.Clear();
        _visibleBrowserPaths.UnionWith(_browserGrouping == ImageBrowserGrouping.None
            ? _imagePaths : _browserGroups.SelectMany(group => group).Select(item => item.Path));
    }

    private int FindBrowserNavigationIndex(int direction) => ImageBrowserGroupingModel.FindAdjacentVisibleIndex(
        _imagePaths, _visibleBrowserPaths, _selectedIndex, direction);

    private async void BrowserSubfolders_Changed(object sender, RoutedEventArgs e)
    {
        if (!_browserInitialized || _disposed) return;
        _includeSubfolders = BrowserSubfoldersCheckBox.IsChecked == true;
        if (_browserRoot is not null)
        {
            await ScanBrowserFolderAsync(_browserRoot, _currentPath, preserveCurrentImage: true);
        }
    }

    private void BrowserCancelScan_Click(object sender, RoutedEventArgs e) =>
        _browserDiscoveryCancellation?.Cancel();

    private void UpdateBrowserControls()
    {
        if (!_browserInitialized) return;
        string? root = _browserRoot ?? (_imagePaths.Count > 0 ? Path.GetDirectoryName(_imagePaths[0]) : null);
        BrowserRootText.Text = root ?? string.Empty;
        ToolTipService.SetToolTip(BrowserRootText, root);
        BrowserSubfoldersCheckBox.Visibility = _browserRoot is not null ? Visibility.Visible : Visibility.Collapsed;
        BrowserGroupingHelpText.Text = _browserGrouping == ImageBrowserGrouping.Filter
            ? "Filters detected from image filenames. Unrecognized files appear under Other."
            : _browserGrouping == ImageBrowserGrouping.None ? string.Empty : "Select a group header to collapse or expand it.";
        BrowserStatusText.Text = _browserStatus ?? $"{BrowserItems.Count:N0} images";
        BrowserScanProgress.IsActive = _isScanningFolder;
        BrowserScanProgress.Visibility = _isScanningFolder ? Visibility.Visible : Visibility.Collapsed;
        BrowserCancelScanButton.Visibility = _isScanningFolder ? Visibility.Visible : Visibility.Collapsed;
        ImageBrowserList.IsEnabled = !_isScanningFolder;
    }

    // The scanned collection defines stacking scope. Grouping and collapse are
    // presentation only; they must not silently withhold tree inputs.
    private IEnumerable<string> GetStackableBrowserPaths() =>
        _imagePaths.Where(ImageFileService.IsStackableImage);
}
