using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopToolbox.Core;

/// <summary>
/// Validates every component of a data directory. On Windows, directory handles remain
/// open without FILE_SHARE_DELETE until disposed, preventing ancestor rename/replacement
/// while configuration files are read or atomically replaced. Child-file writes remain allowed.
/// </summary>
public sealed class DataDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    private bool disposed;
    public string DirectoryPath { get; }
    public bool Exists { get; private set; }

    private DataDirectoryLease(string directory) { DirectoryPath = directory; }

    public static DataDirectoryLease Acquire(string directory, bool createIfMissing = false)
    {
        var fullPath = Normalize(directory);
        var lease = new DataDirectoryLease(fullPath);
        try
        {
            foreach (var component in Components(fullPath))
            {
                if (OperatingSystem.IsWindows())
                {
                    var handle = OpenWindowsDirectory(component, out var error);
                    if (handle is null)
                    {
                        if (error is not (2 or 3)) throw DirectoryAccessError(component, error);
                        if (!createIfMissing) return lease;
                        Directory.CreateDirectory(component);
                        handle = OpenWindowsDirectory(component, out error);
                        if (handle is null) throw DirectoryAccessError(component, error);
                    }
                    lease.handles.Add(handle);
                }
                else
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(component); }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        if (!createIfMissing) return lease;
                        Directory.CreateDirectory(component);
                        attributes = File.GetAttributes(component);
                    }
                    ValidateAttributes(component, attributes);
                }
            }
            lease.Exists = true;
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("本地数据目录无效。", nameof(directory));
        var full = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows())
        {
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException("本地数据目录不能使用设备命名空间。", nameof(directory));
            var pathRoot = Path.GetPathRoot(full)!;
            foreach (var name in full[pathRoot.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                ValidateName(name);
        }
        return Path.TrimEndingDirectorySeparator(full);
    }

    internal static void ValidateName(string name)
    {
        if (name is "." or ".." || name != Path.GetFileName(name) || string.IsNullOrWhiteSpace(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("本地数据名称无效。", nameof(name));
        if (OperatingSystem.IsWindows())
        {
            var stem = name.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
                throw new ArgumentException("本地数据名称不能使用Windows设备名称。", nameof(name));
        }
    }

    private static IEnumerable<string> Components(string full)
    {
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("本地数据目录缺少根路径。");
        yield return root;
        var current = root;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static SafeFileHandle? OpenWindowsDirectory(string path, out int error)
    {
        // Open the component itself, rather than following its reparse target.
        // FILE_LIST_DIRECTORY is intentional: attribute-only handles do not prevent directory rename on Windows.
        var extended = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        var handle = CreateFileW(extended, 0x00000001 | 0x00000080, 0x00000001 | 0x00000002, IntPtr.Zero,
            3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { error = Marshal.GetLastWin32Error(); handle.Dispose(); return null; }
        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw DirectoryAccessError(path, Marshal.GetLastWin32Error());
            ValidateAttributes(path, information.Attributes);
            error = 0;
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void ValidateAttributes(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"本地数据目录或其祖先是链接/目录联接，已停止读写：{path}");
        if ((attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException($"本地数据目录路径包含普通文件：{path}");
    }

    private static IOException DirectoryAccessError(string path, int error) =>
        new($"不能安全访问本地数据目录：{path}。{new Win32Exception(error).Message}", new Win32Exception(error));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        handles.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes Attributes;
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
}



