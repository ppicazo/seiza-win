using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed partial class BatchStackSourceProtectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
    private string? _junction;

    public BatchStackSourceProtectionTests() => Directory.CreateDirectory(_directory);

    [WindowsStackSourceIdentityFact]
    public void BatchPreflightProtectsOtherGroupsLightsMastersAndAlignmentImagesThroughJunctions()
    {
        string target = Path.Combine(_directory, "later-group");
        Directory.CreateDirectory(target);
        string source = Path.Combine(target, "protected.fits");
        File.WriteAllText(source, "original source");
        string alias = Path.Combine(CreateJunction(target), "protected.fits");
        Assert.True(StackSourceIdentity.RefersToSameFile(alias, source));

        foreach (string kind in new[] { "light", "master", "alignment" })
        {
            ImageStackJob first = Job("ha", [PathFor("ha-1.fits"), PathFor("ha-2.fits")], alias);
            ImageStackJob second = Job("oiii",
                [kind == "light" ? source : PathFor("oiii-1.fits"), PathFor("oiii-2.fits")], PathFor("oiii-stack.fits"));
            second = second with
            {
                Request = second.Request with
                {
                    Calibration = new ImageStackCalibration { BiasPath = kind == "master" ? source : null },
                    AlignmentReferencePath = kind == "alignment" ? source : null,
                },
            };

            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                ImageStackValidation.ValidateBatch([first, second]));

            Assert.Contains("not input or calibration files", exception.Message, StringComparison.Ordinal);
            Assert.Equal("original source", File.ReadAllText(source));
        }
    }

    [WindowsStackSourceIdentityFact]
    public void BatchPreflightRejectsEquivalentOutputFilesThroughJunctions()
    {
        string target = Path.Combine(_directory, "outputs");
        Directory.CreateDirectory(target);
        string output = Path.Combine(target, "stack.fits");
        File.WriteAllText(output, "previous output");
        string alias = Path.Combine(CreateJunction(target), "stack.fits");
        ImageStackJob[] jobs =
        [
            Job("ha", [PathFor("ha-1.fits"), PathFor("ha-2.fits")], output),
            Job("oiii", [PathFor("oiii-1.fits"), PathFor("oiii-2.fits")], alias),
        ];

        ArgumentException exception = Assert.Throws<ArgumentException>(() => ImageStackValidation.ValidateBatch(jobs));

        Assert.Contains("different output file", exception.Message, StringComparison.Ordinal);
        Assert.Equal("previous output", File.ReadAllText(output));
    }

    [WindowsStackSourceIdentityFact]
    public void AtomicPublicationRejectsASourceAliasIntroducedWhileStaging()
    {
        string source = PathFor("source.fits");
        string output = PathFor("output.fits");
        File.WriteAllText(source, "original source");
        bool validated = false;

        Assert.Throws<SeizaCoreException>(() => AtomicOutputFile.Write(output,
            staging =>
            {
                File.WriteAllText(staging, "completed output");
                Assert.True(CreateHardLink(output, source, 0), new Win32Exception(Marshal.GetLastWin32Error()).Message);
            },
            () =>
            {
                validated = true;
                StackOutputProtection.ValidateDestination(output, [source]);
            }, CancellationToken.None));

        Assert.True(validated);
        Assert.Equal("original source", File.ReadAllText(source));
        Assert.Equal("original source", File.ReadAllText(output));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".seiza-stack-*.fits"));
    }

    [Fact]
    public void CancellationDuringFinalValidationPreservesTheExistingOutput()
    {
        string output = PathFor("output.fits");
        File.WriteAllText(output, "previous output");
        using var cancellation = new CancellationTokenSource();
        bool validated = false;

        Assert.Throws<OperationCanceledException>(() => AtomicOutputFile.Write(output,
            staging => File.WriteAllText(staging, "completed output"),
            () => { validated = true; cancellation.Cancel(); }, cancellation.Token));

        Assert.True(validated);
        Assert.Equal("previous output", File.ReadAllText(output));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".seiza-stack-*.fits"));
    }

    [WindowsStackSourceIdentityFact]
    public async Task BatchWriteRechecksOtherGroupsSourcesAfterPreflight()
    {
        string[] firstLights = [PathFor("ha-1.fits"), PathFor("ha-2.fits")];
        string[] laterLights = [PathFor("oiii-1.fits"), PathFor("oiii-2.fits")];
        foreach (string[] lights in new[] { firstLights, laterLights })
        {
            for (int index = 0; index < lights.Length; index++)
            {
                StackingOptionsNativeTests.WriteStarField(lights[index], index, bayer: false);
            }
        }
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(laterLights[0]));
        string firstOutput = PathFor("ha-stack.fits");
        string laterOutput = PathFor("oiii-stack.fits");
        ImageStackJob[] jobs = [Job("ha", firstLights, firstOutput), Job("oiii", laterLights, laterOutput)];
        bool introducedAlias = false;
        var progress = new InlineProgress<ImageStackProgress>(update =>
        {
            if (update.Phase == ImageStackProgressPhase.Writing && !introducedAlias)
            {
                Assert.True(CreateHardLink(firstOutput, laterLights[0], 0), new Win32Exception(Marshal.GetLastWin32Error()).Message);
                introducedAlias = true;
            }
        });

        await Assert.ThrowsAsync<SeizaCoreException>(() => ImageStackService.StackBatchAsync(jobs, progress));

        Assert.True(introducedAlias);
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(laterLights[0])));
        Assert.Equal(sourceHash, SHA256.HashData(File.ReadAllBytes(firstOutput)));
        Assert.False(File.Exists(laterOutput));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".seiza-stack-*.fits"));
    }

    [WindowsStackSourceIdentityFact]
    public async Task NonexistentEquivalentOutputsCannotReplaceTheEarlierPublishedGroup()
    {
        string target = Path.Combine(_directory, "outputs");
        Directory.CreateDirectory(target);
        string output = Path.Combine(target, "stack.fits");
        string alias = Path.Combine(CreateJunction(target), "stack.fits");
        string[] firstLights = [PathFor("ha-1.fits"), PathFor("ha-2.fits")];
        string[] laterLights = [PathFor("oiii-1.fits"), PathFor("oiii-2.fits")];
        foreach (string[] lights in new[] { firstLights, laterLights })
        {
            for (int index = 0; index < lights.Length; index++)
            {
                StackingOptionsNativeTests.WriteStarField(lights[index], index, bayer: false);
            }
        }
        ImageStackJob[] jobs = [Job("ha", firstLights, output), Job("oiii", laterLights, alias)];
        ImageStackValidation.ValidateBatch(jobs); // Neither final filename exists yet.
        byte[]? firstHash = null;
        int writing = 0;
        var progress = new InlineProgress<ImageStackProgress>(update =>
        {
            if (update.Phase == ImageStackProgressPhase.Writing && ++writing == 2)
            {
                firstHash = SHA256.HashData(File.ReadAllBytes(output));
            }
        });

        ImageStackBatchFailureException failure = await Assert.ThrowsAsync<ImageStackBatchFailureException>(() =>
            ImageStackService.StackBatchAsync(jobs, progress));

        Assert.IsType<SeizaCoreException>(failure.InnerException);
        Assert.Equal(new[] { output }, failure.CompletedOutputPaths);
        Assert.NotNull(firstHash);
        Assert.Equal(firstHash, SHA256.HashData(File.ReadAllBytes(output)));
        Assert.Equal(firstHash, SHA256.HashData(File.ReadAllBytes(alias)));
        Assert.Empty(Directory.EnumerateFiles(target, ".seiza-stack-*.fits"));
    }

    private string PathFor(string name) => Path.Combine(_directory, name);

    private static ImageStackJob Job(string filter, string[] lights, string output) =>
        new(new ImageStackGroup(filter, null, lights), new ImageStackRequest(lights, output,
            new ImageStackOptions { RemoveTransients = false }, new ImageStackCalibration()));

    private string CreateJunction(string target)
    {
        // Directory junctions do not require developer mode or symlink privileges.
        // Build only a mount-point reparse record beneath this test's GUID folder.
        _junction = PathFor("junction");
        Directory.CreateDirectory(_junction);
        string substitute = @"\??\" + Path.GetFullPath(target);
        byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute);
        byte[] targetBytes = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] buffer = new byte[16 + substituteBytes.Length + 2 + targetBytes.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substituteBytes.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)targetBytes.Length));
        substituteBytes.CopyTo(buffer, 16);
        targetBytes.CopyTo(buffer, 16 + substituteBytes.Length + 2);
        using SafeFileHandle handle = OpenReparsePoint(_junction, 0x40000000, 7, 0, 3, 0x02200000, 0);
        Assert.False(handle.IsInvalid, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        Assert.True(SetReparsePoint(handle, 0x000900A4, buffer, checked((uint)buffer.Length), 0, 0, out _, 0),
            new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return _junction;
    }

    public void Dispose()
    {
        // Remove the junction itself before recursively deleting the ordinary
        // GUID test directory; never traverse its reparse target during cleanup.
        if (_junction is not null && Directory.Exists(_junction)) Directory.Delete(_junction);
        Directory.Delete(_directory, recursive: true);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle OpenReparsePoint(string fileName, uint access, uint share, nint securityAttributes,
        uint creationDisposition, uint flags, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetReparsePoint(SafeFileHandle handle, uint controlCode,
        [In] byte[] input, uint inputLength, nint output, uint outputLength, out uint returned, nint overlapped);
}
