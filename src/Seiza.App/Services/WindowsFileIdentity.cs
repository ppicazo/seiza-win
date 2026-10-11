using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Seiza.App.Services;

internal static class WindowsFileIdentity
{
    /// <summary>
    /// Resolves junctions/symlinks using the final path of an open Windows
    /// handle. For a future output, resolve its nearest existing ancestor and
    /// append only the missing suffix; no files or directories are created.
    /// </summary>
    public static string? TryResolveFinalPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string existing = Path.GetFullPath(path);
            var missing = new List<string>();
            while (!File.Exists(existing) && !Directory.Exists(existing))
            {
                string name = Path.GetFileName(existing);
                if (name.Length == 0) return null;
                missing.Add(name);
                string? parent = Path.GetDirectoryName(existing);
                if (parent is null) return null;
                existing = parent;
            }
            // Zero desired access is sufficient for path metadata, and backup
            // semantics permits opening a directory without traversing it.
            using SafeFileHandle handle = OpenPath(existing, 0, 7, 0, 3, 0x02000000, 0);
            if (handle.IsInvalid) return null;
            char[] buffer = new char[512];
            uint length = GetFinalPathNameByHandle(handle, buffer, checked((uint)buffer.Length), 0);
            if (length == 0 || length > 32768) return null;
            if (length >= buffer.Length)
            {
                buffer = new char[checked((int)length + 1)];
                length = GetFinalPathNameByHandle(handle, buffer, checked((uint)buffer.Length), 0);
                if (length == 0 || length >= buffer.Length) return null;
            }
            string resolved = new(buffer, 0, checked((int)length));
            for (int index = missing.Count - 1; index >= 0; index--)
            {
                resolved = Path.Combine(resolved, missing[index]);
            }
            return resolved;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static string? TryGet(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                return null;
            }
            ulong fileIndex = ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow;
            return $"{information.VolumeSerialNumber:X8}:{fileIndex:X16}";
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenPath(string fileName, uint access, uint share, nint securityAttributes,
        uint creationDisposition, uint flags, nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path,
        uint pathLength, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
