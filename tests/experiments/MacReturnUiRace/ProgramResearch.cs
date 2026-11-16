#if NIAN_MAC_RETURN_RESEARCH
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using DesktopToolbox.DeskLink;
using DesktopToolbox.Mac.Views;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        private static async Task VerifyResearchAsync()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var path = Path.Combine(Path.GetDirectoryName(resultPath)!, "research-" + Guid.NewGuid().ToString("N"));
            var main = new MacMultiDeviceWindow(path, false, new FakeVault());
            var type = typeof(MacMultiDeviceWindow);
            var ensure = type.GetMethod("EnsureResearchReturnWindow", flags) ?? throw new InvalidOperationException("Research preflight not implemented.");
            var close = type.GetMethod("CloseRemoteReturn", flags)!;
            var update = type.GetMethod("UpdateRemoteReturn", flags)!;
            var retire = type.GetMethod("TryReleaseResearchReturn", flags) ?? throw new InvalidOperationException("Research release not implemented.");
            var field = type.GetField("remoteReturn", flags)!;
            var factory = type.GetField("researchWindowFactory", flags) ?? throw new InvalidOperationException("Research factory seam missing.");
            var backend = new Backend();
            var assembly = type.Assembly;
            var ownerType = assembly.GetType("NianLink.MultiDevice.PeerControlOwner", true)!;
            var owner = Activator.CreateInstance(ownerType, nonPublic: true)!;
            ownerType.GetMethod("Bind", flags)!.Invoke(owner, [backend]);
            var guardType = assembly.GetType("NianLink.MultiDevice.GuardedPeerControl", true)!;
            var switches = 0;
            var rawReturnCalls = 0;
            Func<string, CancellationToken, Task<DeskLinkSwitchResult>> switcher = (target, _) =>
            { switches++; return Task.FromResult(new DeskLinkSwitchResult { Ok = true, InputTransferred = true, Target = target }); };
            var guard = (IDeskflowPreviewControl)Activator.CreateInstance(guardType, flags, null, [owner, backend, switcher, "host", "peer"], null)!;
            Task<DeskflowPreviewState>? pending = null;
            try
            {
                if (mode == "research-failure")
                {
                    factory.SetValue(main, (Func<MacRemotePreviewReturnWindow>)(() => throw new InvalidOperationException("synthetic window failure")));
                    try { ensure.Invoke(main, null); throw new InvalidOperationException("Missing-window preflight succeeded."); }
                    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException { Message: "synthetic window failure" }) { }
                    if (field.GetValue(main) is not null) throw new InvalidOperationException("Failed window was published.");
                    return;
                }
                if(mode=="research-show-failure-cleanup")
                {
                    var creates=0;
                    EventHandler<WindowClosingEventArgs> veto=(_,e)=>e.Cancel=true;
                    factory.SetValue(main,(Func<MacRemotePreviewReturnWindow>)(()=>
                    {
                        creates++;
                        MacRemotePreviewReturnWindow? candidate=null;
                        candidate=new MacRemotePreviewReturnWindow("",_=>Task.CompletedTask,candidateBlue:true,persistentResearch:true,
                            researchDismissAction:()=>type.GetMethod("DismissResearchReturn",flags)!.Invoke(main,[candidate]));
                        candidate.Opened+=(_,_)=>throw new InvalidOperationException("synthetic show failure");
                        candidate.Closing+=veto;
                        return candidate;
                    }));
                    try { ensure.Invoke(main,null); throw new InvalidOperationException("Show failure was ignored."); }
                    catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException {Message:"synthetic show failure"}) { }
                    var retained=field.GetValue(main) as MacRemotePreviewReturnWindow;
                    if(retained is not {IsVisible:true})throw new InvalidOperationException("Visible failed window lost its reference.");
                    try { ensure.Invoke(main,null); throw new InvalidOperationException("Unready window was reused."); }
                    catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException {Message:"研究入口不可用，请先安全清理。"}) { }
                    if(creates!=1)throw new InvalidOperationException("Failed cleanup created a duplicate.");
                    retained.Closing-=veto;
                    var dismissButton=(Button)typeof(MacRemotePreviewReturnWindow).GetField("researchDismiss",flags)!.GetValue(retained)!;
                    if(!dismissButton.IsVisible)throw new InvalidOperationException("Unready retained entry has no visible dismissal.");
                    dismissButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                    if(field.GetValue(main) is not null)throw new InvalidOperationException("Failed window cannot be dismissed via the visible action.");
                    return;
                }
                var factoryCalls=0; var reentrantRejected=false; var releaseBlocked=false;
                factory.SetValue(main, (Func<MacRemotePreviewReturnWindow>)(() =>
                {
                    factoryCalls++;
                    var candidate=new MacRemotePreviewReturnWindow("synthetic-old-id", _ => { rawReturnCalls++; return Task.CompletedTask; }, candidateBlue:true, persistentResearch:true);
                    if(mode=="research-reentrant" && factoryCalls==1)
                        candidate.Opened+=(_,_)=>{ releaseBlocked=!(bool)retire.Invoke(main,[false])!; try { ensure.Invoke(main,null); } catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException) { reentrantRejected=true; }};
                    return candidate;
                }));
                ensure.Invoke(main, null);
                if(mode=="research-reentrant" && (!reentrantRejected || !releaseBlocked || factoryCalls!=1))throw new InvalidOperationException("Reentrant Show admitted a second window.");
                var window = field.GetValue(main) as MacRemotePreviewReturnWindow ?? throw new InvalidOperationException("Missing window.");
                if (!window.IsVisible || window.Title != MacRemotePreviewReturnWindow.CaptureTitle) throw new InvalidOperationException("Entry not visible or title incorrect.");
                var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
                if (lifetime.Windows.Count(w => w.IsVisible && w.Title == MacRemotePreviewReturnWindow.CaptureTitle) != 1)
                    throw new InvalidOperationException("Research entry is not unique.");
                var buttonField=typeof(MacRemotePreviewReturnWindow).GetField("button",flags)!;
                var statusButton=(Button)buttonField.GetValue(window)!;
                if(statusButton.Content is not string status || !status.Contains("未确认",StringComparison.Ordinal) || !status.Contains("连接及归还未确认",StringComparison.Ordinal) || !status.Contains("此处不发送归还",StringComparison.Ordinal))
                    throw new InvalidOperationException("Entry does not visibly describe the unconfirmed non-return state.");
                var buttonAction = typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync", flags)!;
                if (mode is "research-poll" or "research-button" or "research-stop")
                {
                    pending = guard.BeginPreviewAsync();
                    await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (mode == "research-poll") update.Invoke(main, [null, guard]);
                    if (mode == "research-button") await ((Task)buttonAction.Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                    if (mode == "research-stop")
                    {
                        ownerType.GetMethod("BeginStopping", flags)!.Invoke(owner, [backend]);
                        close.Invoke(main, null);
                        var unresolved = (bool)ownerType.GetProperty("HasUnresolvedOperation", flags)!.GetValue(owner)!;
                        if (!unresolved || (bool)retire.Invoke(main, [unresolved])!) throw new InvalidOperationException("Unsafe stop discarded unresolved entry.");
                        try { await guard.BeginPreviewAsync(); throw new InvalidOperationException("Begin accepted after stop."); } catch (InvalidOperationException e) when (e.Message != "Begin accepted after stop.") { }
                    }
                    if (!ReferenceEquals(field.GetValue(main), window) || !window.IsVisible) throw new InvalidOperationException("Unresolved operation lost entry.");
                    if (backend.EndCalls != 0 || switches != 0 || rawReturnCalls != 0) throw new InvalidOperationException("Research action issued old ID or display switch.");
                }
                else if (mode == "research-generation")
                {
                    if (!(bool)retire.Invoke(main, [false])!) throw new InvalidOperationException("Clean entry could not retire.");
                    ensure.Invoke(main, null);
                    var next = field.GetValue(main) as Window ?? throw new InvalidOperationException("Next entry missing.");
                    if (ReferenceEquals(next, window) || !next.IsVisible || window.IsVisible) throw new InvalidOperationException("Generation left zero or duplicate entry.");
                    if (lifetime.Windows.Count(w => w.IsVisible && w.Title == MacRemotePreviewReturnWindow.CaptureTitle) != 1)
                        throw new InvalidOperationException("Generation produced multiple capture titles.");
                    update.Invoke(main, ["old", backend]);
                    if (!ReferenceEquals(field.GetValue(main), next) || !next.IsVisible) throw new InvalidOperationException("Late callback replaced next entry.");
                    await ((Task)buttonAction.Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(5));
                    if (!ReferenceEquals(field.GetValue(main), next) || !next.IsVisible || backend.EndCalls != 0 || switches != 0 || rawReturnCalls != 0) throw new InvalidOperationException("Old window acted on next generation.");
                }
                else if (mode == "research-cancel-release")
                {
                    EventHandler<Avalonia.Controls.WindowClosingEventArgs> veto = (_,e)=>e.Cancel=true;
                    window.Closing += veto;
                    if ((bool)retire.Invoke(main, [false])! || !window.IsVisible || !ReferenceEquals(field.GetValue(main),window))
                        throw new InvalidOperationException("Canceled release lost the live entry reference.");
                    window.Closing -= veto;
                    if (!(bool)retire.Invoke(main, [false])! || field.GetValue(main) is not null)
                        throw new InvalidOperationException("Retry could not release the entry.");
                }
                else if (mode == "research-collapse")
                {
                    var toggle = typeof(MacRemotePreviewReturnWindow).GetMethod("ToggleResearchCollapsed", flags) ?? throw new InvalidOperationException("No foldable entry.");
                    var initial = window.Width;
                    var expandedFold=(Button)typeof(MacRemotePreviewReturnWindow).GetField("researchFold",flags)!.GetValue(window)!;
                    if(statusButton.Width + expandedFold.Width > window.Width || window.Height < 68) throw new InvalidOperationException("Expanded status entry is clipped by its window size.");
                    toggle.Invoke(window, null);
                    if (!window.IsVisible || window.Width >= initial) throw new InvalidOperationException("Entry did not fold visibly.");
                    var fold=(Button)typeof(MacRemotePreviewReturnWindow).GetField("researchFold",flags)!.GetValue(window)!;
                    if(fold.Content is not string folded || !folded.Contains("未确认",StringComparison.Ordinal) || !fold.IsVisible || fold.Width>window.Width)
                        throw new InvalidOperationException("Collapsed entry hides unconfirmed status or cannot be expanded.");
                    toggle.Invoke(window, null);
                    if (!window.IsVisible || window.Width != initial) throw new InvalidOperationException("Entry did not unfold.");
                    window.Close();
                    if (!window.IsVisible || !ReferenceEquals(field.GetValue(main), window)) throw new InvalidOperationException("Entry could be dismissed during connection.");
                }
            }
            finally
            {
                backend.Complete();
                if (pending is not null) try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                var w = field.GetValue(main) as Window;
                if (w is not null) retire.Invoke(main, [false]);
            }
        }
    }
}
#endif
