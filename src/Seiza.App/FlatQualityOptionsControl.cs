using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;

namespace Seiza.App;

public sealed class FlatQualityOptionsControl : UserControl
{
    private readonly CheckBox _enabled = new() { Content = "Review flat quality before stacking" };
    private readonly NumberBox _scale = new() { Header = "Saturation level (raw sample units)", Value = 65535 };
    private readonly CheckBox _optics = new() { Content = "Estimate dust distance" };
    private readonly NumberBox _focal = new() { Header = "Focal ratio (f/)", Value = 5 };
    private readonly NumberBox _pixel = new() { Header = "Pixel size after camera binning (µm)", Value = 3.76 };
    private readonly NumberBox _obstruction = new() { Header = "Obstruction diameter (% of aperture)", Value = 0 };

    public FlatQualityOptionsControl()
    {
        var details = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        details.Children.Add(_scale);
        details.Children.Add(new TextBlock { Text = "Use the file's actual units: commonly 65535 for 16-bit ADU or 1 for normalized samples. Review does not remove or replace frames. In multi-filter sessions, each new filter is reviewed before ingestion; restored checkpoints keep their existing calibration.", TextWrapping = TextWrapping.Wrap });
        details.Children.Add(_optics);
        var geometry = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        geometry.Children.Add(_focal); geometry.Children.Add(_pixel); geometry.Children.Add(_obstruction);
        geometry.Children.Add(new TextBlock { Text = "For 3.76 µm pixels recorded with 2×2 binning, enter 7.52 µm. Distances are estimates, not identification of a dirty surface.", TextWrapping = TextWrapping.Wrap });
        details.Children.Add(geometry);
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(_enabled); root.Children.Add(details);
        Content = root;
        _enabled.Checked += (_, _) => details.Visibility = Visibility.Visible;
        _enabled.Unchecked += (_, _) => details.Visibility = Visibility.Collapsed;
        _optics.Checked += (_, _) => geometry.Visibility = Visibility.Visible;
        _optics.Unchecked += (_, _) => geometry.Visibility = Visibility.Collapsed;
    }

    internal FlatQualityOptions ReadOptions() => new()
    {
        Enabled = _enabled.IsChecked == true, FullScale = _scale.Value,
        UseOptics = _optics.IsChecked == true, FocalRatio = _focal.Value,
        PixelSizeUm = _pixel.Value, Obstruction = _obstruction.Value / 100,
    };
}
