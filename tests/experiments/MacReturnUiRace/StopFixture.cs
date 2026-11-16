using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Views;
using Nian.Input.Security;

internal static partial class Program
{
    private sealed partial class FixtureApp
    {
        // Real candidate window, real Mac owner/session and collaboration service;
        // process, IPC, identity vault and media are disposable synthetic resources.
        private static async Task VerifyStopLifecycleAsync()
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
            var profile=Path.Combine(Path.GetDirectoryName(resultPath)!,"stop-lifecycle-"+Guid.NewGuid().ToString("N"));
            var engine=Path.Combine(profile,"synthetic-engine");
            Directory.CreateDirectory(profile);
            await File.WriteAllBytesAsync(engine,[1]);
            var identities=new MemoryIdentityVault();
            using var ipc=await SyntheticIpc.CreateAsync();
            using var ipc2=mode is "research-start-repeat" or "research-stop-reuse"?await SyntheticIpc.CreateAsync():null;
            var process=new SyntheticProcess { FailStop=mode is "startstopfailure" or "research-stop-failed-core" };
            var process2=new SyntheticProcess();
            var processStarts=0;
            var dependencies=new DeskflowSessionDependencies
            {
                HasConflictingCore=_=>false,
                StartProcessAsync=(_,_)=>{processStarts++;return Task.FromResult<IDeskflowOwnedProcess>(processStarts==2 && ipc2 is not null?process2:process);},
                ConnectCoreIpcAsync=(_,_)=>Task.FromResult<Stream>(processStarts==2 && ipc2 is not null?ipc2.Client.GetStream():ipc.Client.GetStream())
            };
            await using var owner=await MacMultiPeerClient.OpenAsync(Path.Combine(profile,"mac"),engine,identities,dependencies);
            var hostName=owner.DeviceName=="SyntheticHost123"?"SyntheticHost456":"SyntheticHost123";
            var invitation=new MultiPeerInvitation
            {
                Host="127.0.0.1",Port=24821,HostName=hostName,PeerName=owner.DeviceName,
                CertificateSha256=new string('A',64),Token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            };
            var review=await owner.ReviewInvitationAsync(invitation.ToJson());
            await owner.ConfirmInvitationAsync(review.Id,invitation.CertificateSha256);
            var startingMode=mode is "research-stop-reuse" or "research-start-repeat" or "research-start-bound-failure" or "research-start-preflight" or "startfailure" or "startconcurrent" or "startsuccess" or "startcleanfailure" or "startstopfailure";
            if(!startingMode)await owner.StartAsync();
            var media=new SyntheticMedia { Fail=mode is "research-start-bound-failure" or "research-stop-failure" or "research-close-failure" or "disposefailure" or "startfailure" or "startconcurrent" or "closefailure", Block=mode is "research-start-bound-failure" or "research-stop-bound-pending" or "disposepending" or "startconcurrent" };
            var collaboration=new MacCollaborationService(Path.Combine(profile,"collaboration"),_=>Task.FromResult<DeskLinkPairingBundle?>(null),media);
            var main=new MacMultiDeviceWindow(Path.Combine(profile,"main"),false,identities);
            typeof(MacMultiDeviceWindow).GetField("engine",flags)!.SetValue(main,engine);
            typeof(MacMultiDeviceWindow).GetField("client",flags)!.SetValue(main,owner);
            if(!startingMode)typeof(MacMultiDeviceWindow).GetField("collaboration",flags)!.SetValue(main,collaboration);
            var stop=typeof(MacMultiDeviceWindow).GetMethod("StopAsync",flags)!;
#if NIAN_MAC_RETURN_RESEARCH
            if(mode is "research-close-ready" or "research-close-allow" or "research-close-cancel" or "research-stop-failed-core" or "research-stop-late-begin" or "research-stop-bound-pending" or "research-stop-failure" or "research-close-failure" or "research-stop-inflight")
                typeof(MacMultiDeviceWindow).GetMethod("EnsureResearchReturnWindow",flags)!.Invoke(main,null);
#endif
            try
            {
                if(mode is "research-close-ready" or "research-close-allow" or "research-close-cancel" or "research-close-failure" or "closefailure" or "closesuccess")
                {
                    if(mode=="research-close-ready")identities.BlockNextLoad=true;
                    var observedOpened=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    main.Opened+=(_,_)=>observedOpened.TrySetResult();
                    main.Show();
                    await observedOpened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if(mode=="research-close-ready")
                    {
                        await identities.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        if(!(bool)typeof(MacMultiDeviceWindow).GetField("busy",flags)!.GetValue(main)! || identities.Release.Task.IsCompleted)
                            throw new InvalidOperationException("The controlled initialization was not pending.");
                        // Keep initialization pending beyond the old 500 ms assumption.
                        _=Task.Run(async()=>{await Task.Delay(2000);identities.Release.TrySetResult();});
                    }
                    var clientField=typeof(MacMultiDeviceWindow).GetField("client",flags)!;
                    var busyField=typeof(MacMultiDeviceWindow).GetField("busy",flags)!;
                    var readyDeadline=System.Diagnostics.Stopwatch.StartNew();
                    while((bool)busyField.GetValue(main)! || clientField.GetValue(main) is not MacMultiPeerClient candidate || ReferenceEquals(candidate,owner))
                    {
                        if(readyDeadline.Elapsed>=TimeSpan.FromSeconds(10))throw new InvalidOperationException("Real window initialization did not finish with its own test client.");
                        await Task.Delay(10);
                    }
                    var opened=(MacMultiPeerClient)clientField.GetValue(main)!;
                    await opened.DisposeAsync();
                    clientField.SetValue(main,owner);
#if NIAN_MAC_RETURN_RESEARCH
                    if(mode is "research-close-ready" or "research-close-allow" or "research-close-cancel")
                    {
                        var controlOwner=typeof(MacMultiPeerClient).GetField("previewOwner",flags)!.GetValue(owner)!;
                        var keyboard=typeof(MacMultiPeerClient).GetField("guardedKeyboard",flags)!.GetValue(owner)!;
                        controlOwner.GetType().GetMethod("Enter",flags)!.Invoke(controlOwner,[keyboard,true,false,null]);
                        typeof(MacMultiDeviceWindow).GetField("researchExitConfirmation",flags)!.SetValue(main,(Func<Task<bool>>)(()=>Task.FromResult(mode is "research-close-ready" or "research-close-allow")));
                    }
                    if(mode=="research-close-failure")typeof(MacMultiDeviceWindow).GetField("researchExitConfirmation",flags)!.SetValue(main,(Func<Task<bool>>)(()=>Task.FromResult(true)));
#endif
                    var discovery=typeof(MacMultiDeviceWindow).GetField("discovery",flags)!.GetValue(main)!;
                    var closedField=discovery.GetType().GetField("closed",flags)!;
                    main.Close();
                    for(var i=0;i<100;i++)
                    {
                        await Task.Delay(50);
                        if((mode=="closefailure"||mode=="research-close-failure"||mode=="research-close-cancel")&&main.IsEnabled)break;
                        if((mode is "closesuccess" or "research-close-allow" or "research-close-ready") && !main.IsVisible)break;
                    }
                    if(mode=="research-close-cancel")
                    {
                        if(!main.IsVisible || !main.IsEnabled || process.StopCount!=0)throw new InvalidOperationException("Canceled exit did not retain the running synthetic connection.");
                    }
                    else if(mode is "closefailure" or "research-close-failure")
                    {
                        if(!main.IsVisible||!main.IsEnabled||process.StopCount!=0)throw new InvalidOperationException("Failed close did not preserve the active keyboard and an enabled main window.");
#if NIAN_MAC_RETURN_RESEARCH
                        if(mode=="research-close-failure" && typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main) is not MacRemotePreviewReturnWindow { IsVisible:true })
                            throw new InvalidOperationException("Failed app close discarded the research entry.");
#endif
                        if((bool)closedField.GetValue(discovery)!)throw new InvalidOperationException("Failed close permanently disposed discovery while keeping the window open.");
                        var feedback=(Avalonia.Controls.TextBlock)typeof(MacMultiDeviceWindow).GetField("feedback",flags)!.GetValue(main)!;
                        if(feedback.Text?.Contains("退出前无法安全停止连接",StringComparison.Ordinal)!=true)throw new InvalidOperationException("Failed close did not show its failure.");
                    }
                    else if(main.IsVisible||!(bool)closedField.GetValue(discovery)!||process.StopCount!=1)
                        throw new InvalidOperationException("Successful close did not stop keyboard and dispose discovery.");
                    return;
                }
                if(startingMode)
                {
#if NIAN_MAC_RETURN_RESEARCH
                    if(mode=="research-start-preflight")
                    {
                        typeof(MacMultiDeviceWindow).GetField("researchWindowFactory",flags)!.SetValue(main,(Func<MacRemotePreviewReturnWindow>)(()=>throw new InvalidOperationException("synthetic window failure")));
                        try { await ((Task)typeof(MacMultiDeviceWindow).GetMethod("StartAsync",flags)!.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Start bypassed entry creation failure."); }
                        catch(InvalidOperationException error) when(error.Message=="synthetic window failure") { }
                        if(processStarts!=0 || typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main) is not null)
                            throw new InvalidOperationException("Control started or entry published after window creation failed.");
                        return;
                    }
#endif
                    var factoryCalls=0;
                    if(mode is not ("startsuccess" or "research-start-repeat" or "research-stop-reuse"))collaboration.Changed+=_=>throw new InvalidOperationException("synthetic collaboration startup failure");
                    Func<string,Func<CancellationToken,Task<DeskLinkPairingBundle?>>,IDeskflowPreviewControl?,MacCollaborationService> factory=(_,_,_)=>{
                        factoryCalls++;
                        return (mode is "research-start-repeat" or "research-stop-reuse") && factoryCalls==2
                            ? new MacCollaborationService(Path.Combine(profile,"collaboration-second"),_=>Task.FromResult<DeskLinkPairingBundle?>(null),new SyntheticMedia())
                            : collaboration;
                    };
                    typeof(MacMultiDeviceWindow).GetField("collaborationFactory",flags)!.SetValue(main,factory);
                    var start=(Task)typeof(MacMultiDeviceWindow).GetMethod("StartAsync",flags)!.Invoke(main,null)!;
#if NIAN_MAC_RETURN_RESEARCH
                    if(mode is "research-start-repeat" or "research-stop-reuse")
                    {
                        await start.WaitAsync(TimeSpan.FromSeconds(10));
                        var type=typeof(MacMultiDeviceWindow);
                        var generation=(long)type.GetField("researchGeneration",flags)!.GetValue(main)!;
                        var backend=new DynamicBackend();
                        type.GetMethod("ApplyResearchObservation",flags)!.Invoke(main,[generation,"existing-session",backend]);
                        var entry=(MacRemotePreviewReturnWindow)type.GetField("remoteReturn",flags)!.GetValue(main)!;
                        try { await ((Task)type.GetMethod("StartAsync",flags)!.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Repeated start unexpectedly succeeded."); }
                        catch(InvalidOperationException error) when(error.Message is "Connection already started." or "当前连接尚未停止。") { }
                        if(!entry.IsVisible || !(bool)type.GetField("researchConnectionActive",flags)!.GetValue(main)! ||
                           type.GetField("researchGeneration",flags)!.GetValue(main) is not long actualGeneration || actualGeneration!=generation)
                            throw new InvalidOperationException("Repeated start discarded the active research binding.");
                        backend.Completion.SetResult(new DeskflowPreviewState());
                        await ((Task)typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync",flags)!.Invoke(entry,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                        if(backend.EndIds.Count!=1 || backend.EndIds[0]!="existing-session")throw new InvalidOperationException("Repeated start discarded the known return attempt.");
                        await ((Task)stop.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));
                        if((bool)type.GetField("researchConnectionActive",flags)!.GetValue(main)!)throw new InvalidOperationException("Successful stop retained an active generation.");
                        if(mode=="research-stop-reuse")
                        {
                            try { await ((Task)type.GetMethod("StartAsync",flags)!.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Restart silently discarded stopped warning."); }
                            catch(InvalidOperationException error) when(error.Message.Contains("自行关闭提示",StringComparison.Ordinal)) { }
                            if(!entry.IsVisible || !ReferenceEquals(type.GetField("remoteReturn",flags)!.GetValue(main),entry) || processStarts!=1)
                                throw new InvalidOperationException("Rejected restart failed to retain the stopped warning.");
                        }
                        type.GetMethod("DismissResearchReturn",flags)!.Invoke(main,[entry]);
                        if(type.GetField("remoteReturn",flags)!.GetValue(main) is not null)throw new InvalidOperationException("Stopped entry was not explicitly dismissed.");
                        await ((Task)type.GetMethod("StartAsync",flags)!.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));
                        if(processStarts!=2 || factoryCalls!=2 || !(bool)type.GetField("researchConnectionActive",flags)!.GetValue(main)!)
                            throw new InvalidOperationException("A clean stop prevented the same synthetic client from restarting.");
                        if(type.GetField("researchGeneration",flags)!.GetValue(main) is not long nextGeneration || nextGeneration==generation)
                            throw new InvalidOperationException("Restart did not create a new generation.");
                        var nextEntry=(MacRemotePreviewReturnWindow)type.GetField("remoteReturn",flags)!.GetValue(main)!;
                        var click=typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync",flags)!;
                        await ((Task)click.Invoke(entry,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                        await ((Task)click.Invoke(nextEntry,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                        if(backend.EndIds.Count!=1)throw new InvalidOperationException("A restart reused the old known session ID.");
                        var nextBackend=new DynamicBackend();nextBackend.Completion.SetResult(new DeskflowPreviewState());
                        type.GetMethod("ApplyResearchObservation",flags)!.Invoke(main,[nextGeneration,"next-session",nextBackend]);
                        await ((Task)click.Invoke(entry,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                        if(nextBackend.EndIds.Count!=0)throw new InvalidOperationException("The old window sent a new connection action.");
                        await ((Task)click.Invoke(nextEntry,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                        if(nextBackend.EndIds.Count!=1 || nextBackend.EndIds[0]!="next-session")throw new InvalidOperationException("The new entry could not attempt the new session.");
                        await ((Task)stop.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));
                        if(process2.StopCount!=1)throw new InvalidOperationException("The restarted synthetic client did not stop.");
                        return;
                    }
                    if(mode=="research-start-bound-failure")
                    {
                        await media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        var type=typeof(MacMultiDeviceWindow);
                        var generation=(long)type.GetField("researchGeneration",flags)!.GetValue(main)!;
                        var backend=new DynamicBackend();
                        type.GetMethod("ApplyResearchObservation",flags)!.Invoke(main,[generation,"during-start",backend]);
                        var entry=(MacRemotePreviewReturnWindow)type.GetField("remoteReturn",flags)!.GetValue(main)!;
                        media.Release.TrySetResult();
                        try { await start.WaitAsync(TimeSpan.FromSeconds(10)); throw new InvalidOperationException("Expected startup cleanup failure."); }
                        catch(InvalidOperationException error) when(error.Message.Contains("清理尚未确认",StringComparison.Ordinal) && HasSyntheticDisposalFailure(error)) { }
                        if(!entry.IsVisible || !(bool)type.GetField("researchConnectionActive",flags)!.GetValue(main)!)
                            throw new InvalidOperationException("Failed startup cleanup discarded the active research binding.");
                        backend.Completion.SetResult(new DeskflowPreviewState());
                        await ((Task)typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync",flags)!.Invoke(entry,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                        if(backend.EndIds.Count!=1 || backend.EndIds[0]!="during-start")throw new InvalidOperationException("Failed startup cleanup discarded the known return attempt.");
                        return;
                    }
#endif
                    if(mode=="startstopfailure")
                    {
                        try{await start.WaitAsync(TimeSpan.FromSeconds(10));throw new InvalidOperationException("Failed keyboard stop reported success.");}
                        catch(InvalidOperationException error) when(error.Message.Contains("键鼠停止尚未确认",StringComparison.Ordinal)){}
                        if(typeof(MacMultiDeviceWindow).GetField("pendingCollaboration",flags)!.GetValue(main) is not null)
                            throw new InvalidOperationException("Successfully disposed collaboration was falsely marked pending.");
                        try{await ((Task)typeof(MacMultiDeviceWindow).GetMethod("StartAsync",flags)!.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));throw new InvalidOperationException("Restart bypassed failed keyboard stop.");}
                        catch(InvalidOperationException error) when(error.Message is "Connection already started." or "连接停止尚未完成，请先重新停止连接。" or "键鼠状态仍未确认，请检查设备后自行关闭提示，再重新连接。"){}
                        if(factoryCalls!=1)throw new InvalidOperationException("Restart created another collaboration after failed keyboard stop.");
#if NIAN_MAC_RETURN_RESEARCH
                        var failedEntry=(MacRemotePreviewReturnWindow)typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main)!;
                        var failedStatus=(Avalonia.Controls.Button)typeof(MacRemotePreviewReturnWindow).GetField("button",flags)!.GetValue(failedEntry)!;
                        if(!failedEntry.IsVisible || failedStatus.Content is not string status || !status.Contains("不可用",StringComparison.Ordinal) || !status.Contains("未确认",StringComparison.Ordinal)) throw new InvalidOperationException("Failed start does not visibly describe the unknown connection.");
#endif
#if NIAN_MAC_RETURN_RESEARCH
                        if(typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main) is not MacRemotePreviewReturnWindow { IsVisible:true })
                            throw new InvalidOperationException("Failed keyboard stop discarded the research entry.");
#endif
                        process.FailStop=false;
                        await ((Task)stop.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));
                        return;
                    }
                    if(mode=="startsuccess")
                    {
                        await start.WaitAsync(TimeSpan.FromSeconds(10));
#if NIAN_MAC_RETURN_RESEARCH
                        if(typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main) is not MacRemotePreviewReturnWindow { IsVisible:true })
                            throw new InvalidOperationException("Successful start did not expose research entry.");
#endif
                        if(!ReferenceEquals(typeof(MacMultiDeviceWindow).GetField("collaboration",flags)!.GetValue(main),collaboration)||
                           typeof(MacMultiDeviceWindow).GetField("pendingCollaboration",flags)!.GetValue(main) is not null)
                            throw new InvalidOperationException("Successful startup did not publish exactly one collaboration owner.");
                        await ((Task)stop.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));
                        if(process.StopCount!=1||factoryCalls!=1)throw new InvalidOperationException("Successful startup and stop did not preserve normal behavior.");
#if NIAN_MAC_RETURN_RESEARCH
                        if(typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main) is not MacRemotePreviewReturnWindow { IsVisible:true })
                            throw new InvalidOperationException("Clean stop did not retain the unconfirmed research entry.");
#endif
                        return;
                    }
                    if(mode=="startcleanfailure")
                    {
                        try{await start.WaitAsync(TimeSpan.FromSeconds(10));throw new InvalidOperationException("Synthetic startup unexpectedly succeeded.");}
                        catch(InvalidOperationException error) when(error.Message=="synthetic collaboration startup failure"){}
                        if(process.StopCount!=1||typeof(MacMultiDeviceWindow).GetField("pendingCollaboration",flags)!.GetValue(main) is not null)
                            throw new InvalidOperationException("Clean failed startup did not complete the original cleanup.");
                        return;
                    }
                    Task? concurrentStop=null;
                    if(mode=="startconcurrent")
                    {
                        await media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        concurrentStop=(Task)stop.Invoke(main,null)!;
                        if(concurrentStop.IsCompleted||process.StopCount!=0)throw new InvalidOperationException("Concurrent stop bypassed startup cleanup.");
                        media.Release.TrySetResult();
                    }
                    try{await start.WaitAsync(TimeSpan.FromSeconds(10));throw new InvalidOperationException("Synthetic startup unexpectedly succeeded.");}
                    catch(InvalidOperationException error) when(error.Message.Contains("清理尚未确认",StringComparison.Ordinal)&&HasSyntheticDisposalFailure(error)){}
                    if(factoryCalls!=1||process.StopCount!=0)throw new InvalidOperationException("Startup did not preserve the synthetic keyboard after cleanup failure.");
                    if(!ReferenceEquals(typeof(MacMultiDeviceWindow).GetField("pendingCollaboration",flags)!.GetValue(main),collaboration))
                        throw new InvalidOperationException("Failed unpublished collaboration ownership was lost.");
                    var afterFailure=concurrentStop??(Task)stop.Invoke(main,null)!;
                    try{await afterFailure.WaitAsync(TimeSpan.FromSeconds(10));throw new InvalidOperationException("Stop reported success despite failed startup cleanup.");}
                    catch(InvalidOperationException error) when(error.Message.Contains("清理尚未确认",StringComparison.Ordinal)&&HasSyntheticDisposalFailure(error)){}
                    if(process.StopCount!=0)throw new InvalidOperationException("Stop bypassed a failed, unpublished collaboration owner and stopped the keyboard.");
                    var retry=(Task)typeof(MacMultiDeviceWindow).GetMethod("StartAsync",flags)!.Invoke(main,null)!;
                    try{await retry.WaitAsync(TimeSpan.FromSeconds(10));throw new InvalidOperationException("Restart reported success despite failed startup cleanup.");}
                    catch(InvalidOperationException error) when(error.Message.Contains("清理尚未确认",StringComparison.Ordinal)&&HasSyntheticDisposalFailure(error)){}
                    if(factoryCalls!=1||process.StopCount!=0)throw new InvalidOperationException("Retry bypassed the unconfirmed collaboration owner.");
                    return;
                }
#if NIAN_MAC_RETURN_RESEARCH
                if(mode=="research-stop-failed-core")
                {
                    var type=typeof(MacMultiDeviceWindow);
                    var generation=(long)type.GetMethod("BeginResearchConnection",flags)!.Invoke(main,null)!;
                    var backend=new DynamicBackend();backend.Completion.SetResult(new DeskflowPreviewState());
                    type.GetMethod("ApplyResearchObservation",flags)!.Invoke(main,[generation,"old-session",backend]);
                    var entry=(MacRemotePreviewReturnWindow)type.GetField("remoteReturn",flags)!.GetValue(main)!;
                    try{await ((Task)stop.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));throw new InvalidOperationException("Synthetic keyboard stop unexpectedly succeeded.");}
                    catch(IOException error) when(error.Message=="synthetic keyboard stop failure"){}
                    if(!entry.IsVisible || (bool)type.GetField("researchConnectionActive",flags)!.GetValue(main)!)throw new InvalidOperationException("Failed stop lost its entry or retained an active connection.");
                    await ((Task)typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync",flags)!.Invoke(entry,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                    if(backend.EndIds.Count!=0)throw new InvalidOperationException("A stopped connection issued the old return request.");
                    process.FailStop=false;
                    await ((Task)stop.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));
                    return;
                }
                if(mode=="research-stop-late-begin")
                {
                    var type=typeof(MacMultiDeviceWindow);
                    type.GetMethod("BeginResearchConnection",flags)!.Invoke(main,null);
                    var entry=(MacRemotePreviewReturnWindow)type.GetField("remoteReturn",flags)!.GetValue(main)!;
                    var life=(Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
                    if(!entry.IsVisible || life.Windows.Count(w=>w.IsVisible && w.Title==MacRemotePreviewReturnWindow.CaptureTitle)!=1)
                        throw new InvalidOperationException("Research entry was not initially visible and unique.");
                    var keyboard=new LateBeginKeyboard(hostName,owner.DeviceName);
                    await using var manager=new MultiPeerPreviewManager(hostName,[owner.DeviceName],keyboard);
                    var begin=manager.BeginAsync(hostName,owner.DeviceName);
                    try
                    {
                        await keyboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        if(begin.IsCompleted || owner.PreviewControlUnresolved)
                            throw new InvalidOperationException("Synthetic host Begin was not independently in flight.");
                        await ((Task)stop.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));
                        if(process.StopCount!=1 || owner.PreviewControlUnresolved)
                            throw new InvalidOperationException("Synthetic client stop did not complete with a locally clean owner.");
                        var entryAfterStop=type.GetField("remoteReturn",flags)!.GetValue(main);
                        var visibleAfterStop=life.Windows.Count(w=>w.IsVisible && w.Title==MacRemotePreviewReturnWindow.CaptureTitle);
                        var vanishedAtStop=!entry.IsVisible && entryAfterStop is null && visibleAfterStop==0;
                        var retainedAtStop=entry.IsVisible && ReferenceEquals(entryAfterStop,entry) && visibleAfterStop==1;
                        if(!vanishedAtStop && !retainedAtStop)
                            throw new InvalidOperationException("Stop left inconsistent or duplicate research entry ownership.");
                        keyboard.Release.TrySetResult();
                        var active=await begin.WaitAsync(TimeSpan.FromSeconds(5));
                        if(!active.IsActive || active.TargetComputer!=owner.DeviceName || keyboard.Active!=owner.DeviceName || keyboard.SwitchCalls!=1)
                            throw new InvalidOperationException("The already-admitted synthetic Begin did not complete after stop.");
                        if(vanishedAtStop)
                            throw new InvalidOperationException("RED: stop released the only return entry before the independently admitted synthetic Begin completed.");
                        if(!entry.IsVisible || !ReferenceEquals(type.GetField("remoteReturn",flags)!.GetValue(main),entry) ||
                           life.Windows.Count(w=>w.IsVisible && w.Title==MacRemotePreviewReturnWindow.CaptureTitle)!=1)
                            throw new InvalidOperationException("The retained research entry was lost or duplicated after the late synthetic Begin.");
                    }
                    finally
                    {
                        keyboard.Release.TrySetResult();
                        try { await begin.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                    }
                    return;
                }
                if(mode=="research-stop-bound-pending")
                {
                    var type=typeof(MacMultiDeviceWindow);
                    var generation=(long)type.GetMethod("BeginResearchConnection",flags)!.Invoke(main,null)!;
                    var backend=new DynamicBackend();
                    type.GetMethod("ApplyResearchObservation",flags)!.Invoke(main,[generation,"synthetic-session",backend]);
                    var entry=(MacRemotePreviewReturnWindow)type.GetField("remoteReturn",flags)!.GetValue(main)!;
                    var stopping=(Task)stop.Invoke(main,null)!;
                    await media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if(stopping.IsCompleted||process.StopCount!=0)throw new InvalidOperationException("Stop did not remain in collaboration cleanup.");
                    type.GetMethod("DismissResearchReturn",flags)!.Invoke(main,[entry]);
                    if(!entry.IsVisible || !ReferenceEquals(type.GetField("remoteReturn",flags)!.GetValue(main),entry))throw new InvalidOperationException("Pending stop allowed dismissing the working return entry.");
                    var action=(Task)typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync",flags)!.Invoke(entry,null)!;
                    await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    if(backend.EndIds.Count!=1||backend.EndIds[0]!="synthetic-session")throw new InvalidOperationException("Stop discarded the known attempt before connection retirement.");
                    backend.Completion.SetResult(new DeskflowPreviewState());
                    await action.WaitAsync(TimeSpan.FromSeconds(3));
                    media.Release.TrySetResult();
                    await stopping.WaitAsync(TimeSpan.FromSeconds(10));
                    if((bool)type.GetField("researchConnectionActive",flags)!.GetValue(main)! || process.StopCount!=1)
                        throw new InvalidOperationException("Successful stop did not invalidate the research generation.");
                    return;
                }
                if(mode=="research-stop-inflight")
                {
                    var controlOwner=typeof(MacMultiPeerClient).GetField("previewOwner",flags)!.GetValue(owner)!;
                    var keyboard=typeof(MacMultiPeerClient).GetField("guardedKeyboard",flags)!.GetValue(owner)!;
                    var ownerType=controlOwner.GetType();
                    ownerType.GetMethod("Enter",flags)!.Invoke(controlOwner,[keyboard,true,false,null]);
                    var entry=(MacRemotePreviewReturnWindow)typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main)!;
                    await ((Task)stop.Invoke(main,null)!).WaitAsync(TimeSpan.FromSeconds(10));
                    if(!owner.PreviewControlUnconfirmed || !entry.IsVisible || !ReferenceEquals(typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main),entry))
                        throw new InvalidOperationException("Stop with in-flight control did not preserve an unconfirmed entry.");
                    var statusButton=(Avalonia.Controls.Button)typeof(MacRemotePreviewReturnWindow).GetField("button",flags)!.GetValue(entry)!;
                    if(statusButton.Content is not string status || !status.Contains("未确认",StringComparison.Ordinal) || !status.Contains("不可用",StringComparison.Ordinal))
                        throw new InvalidOperationException("Unconfirmed stopped entry is not visibly described.");
                    try { ownerType.GetMethod("Complete",flags)!.Invoke(controlOwner,[keyboard,"late",true]); throw new InvalidOperationException("Retired reply was accepted."); }
                    catch(TargetInvocationException e) when(e.InnerException is InvalidOperationException) { }
                    if(!owner.PreviewControlUnconfirmed || !entry.IsVisible)throw new InvalidOperationException("Late reply cleared unconfirmed entry.");
                    return;
                }
#endif
#if NIAN_MAC_RETURN_RESEARCH
                DynamicBackend? failedCleanupBackend=null;
                if(mode=="research-stop-failure")
                {
                    var generation=(long)typeof(MacMultiDeviceWindow).GetMethod("BeginResearchConnection",flags)!.Invoke(main,null)!;
                    failedCleanupBackend=new DynamicBackend();failedCleanupBackend.Completion.SetResult(new DeskflowPreviewState());
                    typeof(MacMultiDeviceWindow).GetMethod("ApplyResearchObservation",flags)!.Invoke(main,[generation,"known-session",failedCleanupBackend]);
                }
#endif
                var first=(Task)stop.Invoke(main,null)!;
                await media.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if(mode=="disposepending")
                {
                    if(first.IsCompleted||process.StopCount!=0)throw new InvalidOperationException("Keyboard stopped before collaboration cleanup finished.");
                    media.Release.TrySetResult();
                    await first.WaitAsync(TimeSpan.FromSeconds(10));
                    if(process.StopCount!=1)throw new InvalidOperationException("Successful cleanup did not stop the owned synthetic keyboard.");
                }
                else if(mode is "disposefailure" or "research-stop-failure")
                {
                    try{await first;throw new InvalidOperationException("Failed collaboration cleanup was reported successful.");}
                    catch(IOException error) when(error.Message=="synthetic media disposal failure"){}
                    if(process.StopCount!=0)throw new InvalidOperationException("Keyboard stopped after collaboration cleanup failed.");
                    var second=(Task)stop.Invoke(main,null)!;
                    try{await second.WaitAsync(TimeSpan.FromSeconds(10));throw new InvalidOperationException("Repeated failed cleanup was reported successful.");}
                    catch(IOException error) when(error.Message=="synthetic media disposal failure"){}
                    if(process.StopCount!=0)throw new InvalidOperationException("Repeated failure allowed the synthetic keyboard to stop.");
#if NIAN_MAC_RETURN_RESEARCH
                    if(mode=="research-stop-failure" && typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main) is not MacRemotePreviewReturnWindow { IsVisible:true })
                        throw new InvalidOperationException("Failed stop discarded the research entry.");
                    if(mode=="research-stop-failure")
                    {
                        var entry=(MacRemotePreviewReturnWindow)typeof(MacMultiDeviceWindow).GetField("remoteReturn",flags)!.GetValue(main)!;
                        typeof(MacMultiDeviceWindow).GetMethod("DismissResearchReturn",flags)!.Invoke(main,[entry]);
                        if(!entry.IsVisible)throw new InvalidOperationException("Failed collaboration cleanup allowed dismissing the connected entry.");
                        await ((Task)typeof(MacRemotePreviewReturnWindow).GetMethod("ReturnAsync",flags)!.Invoke(entry,null)!).WaitAsync(TimeSpan.FromSeconds(3));
                        if(failedCleanupBackend?.EndIds.Count!=1 || failedCleanupBackend.EndIds[0]!="known-session")throw new InvalidOperationException("Failed collaboration cleanup lost the known End attempt.");
                    }
#endif
                }
                else
                {
                    await first.WaitAsync(TimeSpan.FromSeconds(10));
                    if(process.StopCount!=1)throw new InvalidOperationException("Successful cleanup did not stop the owned synthetic keyboard.");
                }
            }
            finally
            {
                identities.Release.TrySetResult();
                media.Release.TrySetResult();
                process.Exit();
                process2.Exit();
            }
        }

