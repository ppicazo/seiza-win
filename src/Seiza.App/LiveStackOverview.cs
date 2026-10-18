using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Seiza.App.Models;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Seiza.App;

/// <summary>Reuses the coordinators' bounded previews; no extra native rendering jobs.</summary>
public sealed class LiveStackOverview : Grid, IDisposable
{
    private readonly Dictionary<string, FilterCard> _cards = new(StringComparer.Ordinal);
    private readonly TextBlock _empty = new()
    {
        Text = "Waiting for filters. Each stack will appear here as complete light frames are discovered.",
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Colors.White),
        Margin = new Thickness(16),
    };
    private int _columns;
    internal Action<string>? OpenFilter { get; set; }

    public LiveStackOverview()
    {
        ColumnSpacing = RowSpacing = 12;
        SizeChanged += (_, _) => ArrangeCards();
    }

    internal void SetItems(IReadOnlyList<LiveStackOverviewItem> items, bool enabled)
    {
        var keys = items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string key in _cards.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            FilterCard card = _cards[key];
            Children.Remove(card);
            card.Dispose();
            _cards.Remove(key);
        }
        Children.Remove(_empty);
        foreach (LiveStackOverviewItem item in items)
        {
            if (!_cards.TryGetValue(item.Key, out FilterCard? card))
            {
                card = new FilterCard();
                string key = item.Key;
                card.Click += (_, _) => OpenFilter?.Invoke(key);
                _cards.Add(key, card);
                Children.Add(card);
            }
            card.Update(item, enabled);
        }
        if (items.Count == 0) Children.Add(_empty);
        ArrangeCards(items.Select(item => item.Key).ToArray());
    }

    private void ArrangeCards(string[]? orderedKeys = null)
    {
        orderedKeys ??= _cards.Keys.OrderBy(key => Grid.GetRow(_cards[key]))
            .ThenBy(key => Grid.GetColumn(_cards[key])).ToArray();
        int columns = LiveStackOverviewItem.ColumnsFor(ActualWidth, _cards.Count);
        if (columns != _columns)
        {
            _columns = columns;
            ColumnDefinitions.Clear();
            for (int i = 0; i < columns; i++) ColumnDefinitions.Add(new ColumnDefinition());
        }
        int rows = Math.Max(1, (orderedKeys.Length + columns - 1) / columns);
        while (RowDefinitions.Count < rows) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (RowDefinitions.Count > rows) RowDefinitions.RemoveAt(RowDefinitions.Count - 1);
        for (int i = 0; i < orderedKeys.Length; i++)
        {
            SetRow(_cards[orderedKeys[i]], i / columns);
            SetColumn(_cards[orderedKeys[i]], i % columns);
        }
    }

    internal void Clear()
    {
        foreach (FilterCard card in _cards.Values) card.Dispose();
        _cards.Clear();
        Children.Clear();
    }

    public void Dispose()
    {
        Clear();
        OpenFilter = null;
        GC.SuppressFinalize(this);
    }

    private sealed class FilterCard : Button, IDisposable
    {
        private readonly CanvasControl _canvas = new() { Height = 170, ClearColor = Colors.Black };
        private readonly TextBlock _title = new() { FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _state = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _counts = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _placeholder = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12),
        };
        private RenderedImageData? _preview, _uploaded;
        private CanvasBitmap? _bitmap;
        private bool _disposed;

        public FilterCard()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Top;
            Padding = new Thickness(12);
            var image = new Grid();
            image.Children.Add(_canvas);
            image.Children.Add(_placeholder);
            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(_title);
            SetColumn(_state, 1);
            header.Children.Add(_state);
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(header);
            content.Children.Add(image);
            content.Children.Add(_counts);
            content.Children.Add(_message);
            Content = content;
            _canvas.Draw += Draw;
            _canvas.CreateResources += (_, _) => ReleaseBitmap();
            Unloaded += (_, _) => ReleaseBitmap();
        }

        public void Update(LiveStackOverviewItem item, bool enabled)
        {
            IsEnabled = enabled;
            _title.Text = item.Name;
            _state.Text = item.State;
            _counts.Text = item.Counts;
            _message.Text = item.Message;
            ToolTipService.SetToolTip(this, $"{item.Name}: {item.Message}\nSelect to view this filter.");
            AutomationProperties.SetName(this, $"{item.Name}: {item.State}. {item.Counts}. {item.Message}. View filter.");
            if (!ReferenceEquals(_preview, item.Preview))
            {
                _preview = item.Preview;
                _canvas.Invalidate();
            }
            _placeholder.Text = item.Placeholder;
            _placeholder.Visibility = _bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_disposed) return;
            if (_bitmap is null || !ReferenceEquals(_uploaded, _preview))
            {
                ReleaseBitmap();
                if (_preview is null) return;
                try
                {
                    _bitmap = CanvasBitmap.CreateFromBytes(sender, _preview.Bgra, _preview.Width, _preview.Height,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                    _uploaded = _preview;
                    _placeholder.Visibility = Visibility.Collapsed;
                }
                catch (Exception)
                {
                    _placeholder.Text = "Preview unavailable; select this filter for details.";
                    _placeholder.Visibility = Visibility.Visible;
                    return;
                }
            }
            double scale = Math.Min(sender.ActualWidth / _bitmap.SizeInPixels.Width, sender.ActualHeight / _bitmap.SizeInPixels.Height);
            if (!double.IsFinite(scale) || scale <= 0) return;
            double width = _bitmap.SizeInPixels.Width * scale, height = _bitmap.SizeInPixels.Height * scale;
            args.DrawingSession.DrawImage(_bitmap,
                new Rect((sender.ActualWidth - width) / 2, (sender.ActualHeight - height) / 2, width, height),
                _bitmap.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
        }

        private void ReleaseBitmap()
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _uploaded = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseBitmap();
            _preview = null;
            _canvas.Draw -= Draw;
            _canvas.RemoveFromVisualTree();
            GC.SuppressFinalize(this);
        }
    }
}
