using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace DesktopToolbox.Mac.Services.Display;
internal static class MacIoKit
{
    internal const string Library="/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation="/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<bool> Available=new(()=>NativeLibrary.TryLoad(Library,out var handle) && new[]{"IOAVServiceCreateWithService","IOAVServiceCopyEDID","IOAVServiceReadI2C","IOAVServiceWriteI2C"}.All(symbol=>NativeLibrary.TryGetExport(handle,symbol,out _)));
    internal static void EnsureAvailable()
    {
        if(!OperatingSystem.IsMacOS()||RuntimeInformation.ProcessArchitecture!=Architecture.Arm64)throw new PlatformNotSupportedException("macos_arm64_required");
        if(!Available.Value)throw new PlatformNotSupportedException("ioav_symbols_unavailable");
    }
    [DllImport(Library)] internal static extern nint IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library)] internal static extern int IOServiceGetMatchingServices(uint mainPort,nint matching,out uint iterator);
    [DllImport(Library)] internal static extern nint IORegistryEntryIDMatching(ulong id);
    [DllImport(Library)] internal static extern uint IOServiceGetMatchingService(uint mainPort,nint matching);
    [DllImport(Library)] internal static extern uint IOIteratorNext(uint iterator);
    [DllImport(Library)] internal static extern int IOObjectRelease(uint handle);
    [DllImport(Library)] internal static extern int IORegistryEntryGetRegistryEntryID(uint entry,out ulong id);
    [DllImport(Library)] internal static extern int IORegistryEntryGetParentEntry(uint entry,[MarshalAs(UnmanagedType.LPUTF8Str)] string plane,out uint parent);
    [DllImport(Library)] internal static extern nint IORegistryEntryCreateCFProperty(uint entry,nint key,nint allocator,uint options);
    [DllImport(Library)] internal static extern nint IOAVServiceCreateWithService(nint allocator,uint service);
    [DllImport(Library)] internal static extern int IOAVServiceCopyEDID(nint service,out nint data);
    [DllImport(Library)] internal static extern int IOAVServiceReadI2C(nint service,uint chipAddress,uint offset,[Out] byte[] bytes,uint count);
    [DllImport(Library)] internal static extern int IOAVServiceWriteI2C(nint service,uint chipAddress,uint dataAddress,[In] byte[] bytes,uint count);
    [DllImport(CoreFoundation)] internal static extern void CFRelease(nint value);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCString(nint allocator,[MarshalAs(UnmanagedType.LPUTF8Str)] string value,uint encoding);
    [DllImport(CoreFoundation)] private static extern byte CFStringGetCString(nint value,[Out] byte[] buffer,nint size,uint encoding);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFDataGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(nint data);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetBytePtr(nint data);
    internal static string? Property(uint entry,string key)
    {
        using var name=new CfReference(CFStringCreateWithCString(0,key,0x08000100));
        if(name.IsInvalid)return null;
        using var value=new CfReference(IORegistryEntryCreateCFProperty(entry,name.DangerousGetHandle(),0,0));
        if(value.IsInvalid||CFGetTypeID(value.DangerousGetHandle())!=CFStringGetTypeID())return null;
        byte[] buffer=new byte[1024];
        if(CFStringGetCString(value.DangerousGetHandle(),buffer,buffer.Length,0x08000100)==0)return null;
        int length=Array.IndexOf(buffer,(byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer,0,length<0?buffer.Length:length);
    }
    internal static MacEdidInfo? Edid(nint service)
    {
        int error=IOAVServiceCopyEDID(service,out var pointer);
        using var data=new CfReference(pointer);
        if(error!=0||data.IsInvalid||CFGetTypeID(pointer)!=CFDataGetTypeID())return null;
        long length=(long)CFDataGetLength(pointer);
        if(length<128||length>4096)return null;
        var buffer=new byte[(int)length];
        Marshal.Copy(CFDataGetBytePtr(pointer),buffer,0,buffer.Length);
        return MacEdid.TryParse(buffer,out var info)?info:null;
    }
}
internal sealed class CfReference : SafeHandleZeroOrMinusOneIsInvalid
{
    internal CfReference(nint value):base(true)=>SetHandle(value);
    protected override bool ReleaseHandle(){MacIoKit.CFRelease(handle);return true;}
}
internal sealed class IoReference : SafeHandleZeroOrMinusOneIsInvalid
{
    internal IoReference(uint value):base(true)=>SetHandle((nint)(long)value);
    internal uint Value=>(uint)handle.ToInt64();
    protected override bool ReleaseHandle()=>MacIoKit.IOObjectRelease(Value)==0;
}
