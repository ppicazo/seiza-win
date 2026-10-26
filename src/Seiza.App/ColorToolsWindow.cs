using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Seiza.App.Models;
using Seiza.App.Services;
using Windows.Graphics;

namespace Seiza.App;

/// <summary>Native controls for the published display and linear color operations.</summary>
public sealed class ColorToolsWindow : Window, IDisposable
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly ComboBox _tool = new() { Header = "Color workflow", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _inputs = new() { Spacing = 14 };
    private readonly ContentControl _inputHost = new();
    private readonly StackPanel _narrowband = new() { Spacing = 12 };
    private readonly StackPanel _continuum = new() { Spacing = 12 };
    private readonly StackPanel _stars = new() { Spacing = 12 };
    private readonly TextBox _ha = PathField("Aligned H-alpha mono stack");
    private readonly TextBox _oiii = PathField("Aligned OIII mono stack");
    private readonly TextBox _sii = PathField("Aligned SII mono stack (SHO)");
    private readonly ComboBox _palette = new() { Header = "Palette" };
    private readonly CheckBox _displayChannels = new() { Content = "The narrowband channels are already stretched display values in [0, 1]" };
    private readonly CheckBox _balance = new() { Content = "Balance OIII / SII midtones against H-alpha", IsChecked = true };
    private readonly NumberBox _shadow = Number("Shadow protection (0–1)", 1, 0, 1, .1);
    private readonly NumberBox _oiiiBoost = Number("OIII boost", 1, .01, 100, .1);
    private readonly NumberBox _siiBoost = Number("SII boost", 1, .01, 100, .1);
    private readonly CheckBox _excludeStars = new() { Content = "Exclude detected stars from balance statistics (5 px radius)" };
    private readonly TextBox _structure = PathField("Optional aligned display RGB broadband structure");
    private readonly NumberBox _structureAmount = Number("Broadband structure mix (0 keeps narrowband)", 0, 0, 1, .1);
    private readonly TextBox _line = PathField("Aligned linear narrowband mono image");
    private readonly NumberBox _lineSaturation = Number("Narrowband physical saturation (0 = unknown)", 0, 0, double.MaxValue, 1000);
    private readonly TextBox _lineMask = PathField("Optional producer clipping mask: mono 0 / 1");
    private readonly ReferenceRow[] _references = [new(1), new(2), new(3)];
    private readonly ComboBox _fitMode = new() { Header = "Continuum coefficients" };
    private readonly CheckBox _psf = new() { Content = "Broaden copies to a common Gaussian PSF before fitting / subtraction" };
    private readonly TextBox _original = PathField("Aligned LINEAR original RGB image");
    private readonly TextBox _starless = PathField("Aligned LINEAR starless RGB image, same physical scale");
    private readonly TextBox _preparedStars = PathField("Prepared DISPLAY RGB stars-only layer");
    private readonly TextBox _nebula = PathField("Aligned RGB nebula image for restoration");
    private readonly CheckBox _nebulaDisplay = new() { Content = "The nebula is already stretched display data in [0, 1]", IsChecked = true };
    private readonly NumberBox _starAmount = Number("Independent RGB star stretch amount (0–8)", 5, 0, 8, .25);
    private readonly NumberBox _starReduction = Number("Linked star reduction (0 preserves stars, 1 strongest)", 0, 0, 1, .1);
    private readonly NumberBox _median = Number("Linked Auto-MTF target median for linear display inputs", .2, .01, .99, .01);
    private readonly NumberBox _green = Number("Reduce excess green (0–1; applied before restoring stars)", 0, 0, 1, .1);
    private readonly CheckBox _aligned = new() { Content = "All inputs share one registered pixel grid, crop and orientation" };
    private readonly CheckBox _linear = new() { Content = "The sources named LINEAR are unstretched and retain their original common photometric scale" };
    private readonly InfoBar _status = new() { IsOpen = true, IsClosable = false, Message = "Choose prepared FITS / XISF images. Sources are never modified." };
    private readonly TextBox _report = new() { Header = "Last fit / processing report", IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 320 };
    private readonly Button _analyze = new() { Content = "Analyze stars and validate fit" };
    private readonly Button _loadPlan = new() { Content = "Load continuum plan / report…" };
    private readonly Button _save = new() { Content = "Process and save image…" };
    private readonly Button _saveReport = new() { Content = "Save report / plan…", IsEnabled = false };
    private readonly Button _open = new() { Content = "Open result / preview / export…", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cancel operation", IsEnabled = false };
    private readonly ProgressRing _progress = new() { Width = 24, Height = 24 };
    private CancellationTokenSource? _operation;
    private JsonElement? _plan;
    private string? _planPath;
    private ColorToolsResult? _result;
    private ColorToolsRequest? _completedRequest;
    private bool _busy, _closed, _initializing = true;

    public ColorToolsWindow()
    {
        Title = "Narrowband, continuum and RGB stars — Seiza";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        RectInt32 workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min(900, workArea.Width), Math.Min(1020, workArea.Height)));
        foreach (string title in new[] { "Narrowband balance and HOO / SHO display", "Signed linear continuum subtraction",
            "Extract and stretch linear RGB stars", "Restore RGB stars over a nebula", "Restore an already prepared RGB stars layer" }) _tool.Items.Add(title);
        _palette.Items.Add("HOO — H-alpha / OIII / OIII"); _palette.Items.Add("SHO — SII / H-alpha / OIII"); _palette.SelectedIndex = 0;
        _fitMode.Items.Add("Automatic stellar fit — analyze and review first");
        _fitMode.Items.Add("Manual coefficients — no automatic fit / validation");
        _fitMode.Items.Add("Use reviewed / saved exact-source plan"); _fitMode.SelectedIndex = 0;
        AddPaths(_narrowband, _ha, _oiii, _sii); _narrowband.Children.Add(_palette);
        _narrowband.Children.Add(_displayChannels); _narrowband.Children.Add(_balance);
        _narrowband.Children.Add(Row(_shadow, _oiiiBoost, _siiBoost)); _narrowband.Children.Add(_excludeStars);
        _narrowband.Children.Add(Picker(_structure)); _narrowband.Children.Add(_structureAmount);
        _narrowband.Children.Add(Note("Linear channels use one linked physical range and display stretch before balance. Flat / noise-dominated channels report skipped balance. These outputs are display images, not photometric line flux."));
        _continuum.Children.Add(Picker(_line)); _continuum.Children.Add(_lineSaturation); _continuum.Children.Add(Picker(_lineMask));
        var additionalReferences = new StackPanel { Spacing = 12 };
        for (int index = 0; index < _references.Length; index++)
        {
            ReferenceRow reference = _references[index];
            var row = new StackPanel { Spacing = 8 };
            row.Children.Add(Picker(reference.Path)); row.Children.Add(Row(reference.Saturation, reference.Coefficient));
            row.Children.Add(new Expander { Header = "Producer clipping mask (optional)", Content = Picker(reference.Mask), HorizontalAlignment = HorizontalAlignment.Stretch });
            if (index == 0) _continuum.Children.Add(row); else additionalReferences.Children.Add(row);
        }
        _continuum.Children.Add(new Expander { Header = "Additional continuum references (optional)", Content = additionalReferences, HorizontalAlignment = HorizontalAlignment.Stretch });
        _continuum.Children.Add(_fitMode); _continuum.Children.Add(_psf);
        _continuum.Children.Add(Note("Use one to three aligned mono broadband references. Enter each producer's physical saturation in these prepared units, or supply an exact mono 0/1 clipping mask; 0 does not estimate a ceiling from the image maximum. Automatic fits can skip on saturation, alignment, PSF or held-out profile failures. No skipped fit is replaced by manual weights. Subtraction preserves signed values and missing coverage. Gaussian PSF matching changes resolution on copies and is recorded in the plan."));
        AddPaths(_stars, _original, _starless, _preparedStars, _nebula);
        _stars.Children.Add(_nebulaDisplay); _stars.Children.Add(Row(_starAmount, _starReduction));
        _stars.Children.Add(Note("Provide the actual aligned linear original and starless RGB separation on one physical scale. This tool does not run star separation. Prepared stars layers are screened without being stretched again. Star reduction is linked across RGB; the saved scientific sources remain unchanged."));
        _inputs.Children.Add(_tool); _inputs.Children.Add(_narrowband); _inputs.Children.Add(_continuum); _inputs.Children.Add(_stars);
        _inputs.Children.Add(_median); _inputs.Children.Add(_green); _inputs.Children.Add(_aligned); _inputs.Children.Add(_linear);
        var actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(_analyze); actions.Children.Add(_loadPlan); actions.Children.Add(_save);
        var resultActions = new StackPanel { Spacing = 8 };
        resultActions.Children.Add(_open); resultActions.Children.Add(_saveReport); resultActions.Children.Add(_cancel);
        var root = new StackPanel { Spacing = 16, Padding = new Thickness(24), MaxWidth = 1000 };
        root.Children.Add(new TextBlock { Text = "Narrowband, continuum and RGB stars", FontSize = 24, TextWrapping = TextWrapping.Wrap });
        _inputHost.Content = _inputs;
        root.Children.Add(_inputHost); root.Children.Add(actions); root.Children.Add(_progress); root.Children.Add(_status);
        root.Children.Add(_report); root.Children.Add(resultActions);
        Content = new ScrollViewer { Content = root };
        root.Loaded += (_, _) => UpdateMode();
        _tool.SelectionChanged += (_, _) => { InvalidatePlan(); UpdateMode(); };
        _palette.SelectionChanged += (_, _) => UpdateMode();
        _displayChannels.Checked += (_, _) => UpdateMode(); _displayChannels.Unchecked += (_, _) => UpdateMode();
        _nebulaDisplay.Checked += (_, _) => UpdateMode(); _nebulaDisplay.Unchecked += (_, _) => UpdateMode();
        _fitMode.SelectionChanged += (_, _) => { InvalidatePlan(); UpdateMode(); };
        foreach (TextBox field in new[] { _line, _lineMask }.Concat(_references.SelectMany(reference => new[] { reference.Path, reference.Mask })))
            field.TextChanged += (_, _) => InvalidatePlan();
        foreach (NumberBox box in new[] { _lineSaturation }.Concat(_references.Select(reference => reference.Saturation)))
            box.ValueChanged += (_, _) => InvalidatePlan();
        _psf.Checked += (_, _) => InvalidatePlan(); _psf.Unchecked += (_, _) => InvalidatePlan();
        _analyze.Click += async (_, _) => await RunAsync(analyze: true);
        _save.Click += async (_, _) => await RunAsync(analyze: false);
        _loadPlan.Click += LoadPlan_Click; _saveReport.Click += SaveReport_Click;
        _open.Click += async (_, _) => { if (_result?.Output is string path) await App.OpenDocumentPathsAsync([path]); };
        _cancel.Click += (_, _) => _operation?.Cancel();
        Closed += (_, _) => Dispose();
        _tool.SelectedIndex = 0; _initializing = false; UpdateMode();
    }

    private sealed class ReferenceRow(int number)
    {
        public TextBox Path { get; } = PathField($"Continuum reference {number} — mono LINEAR{(number > 1 ? " (optional)" : "")}");
        public NumberBox Saturation { get; } = Number("Physical saturation (0 = unknown)", 0, 0, double.MaxValue, 1000);
        public NumberBox Coefficient { get; } = Number("Manual coefficient", 1, 0, 1000, .05);
        public TextBox Mask { get; } = PathField("Optional producer clipping mask: mono 0 / 1");
    }
    private static TextBox PathField(string header) => new() { Header = header };
    private static NumberBox Number(string header, double value, double min, double max, double step) => new()
    { Header = header, Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private static Grid Row(params FrameworkElement[] controls)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < controls.Length; i++)
        { grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); Grid.SetColumn(controls[i], i); grid.Children.Add(controls[i]); }
        return grid;
    }
    private void AddPaths(StackPanel panel, params TextBox[] fields) { foreach (TextBox field in fields) panel.Children.Add(Picker(field)); }
    private Grid Picker(TextBox field)
    {
        var browse = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(field); Grid.SetColumn(browse, 1); grid.Children.Add(browse);
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
                foreach (string extension in new[] { ".fits", ".fit", ".fts", ".xisf" }) picker.FileTypeFilter.Add(extension);
                var file = await picker.PickSingleFileAsync(); if (!_closed && file is not null) field.Text = file.Path;
            }
            catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        };
        return grid;
    }

    private void InvalidatePlan()
    {
        if (_initializing) return;
        _plan = null; _planPath = null;
        UpdateMode();
    }
    private void UpdateMode()
    {
        if (_initializing) return;
        var mode = (ColorTool)_tool.SelectedIndex;
        _narrowband.Visibility = mode == ColorTool.Narrowband ? Visibility.Visible : Visibility.Collapsed;
        _continuum.Visibility = mode == ColorTool.Continuum ? Visibility.Visible : Visibility.Collapsed;
        _stars.Visibility = mode is ColorTool.StretchStars or ColorTool.RestoreStars or ColorTool.RestorePreparedStars ? Visibility.Visible : Visibility.Collapsed;
        if (_sii.Parent is UIElement sulfur) sulfur.Visibility = _palette.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (TextBox field in new[] { _original, _starless })
            if (field.Parent is UIElement parent) parent.Visibility = mode == ColorTool.RestorePreparedStars ? Visibility.Collapsed : Visibility.Visible;
        if (_preparedStars.Parent is UIElement prepared) prepared.Visibility = mode == ColorTool.RestorePreparedStars ? Visibility.Visible : Visibility.Collapsed;
        if (_nebula.Parent is UIElement nebula) nebula.Visibility = mode == ColorTool.StretchStars ? Visibility.Collapsed : Visibility.Visible;
        _nebulaDisplay.Visibility = mode == ColorTool.StretchStars ? Visibility.Collapsed : Visibility.Visible;
        _starAmount.IsEnabled = mode != ColorTool.RestorePreparedStars;
        _starReduction.IsEnabled = mode != ColorTool.StretchStars;
        _median.Visibility = mode == ColorTool.Continuum ? Visibility.Collapsed : Visibility.Visible;
        _median.IsEnabled = mode == ColorTool.Narrowband ? _displayChannels.IsChecked != true :
            mode is ColorTool.RestoreStars or ColorTool.RestorePreparedStars && _nebulaDisplay.IsChecked != true;
        _linear.Visibility = (mode == ColorTool.Narrowband && _displayChannels.IsChecked == true) ||
            (mode == ColorTool.RestorePreparedStars && _nebulaDisplay.IsChecked == true) ? Visibility.Collapsed : Visibility.Visible;
        _green.Visibility = mode is ColorTool.Continuum or ColorTool.StretchStars ? Visibility.Collapsed : Visibility.Visible;
        _analyze.Visibility = _loadPlan.Visibility = mode == ColorTool.Continuum ? Visibility.Visible : Visibility.Collapsed;
        _analyze.IsEnabled = !_busy && _fitMode.SelectedIndex == 0;
        foreach (ReferenceRow reference in _references) reference.Coefficient.IsEnabled = _fitMode.SelectedIndex == 1;
        _psf.IsEnabled = _fitMode.SelectedIndex == 0;
        _save.IsEnabled = !_busy && (mode != ColorTool.Continuum || _fitMode.SelectedIndex == 1 || _plan is not null);
    }

    private ColorToolsRequest CreateRequest(string? output)
    {
        ColorTool mode = (ColorTool)_tool.SelectedIndex;
        var references = _references.Where(reference => !string.IsNullOrWhiteSpace(reference.Path.Text)).ToArray();
        static double? Saturation(NumberBox box) => box.Value == 0 ? null : box.Value;
        return new ColorToolsRequest
        {
            Tool = mode, Output = output, Aligned = _aligned.IsChecked == true, LinearConfirmed = _linear.IsChecked == true,
            Inputs = mode switch
            {
                ColorTool.Narrowband => _palette.SelectedIndex == 0 ? [_ha.Text, _oiii.Text] : [_ha.Text, _oiii.Text, _sii.Text],
                ColorTool.Continuum => new[] { _line.Text }.Concat(references.Select(reference => reference.Path.Text)).ToArray(),
                ColorTool.StretchStars => [_original.Text, _starless.Text],
                ColorTool.RestoreStars => [_original.Text, _starless.Text, _nebula.Text],
                _ => [_preparedStars.Text, _nebula.Text],
            },
            InputDisplay = _displayChannels.IsChecked == true, NebulaDisplay = _nebulaDisplay.IsChecked == true,
            Palette = _palette.SelectedIndex == 0 ? "hoo" : "sho", TargetMedian = _median.Value,
            Balance = _balance.IsChecked == true, ShadowProtection = _shadow.Value, OiiiBoost = _oiiiBoost.Value, SiiBoost = _siiBoost.Value,
            ExcludeStars = _excludeStars.IsChecked == true, GreenReduction = _green.Value,
            Structure = mode == ColorTool.Narrowband && !string.IsNullOrWhiteSpace(_structure.Text) ? _structure.Text : null,
            StructureAmount = _structureAmount.Value,
            Saturations = mode == ColorTool.Continuum ? new[] { Saturation(_lineSaturation) }.Concat(references.Select(reference => Saturation(reference.Saturation))).ToArray() : [],
            ClippingMasks = mode == ColorTool.Continuum ? new[] { _lineMask.Text }.Concat(references.Select(reference => reference.Mask.Text)).ToArray() : [],
            PsfMatching = _psf.IsChecked == true,
            ManualCoefficients = mode == ColorTool.Continuum && _fitMode.SelectedIndex == 1 ? references.Select(reference => reference.Coefficient.Value).ToArray() : null,
            Plan = mode == ColorTool.Continuum && _fitMode.SelectedIndex != 1 ? _plan : null, PlanPath = _planPath,
            StarAmount = _starAmount.Value, StarReduction = _starReduction.Value,
        };
    }

    private async Task RunAsync(bool analyze)
    {
        if (_busy) return;
        SetBusy(true);
        _operation = new CancellationTokenSource();
        try
        {
            CreateRequest(null).Validate(analyze, requireOutput: false);
            string? output = null;
            if (!analyze)
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
                { SuggestedFileName = (ColorTool)_tool.SelectedIndex == ColorTool.Continuum ? "continuum-residual" : "color-display", DefaultFileExtension = ".fits", ShowOverwritePrompt = true };
                picker.FileTypeChoices.Add("Float FITS", [".fits"]); picker.FileTypeChoices.Add("Float XISF", [".xisf"]);
                var file = await picker.PickSaveFileAsync(); if (_closed || file is null) return; output = file.Path;
            }
            var request = CreateRequest(output);
            Show(analyze ? "Measuring and validating stars. A failed fit will not subtract anything." : "Processing into a separate file. Cancellation preserves any existing output.", InfoBarSeverity.Informational);
            var result = await ColorToolsService.RunAsync(request, analyze, _operation.Token);
            if (_closed) return;
            _result = result; _completedRequest = request;
            _report.Text = JsonSerializer.Serialize(result.Report, Pretty);
            if (analyze) _plan = result.Status == "fitted" ? result.Report.GetProperty("plan").Clone() : null;
            _saveReport.IsEnabled = true; _open.IsEnabled = result.Output is not null;
            Show(result.Message, result.Status == "skipped" ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { Show("Cancelled. No output was replaced.", InfoBarSeverity.Informational); }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false); }
    }

    private async void LoadPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id); picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync(); if (_closed || file is null) return;
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file.Path));
            JsonElement plan = document.RootElement.TryGetProperty("plan", out JsonElement nested) ? nested : document.RootElement;
            if (!plan.TryGetProperty("sources", out _) || !plan.TryGetProperty("coefficients", out _))
                throw new InvalidOperationException("Choose a continuum plan or a fitted/subtraction report containing one.");
            _fitMode.SelectedIndex = 2; _plan = plan.Clone(); _planPath = file.Path;
            _report.Text = JsonSerializer.Serialize(document.RootElement, Pretty); UpdateMode();
            Show("Plan loaded. Saving validates its exact source hashes, reference order and grid again.", InfoBarSeverity.Informational);
        }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
    }
    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || _completedRequest is null || _busy) return;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            { SuggestedFileName = "color-report", DefaultFileExtension = ".json", ShowOverwritePrompt = true };
            picker.FileTypeChoices.Add("Processing report / continuum plan", [".json"]);
            var file = await picker.PickSaveFileAsync(); if (!_closed && file is not null)
                await ColorToolsService.SaveReportAsync(_result, _completedRequest, file.Path);
        }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; if (_closed) return;
        _inputHost.IsEnabled = !busy; _loadPlan.IsEnabled = !busy; _progress.IsActive = busy; _cancel.IsEnabled = busy;
        _open.IsEnabled = !busy && _result?.Output is not null; _saveReport.IsEnabled = !busy && _result is not null;
        UpdateMode();
    }
    private void Show(string message, InfoBarSeverity severity)
    { if (!_closed) { _status.Message = message; _status.Severity = severity; } }
    public void Dispose()
    {
        if (_closed) return; _closed = true; _operation?.Cancel(); GC.SuppressFinalize(this);
    }
}
