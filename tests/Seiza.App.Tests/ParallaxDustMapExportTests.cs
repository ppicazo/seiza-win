using System.Runtime.InteropServices;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxDustMapExportTests
{
    [Fact]
    public void NativeExportsProtectSceneInputsAndMapsAcrossRefitsAndSwaps()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        using var original = ParallaxCore.Prepare(fixture.Request);
        float[] opaque = Enumerable.Repeat(1f, 48 * 32).ToArray();
        using var painted = ParallaxCore.WithDustMap(original, opaque, 48, 32);
        string map = fixture.PathFor("input-dust.png");
        Assert.True(painted.WriteDustMap(map));
        using var fromFile = ParallaxCore.WithDustMap(original, map);
        using var refitted = ParallaxCore.Reconfigure(fromFile, fixture.Request.Video);
        using var swapped = ParallaxCore.WithDustMap(refitted, opaque, 48, 32);

        foreach (string input in new[] { fixture.Request.Inputs.Image, fixture.Request.Inputs.Stars!, map })
        {
            byte[] before = File.ReadAllBytes(input);
            Assert.Throws<SeizaCoreException>(() => swapped.WriteDustMap(input));
            Assert.Equal(before, File.ReadAllBytes(input));
        }

        // A map supplied while initially preparing the scene is protected too.
        fixture.Request.Inputs.DustMap = map;
        using var preparedWithMap = ParallaxCore.Prepare(fixture.Request);
        byte[] mapBefore = File.ReadAllBytes(map);
        Assert.Throws<SeizaCoreException>(() => preparedWithMap.WriteDustMap(map));
        Assert.Equal(mapBefore, File.ReadAllBytes(map));
    }

    [Fact]
    public void NativeNoDustAndCanceledExportPreserveExistingDestinations()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        using var original = ParallaxCore.Prepare(fixture.Request);
        string destination = fixture.PathFor("existing.png");
        byte[] before = [1, 5, 9, 13];
        File.WriteAllBytes(destination, before);
        Assert.False(original.WriteDustMap(destination));
        Assert.Equal(before, File.ReadAllBytes(destination));

        using var painted = ParallaxCore.WithDustMap(original, Enumerable.Repeat(1f, 48 * 32).ToArray(), 48, 32);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => painted.WriteDustMap(destination, cancellationToken: cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(destination));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, ".seiza-dust-*"));
    }

    [Fact]
    public void NativeEncoderFailurePreservesExistingDestination()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        using var original = ParallaxCore.Prepare(fixture.Request);
        using var painted = ParallaxCore.WithDustMap(original, Enumerable.Repeat(1f, 48 * 32).ToArray(), 48, 32);
        string destination = fixture.PathFor("existing.unsupported");
        byte[] before = [1, 5, 9, 13];
        File.WriteAllBytes(destination, before);
        Assert.Throws<SeizaCoreException>(() => painted.WriteDustMap(destination));
        Assert.Equal(before, File.ReadAllBytes(destination));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, ".seiza-dust-*"));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".tif")]
    [InlineData(".fits")]
    [InlineData(".xisf")]
    public void AtomicWriterPreservesEncoderExtensionAndPublishesCompletedOutput(string extension)
    {
        using var files = new OutputFixture();
        string destination = files.PathFor("output" + extension);
        File.WriteAllText(destination, "old output");
        bool written = ParallaxDustMapOutput.Write(destination, [], staging =>
        {
            Assert.NotEqual(destination, staging);
            Assert.Equal(extension, Path.GetExtension(staging));
            Assert.Equal(Path.GetDirectoryName(destination), Path.GetDirectoryName(staging));
            Assert.True(File.Exists(staging));
            File.WriteAllText(staging, "complete new output");
            Assert.Equal("old output", File.ReadAllText(destination));
            return true;
        }, CancellationToken.None);
        Assert.True(written);
        Assert.Equal("complete new output", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(files.DirectoryPath, ".seiza-dust-*"));
    }

    [Theory]
    [InlineData("no dust")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public void AtomicWriterNeverPublishesFalseFailedOrCanceledWrites(string outcome)
    {
        using var files = new OutputFixture();
        string destination = files.PathFor("existing.png");
        File.WriteAllText(destination, "old output");
        using var cancellation = new CancellationTokenSource();
        bool Write(string staging)
        {
            File.WriteAllText(staging, "partial output");
            if (outcome == "failure") throw new IOException("Synthetic encoder failure");
            if (outcome == "cancel") cancellation.Cancel();
            return outcome != "no dust";
        }
        if (outcome == "failure")
            Assert.Throws<IOException>(() => ParallaxDustMapOutput.Write(destination, [], Write, cancellation.Token));
        else if (outcome == "cancel")
            Assert.Throws<OperationCanceledException>(() => ParallaxDustMapOutput.Write(destination, [], Write, cancellation.Token));
        else
            Assert.False(ParallaxDustMapOutput.Write(destination, [], Write, cancellation.Token));
        Assert.Equal("old output", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(files.DirectoryPath, ".seiza-dust-*"));
    }

    [Fact]
    public void SourceGuardRejectsNormalizedAliasesBeforeInvokingTheWriter()
    {
        using var files = new OutputFixture();
        string source = files.PathFor("source.png");
        File.WriteAllText(source, "original image");
        string alias = Path.Combine(files.DirectoryPath, ".", "source.png");
        bool invoked = false;
        Assert.Throws<SeizaCoreException>(() => ParallaxDustMapOutput.Write(alias, [source], _ => { invoked = true; return true; }, CancellationToken.None));
        Assert.False(invoked);
        Assert.Equal("original image", File.ReadAllText(source));
        Assert.Empty(Directory.EnumerateFiles(files.DirectoryPath, ".seiza-dust-*"));

        if (OperatingSystem.IsWindows())
            Assert.Throws<SeizaCoreException>(() => ParallaxDustMapOutput.ValidateDestination(@"\\?\" + source, [source]));
    }

    [Fact]
    public void SourceGuardRejectsWindowsHardLinkAliases()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new OutputFixture();
        string source = files.PathFor("source.png"), alias = files.PathFor("alias.png");
        File.WriteAllText(source, "original image");
        Assert.True(CreateHardLink(alias, source, 0), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        Assert.Throws<SeizaCoreException>(() => ParallaxDustMapOutput.ValidateDestination(alias, [source]));
        Assert.Equal("original image", File.ReadAllText(source));
        Assert.Equal("original image", File.ReadAllText(alias));
    }

    [Fact]
    public void SourceGuardRechecksTheDestinationAfterWriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new OutputFixture();
        string source = files.PathFor("source.png"), destination = files.PathFor("output.png");
        File.WriteAllText(source, "original image");
        Assert.Throws<SeizaCoreException>(() => ParallaxDustMapOutput.Write(destination, [source], staging =>
        {
            File.WriteAllText(staging, "new output");
            Assert.True(CreateHardLink(destination, source, 0), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
            return true;
        }, CancellationToken.None));
        Assert.Equal("original image", File.ReadAllText(source));
        Assert.Equal("original image", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateFiles(files.DirectoryPath, ".seiza-dust-*"));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, nint securityAttributes);

    private sealed class OutputFixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", "dust-output-" + Guid.NewGuid().ToString("N"));
        internal OutputFixture() => Directory.CreateDirectory(DirectoryPath);
        internal string PathFor(string name) => Path.Combine(DirectoryPath, name);
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
