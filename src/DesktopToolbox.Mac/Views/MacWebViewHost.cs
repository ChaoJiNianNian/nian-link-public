using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using DesktopToolbox.Mac.Models;

namespace DesktopToolbox.Mac.Views;

/// <summary>Only hosts the shared local UI. Native data is passed as JSON arguments, never executable script.</summary>
public abstract class MacWebViewHost : NativeControlHost
{
    private const string Library = "libDeskLinkWeb.dylib";
    private static readonly NativeCallback Callback = Receive;
    private GCHandle context;
    private IntPtr view;
    protected bool BridgeReady { get; set; }
    protected static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeCallback(IntPtr context, IntPtr json);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr nian_web_create([MarshalAs(UnmanagedType.LPUTF8Str)] string page, NativeCallback callback, IntPtr context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nian_web_post(IntPtr view, [MarshalAs(UnmanagedType.LPUTF8Str)] string kind, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nian_web_destroy(IntPtr view);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int nian_web_drag(IntPtr view);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nian_web_attach(IntPtr view, IntPtr parent);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void nian_web_resize(IntPtr view, double width, double height);
    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (view != IntPtr.Zero) nian_web_resize(view, finalSize.Width, finalSize.Height);
        return result;
    }
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var page = Path.Combine(AppContext.BaseDirectory, "CollaborationUi", "index.html");
        context = GCHandle.Alloc(this);
        try
        {
            view = nian_web_create(page, Callback, GCHandle.ToIntPtr(context));
            if (view == IntPtr.Zero) throw new InvalidOperationException("协同界面资源未能加载，请重新安装完整应用。");
            nian_web_attach(view, parent.Handle);
            nian_web_resize(view, Bounds.Width, Bounds.Height);
            return new PlatformHandle(view, "NSView");
        }
        catch { if (view != IntPtr.Zero) { nian_web_destroy(view); view = IntPtr.Zero; } if (context.IsAllocated) context.Free(); throw; }
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        BridgeReady = false;
        if (view != IntPtr.Zero) { nian_web_destroy(view); view = IntPtr.Zero; }
        if (context.IsAllocated) context.Free();
    }
    private static void Receive(IntPtr context, IntPtr json)
    {
        try
        {
            if (json == IntPtr.Zero || GCHandle.FromIntPtr(context).Target is not MacWebViewHost host) return;
            var text = Marshal.PtrToStringUTF8(json);
            if (text is not null && MacWebRequest.TryParse(text, out var request)) Dispatcher.UIThread.Post(() => host.OnWebRequest(request!));
        }
        catch (Exception) { /* No managed exception may cross a native C callback. */ }
    }
    protected abstract void OnWebRequest(MacWebRequest request);
    protected void PostWeb(string kind, object value)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (view != IntPtr.Zero) nian_web_post(view, kind, JsonSerializer.Serialize(value, WebJson));
    }
    protected bool BeginNativeDrag() => view != IntPtr.Zero && nian_web_drag(view) == 1;
}



