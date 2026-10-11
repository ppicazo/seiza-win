using System.ComponentModel;
using System.Runtime.InteropServices;
using Seiza.App.Models;
using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed partial class StackSourceIdentityTests
{
    [Fact]
    public void SourceIdentityUsesNormalizedPathEqualityBeforeFileLookup()
    {
        string source = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", "source.fits");
        string equivalent = Path.Combine(Path.GetDirectoryName(source)!, ".", Path.GetFileName(source));
        Assert.True(StackSourceIdentity.RefersToSameFile(source, equivalent));
        Assert.False(StackSourceIdentity.RefersToSameFile(source, source + ".different"));
        Assert.False(StackSourceIdentity.RefersToSameFile(null, source));
        Assert.False(StackSourceIdentity.RefersToSameFile(source, " "));
    }

    [WindowsStackSourceIdentityFact]
    public void SourceIdentityAndOutputProtectionRecognizeHardLinkAliases()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Seiza.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.fits");
            string alias = Path.Combine(directory, "alias.fits");
            string distinct = Path.Combine(directory, "distinct.fits");
            File.WriteAllText(source, "synthetic source");
            File.WriteAllText(distinct, "synthetic source");
            Assert.True(CreateHardLink(alias, source, 0), new Win32Exception(Marshal.GetLastWin32Error()).Message);

            Assert.True(StackSourceIdentity.RefersToSameFile(source, alias));
            Assert.True(StackSourceIdentity.RefersToSameFile(alias, source));
            Assert.False(StackSourceIdentity.RefersToSameFile(source, distinct));
            Assert.Throws<SeizaCoreException>(() => StackOutputProtection.ValidateDestination(alias, [source]));
            StackOutputProtection.ValidateDestination(distinct, [source]);
            Assert.Equal("synthetic source", File.ReadAllText(source));

            var options = new StackFolderMonitorOptions { FolderPath = directory, ExcludedPaths = [source, distinct] };
            var tracker = new StackFileCandidateTracker(options);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var file = new FileInfo(alias);
            var observation = new StackFileObservation(alias, file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero), now, WindowsFileIdentity.TryGet(alias));
            Assert.Null(tracker.Observe(observation));
            Assert.Null(tracker.Observe(observation with { ObservedAtUtc = now.AddSeconds(3) }));
            Assert.Empty(tracker.PendingPaths);

            var startup = new LiveStackReferenceCandidates(options);
            Assert.Empty(startup.ObserveExisting(now));
            Assert.Empty(startup.ObserveExisting(now.AddSeconds(3)));
            Assert.False(startup.HasPendingCandidates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}

public sealed class WindowsStackSourceIdentityFactAttribute : FactAttribute
{
    public WindowsStackSourceIdentityFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows file identity and hard-link source protection require Windows.";
        }
    }
}
