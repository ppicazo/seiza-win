using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Seiza.App.ViewModels;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class ImageBrowserGroupViewModel : ObservableCollection<ImageBrowserItemViewModel>
{
    private readonly ImageBrowserItemViewModel[] _allItems;
    private bool _isExpanded;

    public ImageBrowserGroupViewModel(
        string key, string label, IEnumerable<ImageBrowserItemViewModel> items, bool isExpanded = true)
    {
        Key = key;
        Label = label;
        _allItems = items.ToArray();
        _isExpanded = isExpanded;
        if (isExpanded)
        {
            foreach (ImageBrowserItemViewModel item in _allItems) Add(item);
        }
    }

    public string Key { get; }
    public string Label { get; }
    public string ItemCountText => $"({_allItems.Length:N0})";
    public string HeaderText => $"{Label} {ItemCountText}";
    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";
    public string ToggleActionText => $"{(IsExpanded ? "Collapse" : "Expand")} {HeaderText}";

    internal bool ContainsImage(ImageBrowserItemViewModel item) => Array.IndexOf(_allItems, item) >= 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            if (value)
            {
                foreach (ImageBrowserItemViewModel item in _allItems) Add(item);
            }
            else Clear();
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsExpanded)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(ChevronGlyph)));
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(ToggleActionText)));
        }
    }
}
