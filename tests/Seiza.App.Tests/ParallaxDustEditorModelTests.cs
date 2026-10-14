using Seiza.App.Models;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxDustEditorModelTests
{
    [Theory]
    [InlineData(100, 50, 50, 50, 50, 25)]
    [InlineData(50, 100, 50, 50, 25, 50)]
    [InlineData(100, 50, 0, 25, 0, 0)]
    [InlineData(100, 50, 100, 75, 99, 49)]
    public void UniformMappingHonorsLetterboxesAndPixelBounds(int width, int height, double x, double y, double expectedX, double expectedY)
    {
        Assert.True(ParallaxDustEditorModel.TryMapUniform(x, y, 100, 100, width, height, out var point));
        Assert.Equal(expectedX, point.X, 8); Assert.Equal(expectedY, point.Y, 8);
    }

    [Theory]
    [InlineData(20, 5)]
    [InlineData(50, 20)]
    [InlineData(50, 80)]
    [InlineData(-1, 50)]
    [InlineData(101, 50)]
    [InlineData(double.NaN, 50)]
    public void LetterboxedOrNonfinitePointsCannotPaint(double x, double y) =>
        Assert.False(ParallaxDustEditorModel.TryMapUniform(x, y, 100, 100, 100, 50, out _));

    [Fact]
    public void InvalidOrCollapsedViewportsCannotMap()
    {
        Assert.False(ParallaxDustEditorModel.TryMapUniform(0, 0, 0, 100, 100, 50, out _));
        Assert.False(ParallaxDustEditorModel.TryMapUniform(0, 0, double.PositiveInfinity, 100, 100, 50, out _));
        Assert.False(ParallaxDustEditorModel.TryMapUniform(0, 0, double.Epsilon, double.Epsilon, 768, 768, out _));
        Assert.False(ParallaxDustEditorModel.TryMapUniform(0, 0, 100, 100, 0, 50, out _));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void InvalidOpacityIsRefused(float value) =>
        Assert.Throws<ArgumentException>(() => new ParallaxDustEditorModel([value], 1, 1));

    [Fact]
    public void DraftDimensionsAndMemoryAreBounded()
    {
        Assert.Throws<ArgumentException>(() => new ParallaxDustEditorModel([], 0, 1));
        Assert.Throws<ArgumentException>(() => new ParallaxDustEditorModel(new float[4], 1, 1));
        Assert.Throws<ArgumentException>(() => new ParallaxDustEditorModel(new float[769], 769, 1));
        Assert.False(new ParallaxDustEditorModel(new float[768], 768, 1).HasChanges);
    }

    [Fact]
    public void OpaqueAndClearBrushesBlendAtTheRequestedStrengthWithSoftEdges()
    {
        var model = new ParallaxDustEditorModel(new float[9 * 9], 9, 9);
        model.BeginStroke(new(4, 4), 8, 0.5, ParallaxDustBrushMode.Opaque); model.EndStroke();
        Assert.Equal(0.5f, model.Opacity[4 * 9 + 4]);
        Assert.InRange(model.Opacity[4 * 9 + 6], 0.2499f, 0.2501f);
        Assert.Equal(0, model.Opacity[0]);
        model.BeginStroke(new(4, 4), 8, 0.5, ParallaxDustBrushMode.Clear); model.EndStroke();
        Assert.Equal(0.25f, model.Opacity[4 * 9 + 4]);
        Assert.All(model.CreateResult().Opacity, value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void DragInterpolatesWithoutGapsAndOneUndoRevertsTheWholeStroke()
    {
        var model = new ParallaxDustEditorModel(new float[32 * 8], 32, 8);
        model.BeginStroke(new(2, 4), 4, 1, ParallaxDustBrushMode.Opaque);
        model.ContinueStroke(new(29, 4)); model.EndStroke();
        for (int x = 2; x <= 29; x++) Assert.True(model.Opacity[4 * 32 + x] > 0);
        float[] painted = model.CreateResult().Opacity;
        model.Undo(); Assert.False(model.HasChanges); Assert.False(model.CanUndo); Assert.True(model.CanRedo);
        model.Redo(); Assert.Equal(painted, model.CreateResult().Opacity);
    }

    [Fact]
    public void StrokeBeyondAnEdgeStaysClippedAndFinite()
    {
        var model = new ParallaxDustEditorModel(new float[5 * 5], 5, 5);
        model.BeginStroke(new(0, 0), 8, 1, ParallaxDustBrushMode.Opaque);
        model.ContinueStroke(new(-1e100, -1e100)); model.EndStroke();
        Assert.Equal(1, model.Opacity[0]); Assert.Equal(0, model.Opacity[24]);
        Assert.All(model.CreateResult().Opacity, value => Assert.True(float.IsFinite(value) && value is >= 0 and <= 1));
    }

    [Fact]
    public void CancelledStrokeAndDiscardedDraftNeverMutateTheOpenedMapOrHistory()
    {
        float[] original = Enumerable.Repeat(0.25f, 25).ToArray();
        var model = new ParallaxDustEditorModel(original, 5, 5);
        model.BeginStroke(new(2, 2), 4, 1, ParallaxDustBrushMode.Opaque);
        Assert.True(model.HasChanges); model.CancelStroke();
        Assert.False(model.HasChanges); Assert.False(model.CanUndo);
        Assert.All(original, value => Assert.Equal(0.25f, value));
        model.BeginStroke(new(2, 2), 4, 1, ParallaxDustBrushMode.Clear); model.EndStroke();
        ParallaxDustEditResult result = model.CreateResult(); result.Opacity[0] = 1;
        Assert.Equal(0.25f, model.Opacity[0]);
        Assert.All(original, value => Assert.Equal(0.25f, value));
    }

    [Fact]
    public void ResetRestoresTheOpenedMapAndIsItselfUndoable()
    {
        var model = new ParallaxDustEditorModel(Enumerable.Repeat(0.2f, 25).ToArray(), 5, 5);
        model.BeginStroke(new(2, 2), 4, 1, ParallaxDustBrushMode.Opaque); model.EndStroke();
        float[] painted = model.CreateResult().Opacity;
        model.Reset(); Assert.False(model.HasChanges);
        model.Undo(); Assert.Equal(painted, model.CreateResult().Opacity);
        model.Redo(); Assert.False(model.HasChanges);
    }

    [Fact]
    public void NewStrokeDropsRedoButNoopStrokesDoNot()
    {
        var model = new ParallaxDustEditorModel(new float[25], 5, 5);
        model.BeginStroke(new(2, 2), 4, 1, ParallaxDustBrushMode.Opaque); model.EndStroke(); model.Undo();
        model.BeginStroke(new(2, 2), 4, 0, ParallaxDustBrushMode.Opaque); model.EndStroke(); Assert.True(model.CanRedo);
        model.BeginStroke(new(1, 1), 2, 1, ParallaxDustBrushMode.Opaque); model.EndStroke(); Assert.False(model.CanRedo);
    }

    [Fact]
    public void HistoryRetainsAtMostTwentyCompleteStrokes()
    {
        var model = new ParallaxDustEditorModel(new float[32], 32, 1);
        for (int x = 0; x < 21; x++) { model.BeginStroke(new(x, 0), 1, 1, ParallaxDustBrushMode.Opaque); model.EndStroke(); }
        int undos = 0; while (model.CanUndo) { model.Undo(); undos++; }
        Assert.Equal(20, undos); Assert.Equal(1, model.Opacity[0]); Assert.Equal(0, model.Opacity[1]);
    }
}