        private static bool HasSyntheticDisposalFailure(Exception? error)
        {
            if(error is null)return false;
            if(error is IOException { Message:"synthetic media disposal failure" })return true;
            if(error is AggregateException aggregate)return aggregate.InnerExceptions.Any(HasSyntheticDisposalFailure);
            return HasSyntheticDisposalFailure(error.InnerException);
        }
    }

    private sealed class MemoryIdentityVault:IIdentityVault
    {
        private readonly Dictionary<string,byte[]> values=[];
        internal bool BlockNextLoad;
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously), Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]?> LoadAsync(string key,CancellationToken cancellationToken=default)
        {
            if(BlockNextLoad){BlockNextLoad=false;Entered.TrySetResult();await Release.Task.WaitAsync(cancellationToken);}
            return values.TryGetValue(key,out var value)?value.ToArray():null;
        }
        public Task StoreAsync(string key,ReadOnlyMemory<byte> value,CancellationToken cancellationToken=default)
        {values.Add(key,value.ToArray());return Task.CompletedTask;}
    }
    private sealed class SyntheticMedia:IMacMediaBridge
    {
        internal bool Fail,Block;
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JsonDocument> RequestAsync(object request,CancellationToken cancellationToken=default)=>Task.FromResult(JsonDocument.Parse("{\"status\":\"ready\"}"));
        public Task<MacScreenCaptureReply> CaptureAsync(int width=960,int height=540,uint? windowId=null,CancellationToken cancellationToken=default)=>Task.FromResult(new MacScreenCaptureReply{Status="unavailable"});
        public async ValueTask DisposeAsync()
        {
            Entered.TrySetResult();
            if(Block)await Release.Task;
            if(Fail)throw new IOException("synthetic media disposal failure");
        }
    }
