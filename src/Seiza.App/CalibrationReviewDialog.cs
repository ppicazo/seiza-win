using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Seiza.App.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Seiza.App;

internal static class CalibrationReviewDialog
{
    public static ContentDialog Create(XamlRoot root, IReadOnlyList<string> warnings,
        IReadOnlyList<FlatQualityReview> reviews, string continueText)
    {
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(Text("Review the findings, then continue with the same selection or cancel to revise your calibration library. No frames have been excluded by this review."));
        if (warnings.Count > 0)
        {
            content.Children.Add(Heading("Preparation warnings"));
            foreach (string warning in warnings) content.Children.Add(Text(warning));
        }
        foreach (var review in reviews)
        {
            content.Children.Add(Heading(review.GroupTitle ?? "Selected flats"));
            content.Children.Add(Text(review.Summary));
            var flagged = review.Report.Frames.Select((verdict, index) => (verdict, index))
                .Where(item => item.verdict.Grade != "ok").ToArray();
            var frames = new StackPanel { Spacing = 8 };
            void AddFrame(FlatVerdict verdict, int index)
            {
                var card = new StackPanel { Spacing = 4 };
                card.Children.Add(Heading($"{Path.GetFileName(review.Paths[index])} — {verdict.Grade}"));
                card.Children.Add(Text(review.Paths[index]));
                foreach (var finding in verdict.Findings) card.Children.Add(Text(finding.Description));
                frames.Children.Add(new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1),
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Child = card });
            }
            foreach (var item in flagged.Take(10)) AddFrame(item.verdict, item.index);
            content.Children.Add(frames);
            if (flagged.Length > 10)
            {
                var more = new Button { Content = $"Show all {flagged.Length} flagged frames" };
                more.Click += (_, _) => { foreach (var item in flagged.Skip(10)) AddFrame(item.verdict, item.index); more.Visibility = Visibility.Collapsed; };
                content.Children.Add(more);
            }
            content.Children.Add(Heading($"Combined-master prediction — {review.Report.Master.Grade}"));
            content.Children.Add(Text("Native screening predicts a master made from this entire selected set; this is not a measurement of the saved master file."));
            foreach (var finding in review.Report.Master.Findings) content.Children.Add(Text(finding.Description));
            var dust = new StackPanel { Spacing = 8 };
            dust.Children.Add(Text(review.DustSummary));
            string detections = string.Join(", ", review.Measurements.SelectMany(m => m.Features)
                .GroupBy(f => f.Shape).Select(group => $"{group.Key}: {group.Count()}"));
            dust.Children.Add(Text("Detections across frames (not unique physical particles): " + (detections.Length == 0 ? "none" : detections) +
                $". Vignetting regions, counted separately: {review.Measurements.Sum(m => m.Vignetting.Length)}."));
            foreach (var item in review.Measurements.SelectMany((m, i) => m.Features.Select(f => (Feature: f, Index: i)))
                .Where(item => item.Feature.DistanceMm is not null).OrderByDescending(item => item.Feature.Depth).Take(8))
            {
                var f = item.Feature;
                dust.Children.Add(Text($"{Path.GetFileName(review.Paths[item.Index])}: {f.Shape} at ({f.X:F1}, {f.Y:F1}), radius {f.Radius:F1} px, depth {f.Depth:P2}; approximately {f.DistanceMm:F2} mm in front of the sensor."));
            }
            dust.Children.Add(Text("Complete geometry, illumination grids, values and limits are included in Copy Report. These measurements do not determine which flat session matches a light."));
            content.Children.Add(new Expander { Header = "Dust and vignetting details", Content = dust, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        var copy = new Button { Content = "Copy Report" };
        var copied = Text("");
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText("Calibration Review\nPreparation warnings:\n" + string.Join("\n", warnings) + "\n\n" +
                    string.Join("\n\n", reviews.Select(review => review.CompleteReport())));
                Clipboard.SetContent(data);
                copied.Text = "Complete report copied.";
            }
            catch (Exception error) { copied.Text = "Could not copy report: " + error.Message; }
        };
        content.Children.Add(copy); content.Children.Add(copied);
        return new ContentDialog { XamlRoot = root, Title = "Calibration Review",
            Content = new ScrollViewer { MaxHeight = 480, Content = content }, PrimaryButtonText = continueText,
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
    }
    private static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static TextBlock Heading(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
}
