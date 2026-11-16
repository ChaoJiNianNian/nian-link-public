using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Nian.Input.Security;

/// <summary>Current-user DPAPI only; no machine scope and no plaintext private key file.</summary>
public sealed class WindowsIdentityVault(string profileDirectory) : IIdentityVault
{
    public Task<byte[]?> LoadAsync(string key,CancellationToken cancellationToken=default)=>Task.Run(()=>Load(key),cancellationToken);
    private byte[]? Load(string key)
    {
        EnsureWindows();var path=PathFor(key);PrivateFiles.Check(path);if(!File.Exists(path))return null;
        var encrypted=PrivateFiles.Read(path);try{return Transform(encrypted,key,false);}finally{CryptographicOperations.ZeroMemory(encrypted);}
    }
    public async Task StoreAsync(string key,ReadOnlyMemory<byte> value,CancellationToken cancellationToken=default)
    {
        var plaintext=value.ToArray();byte[]? encrypted=null;
        try { encrypted=await Task.Run(()=>{EnsureWindows();return Transform(plaintext,key,true);},cancellationToken);await PrivateFiles.WriteAsync(PathFor(key),encrypted,cancellationToken,false); }
        finally {CryptographicOperations.ZeroMemory(plaintext);if(encrypted is not null)CryptographicOperations.ZeroMemory(encrypted);}
    }
    private string PathFor(string key)
    { if(!TrustValidation.Fingerprint(key))throw new ArgumentException("The identity vault key is invalid.");return Path.Combine(profileDirectory,"identity-"+key+".dpapi"); }
    private static void EnsureWindows(){if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("DPAPI requires Windows.");}
    [StructLayout(LayoutKind.Sequential)]private struct Blob{public int Length;public nint Data;}
    private static byte[] Transform(byte[] input,string key,bool protect)
    {
        var entropy=SHA256.HashData(Encoding.UTF8.GetBytes("NianDesktopInputV2:"+key));var source=new Blob{Length=input.Length,Data=Marshal.AllocHGlobal(input.Length)};var extra=new Blob{Length=entropy.Length,Data=Marshal.AllocHGlobal(entropy.Length)};Blob output=default;
        try
        {
            Marshal.Copy(input,0,source.Data,input.Length);Marshal.Copy(entropy,0,extra.Data,entropy.Length);
            var ok=protect?CryptProtectData(ref source,null,ref extra,0,0,1,out output):CryptUnprotectData(ref source,0,ref extra,0,0,1,out output);
            if(!ok)throw new CryptographicException("Current-user identity protection failed.",new Win32Exception(Marshal.GetLastWin32Error()));
            if(output.Length is <1 or >65536)throw new CryptographicException("The protected identity length is invalid.");
            var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result;
        }
        finally
        {
            Zero(source);Marshal.FreeHGlobal(source.Data);Zero(extra);Marshal.FreeHGlobal(extra.Data);CryptographicOperations.ZeroMemory(entropy);
            if(output.Data!=0){Zero(output);LocalFree(output.Data);}
        }
    }
    private static void Zero(Blob blob){for(var i=0;i<blob.Length;i++)Marshal.WriteByte(blob.Data,i,0);}
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CryptProtectData(ref Blob input,string? description,ref Blob entropy,nint reserved,nint prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CryptUnprotectData(ref Blob input,nint description,ref Blob entropy,nint reserved,nint prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")]private static extern nint LocalFree(nint value);
}