#if NIAN_MAC_RETURN_RESEARCH
    // In-memory host model: deliberately reports the peer connected even after
    // synthetic Mac stop, modelling delayed host observation, not real IPC.
    private sealed class LateBeginKeyboard(string host,string peer):IMultiPeerKeyboard
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Active=host;
        internal int SwitchCalls;
        private long revision=1;
        public Task<DeskLinkState> StartAsync(CancellationToken ct)=>GetStateAsync(ct);
        public Task<DeskLinkState> GetStateAsync(CancellationToken ct)=>Task.FromResult(new DeskLinkState
        { ProcessState=DeskLinkProcessState.Running,NativeControlAvailable=true,ActiveComputer=Active,Revision=revision,ConnectedComputers=[peer] });
        public async Task<DeskLinkSwitchResult> SwitchIfCurrentAsync(string target,string expected,long expectedRevision,CancellationToken ct)
        {
            SwitchCalls++;
            if(target==peer) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            if(Active!=expected || revision!=expectedRevision)return new DeskLinkSwitchResult { Error="synthetic stale" };
            Active=target;revision++;
            return new DeskLinkSwitchResult { Ok=true,InputTransferred=true,ActiveComputer=target,Target=target };
        }
        public Task<DeskLinkState> StopAsync(CancellationToken ct)=>GetStateAsync(ct);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
#endif
    private sealed class SyntheticProcess:IDeskflowOwnedProcess
    {
        private readonly TaskCompletionSource<int> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int StopCount;
        internal bool FailStop;
        public int Id=>456789;
        public bool HasExited=>completion.Task.IsCompleted;
        public Task<int> Completion=>completion.Task;
        internal void Exit()=>completion.TrySetResult(0);
        public Task StopAsync(CancellationToken ct=default){StopCount++;if(FailStop)throw new IOException("synthetic keyboard stop failure");Exit();return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class SyntheticIpc(TcpListener listener,TcpClient client,TcpClient server):IDisposable
    {
        internal TcpClient Client {get;}=client;
        private TcpClient Server {get;}=server;
        internal static async Task<SyntheticIpc> CreateAsync()
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var client=new TcpClient();var connecting=client.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
            var server=await listener.AcceptTcpClientAsync();await connecting;
            return new(listener,client,server);
        }
        public void Dispose(){Client.Dispose();Server.Dispose();listener.Stop();}
    }
}
