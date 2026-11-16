using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DesktopToolbox.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DesktopToolbox.App.DeskLink;

internal sealed class MiniCollaborationWebSurface : IDisposable
{
    private static WebView2CompositionControl CreateWeb()=>new(){DefaultBackgroundColor=System.Drawing.Color.Transparent,AllowDrop=true,AllowExternalDrop=false};
    private WebView2CompositionControl web=CreateWeb();
    private readonly Grid host=new(){Background=System.Windows.Media.Brushes.Transparent};
    private readonly MiniFileDropTarget fileDropTarget;
    private readonly Queue<long> recoveries=new();
    private bool recovering;
    private readonly string profile;
    private readonly Func<MiniBridgeRequest,IReadOnlyList<string>,CancellationToken,Task<object?>> invoke;
    private readonly Action<Exception> log;
    private readonly Action ready;
    private readonly HashSet<string> requests=[];
    private CancellationTokenSource pageLifetime=new();
    private bool disposed,pageReady;
    internal bool IsPageReady => !disposed && pageReady;
    private string epoch=Guid.NewGuid().ToString("N");
    public string Epoch=>epoch;
    public FrameworkElement View=>host;
    public MiniCollaborationWebSurface(string profile,Func<MiniBridgeRequest,IReadOnlyList<string>,CancellationToken,Task<object?>> invoke,Action ready,Action<Exception> log)
    {
        this.profile=profile;this.invoke=invoke;this.ready=ready;this.log=log;host.Children.Add(web);
        fileDropTarget=new(host,over=>_ = PageScriptAsync("window.nianNativeDrag?.("+(over?"true":"false")+");"),SendNativeDropAsync,NativeDropFailed);
    }
    private async Task PageScriptAsync(string script)
    {
        if(disposed||!pageReady)return;
        try{await web.CoreWebView2.ExecuteScriptAsync(script);}
        catch(Exception error) when(error is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {if(!disposed)log(error);}
    }
    private void NativeDropFailed(Exception error)
    {
        if(error is not OperationCanceledException)log(error);
        _ = PageScriptAsync("window.nianNativeTransferResult?.("+JsonSerializer.Serialize(new {ok=false,error=error is OperationCanceledException?"操作已取消。":error.Message})+");");
    }
    private async Task SendNativeDropAsync(IReadOnlyList<string> paths)
    {
        if(disposed||!pageReady)throw new InvalidOperationException("小窗正在加载，请稍后再拖入文件。");
        var requestEpoch=epoch;var token=pageLifetime.Token;
        try
        {
            await PageScriptAsync("window.nianPrepareTransfer?.("+paths.Count+");");
            var payload=JsonSerializer.SerializeToElement(new {names=paths.Select(Path.GetFileName).ToArray()});
            await invoke(new(Guid.NewGuid().ToString("N"),"files.drop",payload),paths,token);
            if(!disposed&&epoch==requestEpoch&&!token.IsCancellationRequested)
                await PageScriptAsync("window.nianNativeTransferResult?.({ok:true,queued:true});");
        }
        catch(Exception error)
        {
            if(error is not OperationCanceledException)log(error);
            if(!disposed&&epoch==requestEpoch&&!token.IsCancellationRequested)
                await PageScriptAsync("window.nianNativeTransferResult?.("+JsonSerializer.Serialize(new {ok=false,error=error.Message})+");");
        }
    }
    public async Task InitializeAsync()
    {
        var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:profile);
        if(disposed)return;
        await web.EnsureCoreWebView2Async(environment);if(disposed)return;
        var core=web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("nian-collaboration.local",Path.Combine(AppContext.BaseDirectory,"CollaborationUi"),CoreWebView2HostResourceAccessKind.Deny);
        core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=false;core.Settings.IsStatusBarEnabled=false;core.Settings.IsZoomControlEnabled=false;
        core.PermissionRequested+=(_,args)=>args.State=CoreWebView2PermissionState.Deny;
        core.DownloadStarting+=(_,args)=>args.Cancel=true;core.NewWindowRequested+=(_,args)=>args.Handled=true;
        core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
        core.WebResourceRequested+=(_,args)=>
        {
            if(!IsResource(args.Request.Uri))args.Response=environment.CreateWebResourceResponse(null,403,"Blocked","");
        };
        core.NavigationStarting+=(_,args)=>{if(!MiniBridgePolicy.IsPage(args.Uri)){args.Cancel=true;return;}Reset();};
        core.ProcessFailed+=(_,args)=>_ = RecoverAsync(args.ProcessFailedKind);
        core.WebMessageReceived+=OnMessage;
        // Only the trusted Windows document receives this local platform stylesheet.
        await core.AddScriptToExecuteOnDocumentCreatedAsync("""
            (() => {
                if (window.top !== window || location.href !== 'https://nian-collaboration.local/index.html') return;
                const apply = () => {
                    const link = document.createElement('link');
                    link.rel = 'stylesheet';
                    link.href = 'https://nian-collaboration.local/mini-windows.css';
                    link.dataset.nianWindowsSurface = '';
                    document.head.append(link);
                };
                if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', apply, { once: true });
                else apply();
            })();
            """);
        if(disposed)return;
        core.Navigate(MiniBridgePolicy.Page);
    }
    private static bool IsResource(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="nian-collaboration.local"||uri.Port!=443||uri.Query.Length!=0||uri.UserInfo.Length!=0)return false;
        return uri.AbsolutePath is "/index.html" or "/mini.css" or "/mini.js" or "/mini-windows.css" or "/assets/lucide.min.js";
    }
    private void Reset()
    {pageReady=false;fileDropTarget.Reset();pageLifetime.Cancel();pageLifetime.Dispose();pageLifetime=new();requests.Clear();epoch=Guid.NewGuid().ToString("N");}
    private async void OnMessage(object? sender,CoreWebView2WebMessageReceivedEventArgs args)
    {
        if(disposed||!MiniBridgePolicy.IsPage(args.Source))return;
        MiniBridgeRequest request;
        try{request=MiniBridgePolicy.Parse(args.WebMessageAsJson);}catch(Exception error) when(error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException){log(error);return;}
        if(requests.Contains(request.Id))return;
        if(requests.Count>=64){Post(new {id=request.Id,ok=false,error="正在处理的操作较多，请稍后再试。",epoch});return;}
        requests.Add(request.Id);
        var requestEpoch=epoch;var token=pageLifetime.Token;
        try
        {
            IReadOnlyList<string> paths=[];
            if(request.Action=="files.drop")
            {
                if(args.AdditionalObjects is null||args.AdditionalObjects.Count is <1 or >256)throw new ArgumentException("请从资源管理器直接拖入文件，或使用选择文件按钮。");
                var values=new List<string>();
                foreach(var item in args.AdditionalObjects)
                {
                    if(item is not CoreWebView2File file||!Path.IsPathFullyQualified(file.Path)||!File.Exists(file.Path)&&!Directory.Exists(file.Path))throw new ArgumentException("无法读取拖入的文件。");
                    values.Add(file.Path);
                }
                paths=values;
            }
            if(request.Action=="ready")pageReady=true;
            var result=request.Action=="ready"?new {epoch}:await invoke(request,paths,token);
            if(!disposed&&epoch==requestEpoch&&!token.IsCancellationRequested)
            {Post(new {id=request.Id,ok=true,result,epoch=requestEpoch});if(request.Action=="ready")ready();}
        }
        catch(Exception error)
        {
            if(error is not OperationCanceledException)log(error);
            if(!disposed&&epoch==requestEpoch&&!token.IsCancellationRequested)Post(new {id=request.Id,ok=false,error=error is OperationCanceledException?"操作已取消。":error.Message,epoch=requestEpoch});
        }
        finally{if(epoch==requestEpoch)requests.Remove(request.Id);}
    }
    internal async Task RecoverAsync(CoreWebView2ProcessFailedKind kind)
    {
        if(disposed||recovering)return;
        recovering=true;Reset();log(new IOException("互联小窗页面异常："+kind));
        var now=Environment.TickCount64;
        while(recoveries.TryPeek(out var previous)&&now-previous>60000)recoveries.Dequeue();
        if(recoveries.Count>=2){ShowRecoveryButton();recovering=false;return;}
        recoveries.Enqueue(now);
        try
        {
            if(kind==CoreWebView2ProcessFailedKind.BrowserProcessExited)await RebuildAsync();
            else web.CoreWebView2.Reload();
        }
        catch(Exception error){log(error);ShowRecoveryButton();}
        finally{recovering=false;}
    }
    private async Task RebuildAsync()
    {
        web.Dispose();host.Children.Clear();web=CreateWeb();host.Children.Add(web);await InitializeAsync();
    }
    private void ShowRecoveryButton()
    {
        if(disposed)return;
        var button=new System.Windows.Controls.Button{Content="小窗暂未响应，点击重新加载",Margin=new Thickness(12),Padding=new Thickness(16)};
        button.Click+=async(_,_)=>{button.IsEnabled=false;recoveries.Clear();try{await RebuildAsync();}catch(Exception error){log(error);ShowRecoveryButton();}};
        host.Children.Clear();host.Children.Add(button);
    }
    public void PublishState(object state)=>Post(new {type="state",state});
    private void Post(object message)
    {
        if(disposed||!pageReady)return;
        try{web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message,JsonFileStore.JsonOptions));}
        catch(Exception error) when(error is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException){if(!disposed)log(error);}
    }
    internal Task CaptureAsync(Stream stream)=>web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,stream);
    internal Task<string> InspectAsync(string script)=>web.CoreWebView2.ExecuteScriptAsync(script);
    public void Dispose(){if(disposed)return;disposed=true;pageReady=false;fileDropTarget.Dispose();pageLifetime.Cancel();pageLifetime.Dispose();web.Dispose();}
}
