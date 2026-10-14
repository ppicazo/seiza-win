namespace Seiza.App.Models;

internal sealed record ParallaxDustEditResult(float[] Opacity, int Width, int Height);
internal readonly record struct ParallaxDustPoint(double X, double Y);
internal enum ParallaxDustBrushMode { Opaque, Clear }

// A private, bounded draft. Neither the input map nor a native scene is mutated;
// an independent result is handed to the caller only after the dialog applies.
internal sealed class ParallaxDustEditorModel
{
    internal const int MaximumSide = 768;
    internal const int HistoryLimit = 20;
    private readonly float[] _initial;
    private float[] _opacity;
    private readonly List<float[]> _undo = [], _redo = [];
    private float[]? _strokeBefore;
    private ParallaxDustPoint _last;
    private double _radius, _strength;
    private ParallaxDustBrushMode _mode;

    internal int Width { get; }
    internal int Height { get; }
    internal bool IsStrokeActive => _strokeBefore is not null;
    internal bool CanUndo => _undo.Count > 0;
    internal bool CanRedo => _redo.Count > 0;
    internal bool HasChanges => !_opacity.AsSpan().SequenceEqual(_initial);
    internal ReadOnlySpan<float> Opacity => _opacity;

    internal ParallaxDustEditorModel(float[] opacity, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(opacity);
        if (width < 1 || height < 1 || width > MaximumSide || height > MaximumSide || opacity.Length != checked(width * height))
            throw new ArgumentException($"The dust draft must be at most {MaximumSide} pixels per side and contain one opacity per pixel.", nameof(opacity));
        if (opacity.Any(value => !float.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentException("Dust opacity must be finite and between zero and one.", nameof(opacity));
        Width = width; Height = height;
        _initial = opacity.ToArray(); _opacity = opacity.ToArray();
    }

    internal static bool TryMapUniform(double x, double y, double viewportWidth, double viewportHeight,
        int width, int height, out ParallaxDustPoint point)
    {
        point = default;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 || viewportHeight <= 0 || width < 1 || height < 1) return false;
        double scale = Math.Min(viewportWidth / width, viewportHeight / height);
        if (!double.IsFinite(scale) || scale <= 0) return false;
        double left = (viewportWidth - width * scale) / 2, top = (viewportHeight - height * scale) / 2;
        if (x < left || y < top || x > left + width * scale || y > top + height * scale) return false;
        point = new(Math.Clamp((x - left) / scale, 0, width - 1), Math.Clamp((y - top) / scale, 0, height - 1));
        return true;
    }

    internal bool BeginStroke(ParallaxDustPoint point, double diameter, double strength, ParallaxDustBrushMode mode)
    {
        if (IsStrokeActive) throw new InvalidOperationException("Finish the current brush stroke first.");
        ValidatePoint(point);
        if (!double.IsFinite(diameter) || diameter <= 0 || diameter > MaximumSide * 2 ||
            !double.IsFinite(strength) || strength is < 0 or > 1 || !Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(diameter), "The brush needs a finite positive diameter and a strength from zero to one.");
        _strokeBefore = _opacity.ToArray(); _radius = diameter / 2; _strength = strength; _mode = mode;
        _last = BoundPoint(point);
        return Dab(_last);
    }

    internal bool ContinueStroke(ParallaxDustPoint point)
    {
        if (!IsStrokeActive) return false;
        ValidatePoint(point); point = BoundPoint(point);
        double dx = point.X - _last.X, dy = point.Y - _last.Y, distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 1e-9) return false;
        int steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(0.5, _radius / 4)));
        bool changed = false;
        for (int step = 1; step <= steps; step++)
        {
            double fraction = (double)step / steps;
            changed |= Dab(new(_last.X + dx * fraction, _last.Y + dy * fraction));
        }
        _last = point; return changed;
    }

    internal void EndStroke()
    {
        if (_strokeBefore is not { } before) return;
        _strokeBefore = null;
        if (!before.AsSpan().SequenceEqual(_opacity)) Remember(before);
    }

    internal void CancelStroke()
    {
        if (_strokeBefore is not { } before) return;
        _opacity = before; _strokeBefore = null;
    }

    internal void Undo()
    {
        EndStroke();
        if (_undo.Count == 0) return;
        _redo.Add(_opacity); _opacity = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
    }
    internal void Redo()
    {
        EndStroke();
        if (_redo.Count == 0) return;
        _undo.Add(_opacity); _opacity = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
    }
    internal void Reset()
    {
        EndStroke();
        if (!HasChanges) return;
        Remember(_opacity); _opacity = _initial.ToArray();
    }

    internal ParallaxDustEditResult CreateResult()
    {
        EndStroke();
        return new(_opacity.ToArray(), Width, Height);
    }

    private void Remember(float[] before)
    {
        _undo.Add(before); if (_undo.Count > HistoryLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }
    private static void ValidatePoint(ParallaxDustPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentOutOfRangeException(nameof(point), "A brush point must be finite.");
    }
    private ParallaxDustPoint BoundPoint(ParallaxDustPoint point) => new(
        Math.Clamp(point.X, -_radius, Width - 1 + _radius), Math.Clamp(point.Y, -_radius, Height - 1 + _radius));

    private bool Dab(ParallaxDustPoint point)
    {
        int minX = (int)Math.Max(0, Math.Floor(point.X - _radius)), minY = (int)Math.Max(0, Math.Floor(point.Y - _radius));
        int maxX = (int)Math.Min(Width - 1, Math.Ceiling(point.X + _radius)), maxY = (int)Math.Min(Height - 1, Math.Ceiling(point.Y + _radius));
        double target = _mode == ParallaxDustBrushMode.Opaque ? 1 : 0;
        bool changed = false;
        for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
        {
            double dx = x - point.X, dy = y - point.Y;
            double weight = Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy) / _radius) * _strength;
            int index = y * Width + x;
            float value = (float)Math.Clamp(_opacity[index] + (target - _opacity[index]) * weight, 0, 1);
            changed |= value != _opacity[index]; _opacity[index] = value;
        }
        return changed;
    }
}
