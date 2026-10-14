using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class ParallaxManualDepthNativeTests
{
    [Fact]
    public void ManualMaskPreparesAGenuinePlaneAndRetainsItsImmutableInputAcrossRefits()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        string mask = AddManualCloud(fixture);
        byte[] maskBefore = File.ReadAllBytes(mask);
        using ParallaxVideo original = ParallaxCore.Prepare(fixture.Request);
        ParallaxPlane plane = Assert.Single(original.Summary.Planes);
        Assert.Equal("Painted cloud", plane.Name);
        Assert.Equal(150, plane.DistanceParsecs);
        Assert.Equal("given", plane.Basis);
        Assert.Null(plane.ObjectName);
        Assert.InRange(plane.Coverage, 0.01, 0.8);
        Assert.Contains(mask, original.ProtectedPaths);
        byte[] oldFrame = original.RenderBgraFrame(0);
        using ParallaxVideo refitted = ParallaxCore.Reconfigure(original, fixture.Request.Video with { Size = "80x60" });
        Assert.Equal(original.Summary.Planes, refitted.Summary.Planes);
        Assert.Throws<SeizaCoreException>(() => refitted.WriteDustMap(mask));
        Assert.Equal(maskBefore, File.ReadAllBytes(mask));
        // A changed editable plane record cannot change what the old video used.
        fixture.Request.Scene.Planes![0].Mask = fixture.PathFor("other-mask.png");
        Assert.Contains(mask, refitted.ProtectedPaths);
        Assert.Equal(oldFrame, original.RenderBgraFrame(0));
        // Refits reuse the prepared plane and never reload its mask.
        File.Delete(mask);
        using ParallaxVideo afterRemoval = ParallaxCore.Reconfigure(refitted, fixture.Request.Video with { Size = "80x60" });
        Assert.Equal(80 * 60 * 4, afterRemoval.RenderBgraFrame(0).Length);
        Assert.Contains(mask, afterRemoval.ProtectedPaths);
    }

    [Fact]
    public void ExplicitStopDepthReconfiguresWithoutRebuildingAndClearUsesTheCurrentPlane()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        AddManualCloud(fixture);
        fixture.Request.Video.Tour[1].Focus = [48, 32];
        fixture.Request.Video.Tour[1].Pan = 0;
        fixture.Request.Video.Tour[1].RotateDegrees = 0;
        fixture.Request.Video.Tour[1].DepthParsecs = 150;
        using ParallaxVideo original = ParallaxCore.Prepare(fixture.Request);
        byte[] before = original.RenderBgraFrame(10);
        var changed = fixture.Request.Video with { Tour = fixture.Request.Video.Tour.Select(stop => stop.DeepClone()).ToList() };
        changed.Tour[1].DepthParsecs = 1200;
        var warnings = new List<string>();
        using ParallaxVideo deeper = ParallaxCore.Reconfigure(original, changed, new InlineProgress<ParallaxEvent>(update =>
        {
            if (update.Kind == "warning" && update.Message is string warning) warnings.Add(warning);
        }));
        Assert.Contains(warnings, warning => warning.Contains("framed at 1200 pc", StringComparison.Ordinal));
        Assert.NotEqual(before, deeper.RenderBgraFrame(10));
        changed.Tour[1].DepthParsecs = null;
        using ParallaxVideo automatic = ParallaxCore.Reconfigure(deeper, changed);
        Assert.Equal(before, automatic.RenderBgraFrame(10));
        Assert.Equal(before, original.RenderBgraFrame(10));
        changed.Tour[1].DepthParsecs = -1;
        Assert.Throws<SeizaCoreException>(() => ParallaxCore.Reconfigure(original, changed));
        Assert.Equal(before, original.RenderBgraFrame(10));
    }

    [Fact]
    public void ManualMaskErrorsAreActionableAndCannotDamageAPreparedVideo()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        AddManualCloud(fixture);
        using ParallaxVideo original = ParallaxCore.Prepare(fixture.Request);
        byte[] before = original.RenderBgraFrame(0);
        string missing = fixture.PathFor("missing-mask.png");
        fixture.Request.Scene.Planes![0].Mask = missing;
        SeizaCoreException missingError = Assert.Throws<SeizaCoreException>(() => ParallaxCore.Prepare(fixture.Request));
        Assert.Contains("missing-mask.png", missingError.Message, StringComparison.Ordinal);
        string wrongShape = fixture.PathFor("wrong-shape.png");
        WritePng(wrongShape, 16, 16, (_, _) => [255, 255, 255]);
        fixture.Request.Scene.Planes[0].Mask = wrongShape;
        SeizaCoreException shapeError = Assert.Throws<SeizaCoreException>(() => ParallaxCore.Prepare(fixture.Request));
        Assert.Contains("shape", shapeError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, original.RenderBgraFrame(0));
    }

    [Fact]
    public void NamedObjectDistanceCanBeGivenWithoutAutomaticPlaneDetection()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        WriteNamedCatalogAndDistances(fixture);
        fixture.Request.Scene.DistanceParsecs = null;
        fixture.Request.Scene.DistanceFocus = [48, 32];
        fixture.Request.Scene.SetDistanceOverride("  ngc 9001  ", 725);
        Assert.False(fixture.Request.Scene.AutoPlanes);
        using ParallaxVideo video = ParallaxCore.Prepare(fixture.Request);
        Assert.Equal(725, video.Summary.BackgroundDistanceParsecs);
        fixture.Request.Scene.SetDistanceOverride("unknown named object", 500);
        SeizaCoreException error = Assert.Throws<SeizaCoreException>(() => ParallaxCore.Prepare(fixture.Request));
        Assert.Contains("unknown named object", error.Message, StringComparison.Ordinal);
        Assert.Equal(66 * 48 * 4, video.RenderBgraFrame(0).Length);
    }

    [Fact]
    public void ManualMaskAliasesAndDraftOnlyMasksAreProtectedFromDustPublication()
    {
        using var fixture = new ParallaxCoreTests.Fixture();
        string mask = AddManualCloud(fixture);
        byte[] before = File.ReadAllBytes(mask);
        using ParallaxVideo original = ParallaxCore.Prepare(fixture.Request);
        using ParallaxVideo dusty = ParallaxCore.WithDustMap(original, Enumerable.Repeat(1f, 48 * 32).ToArray(), 48, 32);
        Assert.Throws<SeizaCoreException>(() => dusty.WriteDustMap(mask));
        string alias = fixture.PathFor("mask-hardlink.png");
        Assert.True(CreateHardLink(alias, mask, 0), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
        Assert.Throws<SeizaCoreException>(() => dusty.WriteDustMap(alias));
        Assert.Equal(before, File.ReadAllBytes(alias));
        var draft = new ParallaxComposition { Scene = new() { Planes = [new() { Mask = fixture.PathFor("draft-mask.png"), DistanceParsecs = 200 }] } };
        File.Copy(mask, draft.Scene.Planes![0].Mask);
        Assert.Throws<SeizaCoreException>(() => dusty.WriteDustMap(draft.Scene.Planes[0].Mask, ParallaxInputPaths.FromComposition(draft)));
        Assert.Equal(before, File.ReadAllBytes(draft.Scene.Planes[0].Mask));
        Assert.Equal(before, File.ReadAllBytes(mask));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(mask)!, ".seiza-dust-*"));
    }

    private static string AddManualCloud(ParallaxCoreTests.Fixture fixture)
    {
        string source = fixture.Request.Inputs.Starless!;
        WritePng(source, 96, 64, (x, y) =>
        {
            byte cloud = (byte)(180 * Math.Exp(-((x - 48.0) * (x - 48) + (y - 32.0) * (y - 32)) / 80));
            return [(byte)(25 + cloud), (byte)(30 + cloud), (byte)(40 + cloud)];
        });
        string mask = fixture.PathFor("painted mask α.png");
        WritePng(mask, 96, 64, (x, y) =>
        {
            byte coverage = (x - 48) * (x - 48) + (y - 32) * (y - 32) < 20 * 20 ? (byte)255 : (byte)0;
            return [coverage, coverage, coverage];
        });
        fixture.Request.Scene.Planes = [new() { Mask = mask, Name = "Painted cloud", DistanceParsecs = 150 }];
        return mask;
    }

    private static void WriteNamedCatalogAndDistances(ParallaxCoreTests.Fixture fixture)
    {
        // A small fixed-layout v3 catalogue, with a stable ID and the paired
        // distance-file fingerprint required by native focus lookup. An empty
        // distance table uses the kind default until the named override wins.
        string catalog = fixture.PathFor("named-objects.bin");
        using var strings = new MemoryStream();
        using var textWriter = new BinaryWriter(strings, Encoding.UTF8, leaveOpen: true);
        (uint Offset, ushort Length) Text(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            var reference = (checked((uint)strings.Position), checked((ushort)bytes.Length));
            textWriter.Write(bytes); return reference;
        }
        var name = Text("NGC 9001"); var id = Text("test:NGC9001"); var key = Text("NGC9001");
        const int records = 104, tiles = 192, candidates = 208, names = 216, lists = 232, texts = 232;
        using (var file = File.Create(catalog))
        using (var writer = new BinaryWriter(file, Encoding.UTF8))
        {
            writer.Write("SEIZAOB3"u8); writer.Write(104u); writer.Write(1u); writer.Write(1u);
            writer.Write(2u); writer.Write(1u); writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((ulong)strings.Length);
            foreach (int offset in new[] { records, tiles, candidates, names, lists, texts, texts + (int)strings.Length }) writer.Write((ulong)offset);
            void Ref((uint Offset, ushort Length) reference) { writer.Write(reference.Offset); writer.Write(reference.Length); }
            void Pad(int offset) { while (file.Position < offset) writer.Write((byte)0); }
            writer.Write(new byte[] { 5, 0, 0, 0 }); writer.Write(56.75); writer.Write(24.12);
            writer.Write(float.NaN); writer.Write(1f); writer.Write(float.NaN); writer.Write(float.NaN);
            Ref(name); Ref((0, 0)); Ref(id); Ref((0, 0));
            for (int i = 0; i < 4; i++) Ref((0, 0));
            Pad(tiles); writer.Write(0u); writer.Write(1u); writer.Write(1u); writer.Write(0u);
            Pad(candidates); writer.Write(0u);
            Pad(names); Ref(key); Ref(name); writer.Write(0u);
            Pad(texts); writer.Write(strings.ToArray());
        }
        string distances = fixture.PathFor("named-object-distances.bin");
        using (var writer = new BinaryWriter(File.Create(distances), Encoding.UTF8))
        {
            writer.Write("SEIZADS1"u8); writer.Write(0u); writer.Write(new byte[4]); writer.Write(0u);
            writer.Write(SHA256.HashData(File.ReadAllBytes(catalog)));
            byte[] notice = "Synthetic catalogue fixture; no astronomical measurements"u8.ToArray();
            writer.Write(checked((ushort)notice.Length)); writer.Write(notice);
        }
        fixture.Request.Inputs.Objects = catalog;
        fixture.Request.Inputs.ObjectDistances = distances;
    }

    private static void WritePng(string path, int width, int height, Func<int, int, byte[]> pixel)
    {
        byte[] samples = new byte[(width * 3 + 1) * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) pixel(x, y).CopyTo(samples, y * (width * 3 + 1) + 1 + x * 3);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(samples);
        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), checked((uint)height));
        header[8] = 8; header[9] = 2;
        Chunk(file, "IHDR", header); Chunk(file, "IDAT", compressed.ToArray()); Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        byte[] name = Encoding.ASCII.GetBytes(type);
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, checked((uint)data.Length));
        file.Write(word); file.Write(name); file.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in name.Concat(data))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); file.Write(word);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingName, nint securityAttributes);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
