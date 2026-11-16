using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Nian.Input.Security;

internal static class PrivateFiles
{
    internal static void Check(string path)
    {
        if(!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path)==Path.GetFullPath(path)) throw new ArgumentException("Choose an absolute isolated profile path.");
        for(string? cursor=Path.GetFullPath(path);!string.IsNullOrEmpty(cursor);cursor=Path.GetDirectoryName(cursor))
            if((File.Exists(cursor)||System.IO.Directory.Exists(cursor)) && (File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0) throw new IOException("Device credentials cannot use linked paths.");
    }
    internal static void Directory(string path) { Check(path); System.IO.Directory.CreateDirectory(path); Check(path); Protect(path,true); }
    internal static byte[] Read(string path,int max=65536)
    {
        Check(path); Protect(path); using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length is <1 || stream.Length>max) throw new InvalidDataException("The device store length is invalid.");
        var value=new byte[checked((int)stream.Length)]; stream.ReadExactly(value); return value;
    }
    internal static async Task WriteAsync(string path,ReadOnlyMemory<byte> value,CancellationToken ct,bool overwrite=true,Func<IDisposable>? commitGuard=null,Action? committed=null)
    {
        Check(path); var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            var options=new FileStreamOptions { Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.Asynchronous };
            if(!OperatingSystem.IsWindows()) options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            await using(var stream=new FileStream(temporary,options)) { Protect(temporary); await stream.WriteAsync(value,ct); await stream.FlushAsync(ct); stream.Flush(true); }
            using(commitGuard?.Invoke())
            {
                Check(path);File.Move(temporary,path,overwrite);committed?.Invoke();
            }
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void Protect(string path,bool directory=false)
    {
        if(OperatingSystem.IsWindows()) ProtectWindows(path,directory);
        else File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|(directory?UnixFileMode.UserExecute:0));
    }
    [SupportedOSPlatform("windows")] private static void ProtectWindows(string path,bool directory)
    {
        using var identity=WindowsIdentity.GetCurrent(); var user=identity.User??throw new IOException("The current user identity is unavailable.");
        FileSystemSecurity security=directory?new DirectorySecurity():new FileSecurity(); security.SetOwner(user); security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new FileSystemAccessRule(user,FileSystemRights.FullControl,directory?InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit:InheritanceFlags.None,PropagationFlags.None,AccessControlType.Allow));
        if(directory)new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }
}
