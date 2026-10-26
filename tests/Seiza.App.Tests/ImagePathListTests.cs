using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ImagePathListTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("SeizaPathList-").FullName;

    [Fact]
    public void TextSupportsQuotedAbsoluteAndRelativePathsAndDeduplicates()
    {
        string first = Write("first frame.fits", "");
        string second = Write("second.xisf", "");
        string list = Write("images.txt", $"\n\"{first}\"\nsecond.xisf\n./second.xisf\nmissing.fits\nnotes.txt\n");
        ImagePathListResult result = ImagePathList.Read(list);
        Assert.Equal(new[] { first, second }, result.Paths);
        Assert.Equal(2, result.SkippedCount);
    }

    [Fact]
    public void CsvFindsNamedPathColumnAndHandlesQuotedCommas()
    {
        string image = Write("frame, one.FITS", "");
        string list = Write("images.csv", "rating,filepath,note\r\n5,\"frame, one.FITS\",\"a \"\"quoted\"\" note\"\r\n4,missing.fits,no\r\n3\r\n");
        ImagePathListResult result = ImagePathList.Read(list);
        Assert.Equal(image, Assert.Single(result.Paths));
        Assert.Equal(2, result.SkippedCount);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("file_path")]
    [InlineData("FilePath")]
    [InlineData("file")]
    [InlineData("FileName")]
    [InlineData(" FILE ")]
    [InlineData(" File_Path ")]
    public void CsvRecognizesPathHeaders(string header)
    {
        string image = Write("frame.fit", "");
        var result = ImagePathList.Read(Write("images.csv", $"rating,{header}\n5,frame.fit\n"));
        Assert.Equal(image, Assert.Single(result.Paths));
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void HeaderlessCsvUsesFirstColumnAndIgnoresMetadata()
    {
        string image = Write("first.png", "");
        Write("other.fits", "");
        var result = ImagePathList.Read(Write("images.csv", "first.png,other.fits\nfirst.png,duplicate\n"));
        Assert.Equal(image, Assert.Single(result.Paths));
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void SubframeSelectorPreambleIsNotCountedAsImageData()
    {
        string first = Write("approved.fits", "");
        string second = Write("rejected.fits", "");
        string list = Write("subframes.csv",
            "SubframeSelector 1.9\nSubframe Scale,1.25000\nCamera Gain,0.70000\n" +
            "Approval expression,\"FWHM < 5\"\n\nIndex,Approved,Locked,File,Weight\n" +
            $"1,true,false,\"{first}\",1\n2,false,true,\"{second}\",0.5\n3,true,false,missing.fits,0.1\n");
        var result = ImagePathList.Read(list);
        Assert.Equal(new[] { first, second }, result.Paths);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void HeaderlessDataCannotSwitchColumnsAfterFirstImage()
    {
        string first = Write("first.fits", "");
        string second = Write("second.fits", "");
        var result = ImagePathList.Read(Write("images.csv", "first.fits,note\nsecond.fits,File\n"));
        Assert.Equal(new[] { first, second }, result.Paths);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void ParentRelativePathsResolveFromListDirectory()
    {
        string image = Write("frame.fits", "");
        string nested = Directory.CreateDirectory(Path.Combine(_root, "lists")).FullName;
        string list = Path.Combine(nested, "images.txt");
        try
        {
            File.WriteAllText(list, "../frame.fits\n");
            Assert.Equal(image, Assert.Single(ImagePathList.Read(list).Paths));
        }
        finally
        {
            File.Delete(list);
            Directory.Delete(nested);
        }
    }

    [Fact]
    public void MalformedCsvReportsLineInsteadOfReturningPartialCollection()
    {
        Write("first.fits", "");
        string list = Write("images.csv", "path\nfirst.fits\n\"unterminated\n");
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ImagePathList.Read(list));
        Assert.Contains("line 3", error.Message);
    }

    [Fact]
    public void EmptyListAndMissingListAreDistinct()
    {
        Assert.Empty(ImagePathList.Read(Write("empty.txt", "\n  \n")).Paths);
        Assert.Throws<FileNotFoundException>(() => ImagePathList.Read(Path.Combine(_root, "missing.txt")));
    }

    [Fact]
    public void CancellationPrecedesReadingTheFile()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ImagePathList.Read(Path.Combine(_root, "missing.txt"), cancellation.Token));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        // Only remove files created directly in this fixture; never recurse into listed targets.
        foreach (string path in Directory.EnumerateFiles(_root)) File.Delete(path);
        Directory.Delete(_root);
    }
}
