using System.Windows;
using DragEventArgs=System.Windows.DragEventArgs;
using DragEventHandler=System.Windows.DragEventHandler;
using DragDropEffects=System.Windows.DragDropEffects;
using DataFormats=System.Windows.DataFormats;

namespace DesktopToolbox.App.DeskLink;

/// <summary>One native owner for file drops over every child of the mini window.</summary>
internal sealed class MiniFileDropTarget : IDisposable
{
    private readonly FrameworkElement host;
    private readonly Action<bool> feedback;
    private readonly Func<IReadOnlyList<string>,Task> send;
    private readonly Action<Exception> failed;
    private readonly bool previousAllowDrop;
    private bool active,disposed;

    public MiniFileDropTarget(FrameworkElement host,Action<bool> feedback,Func<IReadOnlyList<string>,Task> send,Action<Exception> failed)
    {
        this.host=host;this.feedback=feedback;this.send=send;this.failed=failed;
        previousAllowDrop=host.AllowDrop;host.AllowDrop=true;
        host.AddHandler(DragDrop.PreviewDragEnterEvent,new DragEventHandler(OnOver),true);
        host.AddHandler(DragDrop.PreviewDragOverEvent,new DragEventHandler(OnOver),true);
        host.AddHandler(DragDrop.PreviewDragLeaveEvent,new DragEventHandler(OnLeave),true);
        host.AddHandler(DragDrop.PreviewDropEvent,new DragEventHandler(OnDrop),true);
    }
    private static bool CanCopy(DragEventArgs args)
        => (args.AllowedEffects&DragDropEffects.Copy)!=0&&args.Data.GetDataPresent(DataFormats.FileDrop,false);
    private void SetActive(bool value)
    {if(active==value)return;active=value;feedback(value);}
    public void Reset()=>SetActive(false);
    private void OnOver(object sender,DragEventArgs args)
    {
        if(disposed)return;
        args.Handled=true;
        try{var accepted=CanCopy(args);args.Effects=accepted?DragDropEffects.Copy:DragDropEffects.None;SetActive(accepted);}
        catch(Exception error){args.Effects=DragDropEffects.None;SetActive(false);failed(error);}
    }
    private void OnLeave(object sender,DragEventArgs args)
    {
        if(disposed)return;
        args.Handled=true;
        // OLE DragLeave has no current pointer coordinates, including when the source cancels.
        // A subsequent Enter/Over restores feedback when moving between child targets.
        SetActive(false);
    }
    private async void OnDrop(object sender,DragEventArgs args)
    {
        if(disposed)return;
        args.Handled=true;args.Effects=DragDropEffects.None;SetActive(false);
        try
        {
            if(!CanCopy(args))return;
            if(args.Data.GetData(DataFormats.FileDrop,false) is not string[] offered||offered.Length is <1 or >256)
                throw new ArgumentException("一次请拖入 1 至 256 个文件或文件夹。");
            // The OS data object is valid only during Drop. Copy before yielding to the send queue.
            var paths=offered.ToArray();
            if(paths.Any(path=>string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path)||!File.Exists(path)&&!Directory.Exists(path)))
                throw new ArgumentException("无法读取拖入的文件或文件夹，请确认它仍可访问。");
            args.Effects=DragDropEffects.Copy;
            await send(paths);
        }
        catch(Exception error){failed(error);}
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        host.RemoveHandler(DragDrop.PreviewDragEnterEvent,new DragEventHandler(OnOver));
        host.RemoveHandler(DragDrop.PreviewDragOverEvent,new DragEventHandler(OnOver));
        host.RemoveHandler(DragDrop.PreviewDragLeaveEvent,new DragEventHandler(OnLeave));
        host.RemoveHandler(DragDrop.PreviewDropEvent,new DragEventHandler(OnDrop));
        host.AllowDrop=previousAllowDrop;SetActive(false);
    }
}
