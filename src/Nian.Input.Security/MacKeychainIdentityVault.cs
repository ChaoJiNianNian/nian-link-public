using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Nian.Input.Security;

/// <summary>Private PKCS12 bytes stay in Keychain and process memory; never written to disk or passed through a command line.</summary>
public sealed class MacKeychainIdentityVault : IIdentityVault
{
    // Standalone product has its own identity namespace; never access the toolbox's existing keychain entry.
    public const string ServiceName="app.niannian.nianlink-standalone.identity.v1";
    private const string DiscoveryServiceName="app.niannian.nianlink-discovery-test.identity.v1";
    private const string FilelessServiceName="app.niannian.nianlink-fileless-test.identity.v1";
    public string IdentityServiceName { get; }
    public MacKeychainIdentityVault():this(ServiceName){}
    private MacKeychainIdentityVault(string serviceName)=>IdentityServiceName=serviceName;
    public static MacKeychainIdentityVault CreateDiscoveryCandidate()=>new(DiscoveryServiceName);
    public static MacKeychainIdentityVault CreateFilelessCandidate()=>new(FilelessServiceName);
    public Task<byte[]?> LoadAsync(string key,CancellationToken cancellationToken=default)=>Task.Run(()=>Load(key,IdentityServiceName),cancellationToken);
    public async Task StoreAsync(string key,ReadOnlyMemory<byte> value,CancellationToken cancellationToken=default)
    {
        var copy=value.ToArray();try{await Task.Run(()=>Store(key,copy,IdentityServiceName),cancellationToken);}finally{CryptographicOperations.ZeroMemory(copy);}
    }
    private static byte[]? Load(string key,string serviceName)
    {
        EnsureMac(key);using var query=new Query(key,serviceName);query.Set("kSecReturnData",Native.True);query.Set("kSecMatchLimit",Native.Constant("kSecMatchLimitOne"));
        var status=Native.SecItemCopyMatching(query.Handle,out var result);
        if(status==-25300)return null;if(status!=0)throw new CryptographicException("Mac Keychain identity read failed ("+status+").");
        try
        {
            if(result==0||Native.CFGetTypeID(result)!=Native.CFDataGetTypeID())throw new CryptographicException("Mac Keychain identity value is invalid.");
            var length=Native.CFDataGetLength(result);if(length is <1 or >65536)throw new CryptographicException("Mac Keychain identity length is invalid.");
            var bytes=new byte[checked((int)length)];Marshal.Copy(Native.CFDataGetBytePtr(result),bytes,0,bytes.Length);return bytes;
        }
        finally{if(result!=0)Native.CFRelease(result);}
    }
    private static void Store(string key,byte[] bytes,string serviceName)
    {
        EnsureMac(key);using var query=new Query(key,serviceName);var data=Native.CFDataCreate(0,bytes,bytes.Length);if(data==0)throw new CryptographicException("Mac Keychain identity allocation failed.");
        try
        {
            query.Set("kSecValueData",data);query.Set("kSecAttrAccessible",Native.Constant("kSecAttrAccessibleWhenUnlockedThisDeviceOnly"));
            var status=Native.SecItemAdd(query.Handle,0);if(status!=0)throw new CryptographicException("Mac Keychain identity creation failed ("+status+").");
        }
        finally{Native.CFRelease(data);}
    }
    private static void EnsureMac(string key){if(!OperatingSystem.IsMacOS())throw new PlatformNotSupportedException("Keychain requires macOS.");if(!TrustValidation.Fingerprint(key))throw new ArgumentException("The identity vault key is invalid.");}
    private sealed class Query : IDisposable
    {
        private readonly nint service,account;
        internal nint Handle {get;}
        internal Query(string key,string serviceName)
        {
            Handle=Native.CFDictionaryCreateMutable(0,0,Native.ExportCore("kCFTypeDictionaryKeyCallBacks"),Native.ExportCore("kCFTypeDictionaryValueCallBacks"));
            service=Native.CFStringCreateWithCString(0,serviceName,0x08000100);account=Native.CFStringCreateWithCString(0,key,0x08000100);
            if(Handle==0||service==0||account==0){Dispose();throw new CryptographicException("Mac Keychain query allocation failed.");}
            Set("kSecClass",Native.Constant("kSecClassGenericPassword"));Set("kSecAttrService",service);Set("kSecAttrAccount",account);
        }
        internal void Set(string key,nint value)=>Native.CFDictionarySetValue(Handle,Native.Constant(key),value);
        public void Dispose(){if(Handle!=0)Native.CFRelease(Handle);if(service!=0)Native.CFRelease(service);if(account!=0)Native.CFRelease(account);}
    }
    private static class Native
    {
        private const string Security="/System/Library/Frameworks/Security.framework/Security",Core="/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly nint security=NativeLibrary.Load(Security),core=NativeLibrary.Load(Core);
        internal static nint Constant(string name)=>Marshal.ReadIntPtr(NativeLibrary.GetExport(security,name));
        internal static nint ExportCore(string name)=>NativeLibrary.GetExport(core,name);
        internal static nint True=>Marshal.ReadIntPtr(ExportCore("kCFBooleanTrue"));
        [DllImport(Security)]internal static extern int SecItemCopyMatching(nint query,out nint result);
        [DllImport(Security)]internal static extern int SecItemAdd(nint attributes,nint result);
        [DllImport(Core)]internal static extern nint CFDictionaryCreateMutable(nint allocator,nint capacity,nint keyCallbacks,nint valueCallbacks);
        [DllImport(Core)]internal static extern void CFDictionarySetValue(nint dictionary,nint key,nint value);
        [DllImport(Core)]internal static extern nint CFStringCreateWithCString(nint allocator,[MarshalAs(UnmanagedType.LPUTF8Str)]string value,uint encoding);
        [DllImport(Core)]internal static extern nint CFDataCreate(nint allocator,byte[] bytes,nint length);
        [DllImport(Core)]internal static extern nint CFDataGetLength(nint data);
        [DllImport(Core)]internal static extern nint CFDataGetBytePtr(nint data);
        [DllImport(Core)]internal static extern nuint CFDataGetTypeID();
        [DllImport(Core)]internal static extern nuint CFGetTypeID(nint value);
        [DllImport(Core)]internal static extern void CFRelease(nint value);
    }
}
