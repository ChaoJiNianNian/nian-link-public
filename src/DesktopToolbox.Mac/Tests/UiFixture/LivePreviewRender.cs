using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Collaboration;
using DesktopToolbox.DeskLink.Collaboration.LivePreview;
using DesktopToolbox.DeskLink.Preview;
using DesktopToolbox.Mac.Services;
using DesktopToolbox.Mac.Views;
using Nian.Input.Control;

internal static partial class Program
{
    private static async Task RunLivePreviewRenderAsync(IClassicDesktopStyleApplicationLifetime application)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var checks=new Dictionary<string,bool>();var errors=new List<string>();var nativeDrawSnapshot="";
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixture-desktop.jpg"));
        using var reference=new Bitmap(new MemoryStream(bytes));
        var source=new LiveRenderSource();var backend=new LiveRenderBackend();
        await using var session=new DeskflowPreviewSession(backend,source);
        await using var service=new MacCollaborationService(Path.Combine(Path.GetDirectoryName(output)!,"fixture-live-host"),
            _=>Task.FromResult<DeskLinkPairingBundle?>(null),new FakeMedia(),24802,null,null,backend);
        service.UpdateState(current=>current with{IsOnline=true,Settings=current.Settings with{PreviewEnabled=true},
            PassivePreviewGenerationSupported=true,PassivePreviewRequestId=Guid.NewGuid(),RemotePreviewSourceStatus=new(Guid.NewGuid(),1,"enabled")});
        typeof(MacCollaborationService).GetField("remoteSupportsLivePreview",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(service,true);
        service.RegisterDeskflowSessionForFixture(session);
        var viewer=new MacRemotePreviewWindow(session,"Mac","Windows")
        {ShowActivated=false,ShowInTaskbar=false,Position=new PixelPoint(-5000,-5000),WindowStartupLocation=WindowStartupLocation.Manual};
        var surface=(MacPreviewSurface)typeof(MacRemotePreviewWindow).GetField("preview",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(viewer)!;
        var image=(Image)typeof(MacPreviewSurface).GetField("image",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!;
        var bitmapField=typeof(MacPreviewSurface).GetField("bitmap",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var capturedField=typeof(MacPreviewSurface).GetField("lastLiveCapturedAt",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var changed=0;surface.FrameChanged+=()=>changed++;
        PreviewFrame Passive(DateTimeOffset? captured=null)=>new(){State="live",Width=reference.PixelSize.Width,Height=reference.PixelSize.Height,JpegBytes=bytes,CapturedAtUtc=captured??DateTimeOffset.UtcNow};
        LivePreviewCapturePacket Packet(long sequence)=>new(){Kind=LivePreviewCaptureKind.Image,Sequence=sequence,Width=reference.PixelSize.Width,Height=reference.PixelSize.Height,JpegBytes=bytes,CapturedAtUtc=DateTimeOffset.UtcNow};
        async Task<bool> Until(Func<bool> predicate,int milliseconds=1500)
        {for(var time=0;time<milliseconds&&!predicate();time+=20)await Task.Delay(20);return predicate();}
        viewer.Show();viewer.Position=new PixelPoint(-5000,-5000);
        try
        {
            checks["realNativeWindowCreated"]=viewer.TryGetPlatformHandle() is not null;
            viewer.SetConnectionState(true,"只交接键鼠，屏幕保持不变");viewer.ShowFrame(Passive(),30);viewer.UpdateLayout();
            checks["passiveFrameIsVisibleBeforeControl"]=surface.HasLiveFrame;
            var enabling=session.EnableAsync();await Until(()=>source.Epoch is not null);
            service.HandleOrdinaryPollingFailure(new IOException("synthetic business poll timeout"));
            viewer.SetConnectionState(service.PreviewControlReady,service.PreviewControlHint);
            checks["businessPollFailureDoesNotCancelPreparingInRealWindow"]=session.Phase==PreviewControlPhase.Preparing&&service.PreviewControlReady;
            source.Emit(new(){Kind=LivePreviewCaptureKind.HealthyIdle,Sequence=1});await Task.Delay(80);
            checks["healthyIdleCannotBeginWithoutPresentedImage"]=backend.BeginCalls==0;
            source.Emit(Packet(2));
            checks["presentedLiveBitmapAllowsHandoff"]=await Until(()=>backend.BeginCalls==1&&session.Phase==PreviewControlPhase.Controlling);
            if(checks["presentedLiveBitmapAllowsHandoff"])
            {
                await enabling;var live=image.Source;var firstBitmap=bitmapField.GetValue(surface);
                service.HandleOrdinaryPollingFailure(new IOException("synthetic business poll timeout"));
                viewer.SetConnectionState(service.PreviewControlReady,service.PreviewControlHint);
                await Task.Delay(80);
                checks["businessPollFailureDoesNotCancelHealthyOperationInRealWindow"]=session.Phase==PreviewControlPhase.Controlling&&service.PreviewControlReady&&surface.HasLiveFrame;
                checks["liveCaptionIsIndependentOfPassiveCadence"]=surface.FrameStatus.Contains("实时",StringComparison.Ordinal);
                viewer.ShowFrame(PreviewFrame.Off("waiting"),30);
                checks["passiveWaitingCannotEraseOperationImage"]=surface.HasLiveFrame&&ReferenceEquals(live,image.Source);
                var notifications=changed;
                var measureInvalidatingSourceChanges=0;
                EventHandler<AvaloniaPropertyChangedEventArgs> sourceChanged=(_,change)=>
                {if(change.Property==Image.SourceProperty)measureInvalidatingSourceChanges++;};
                image.PropertyChanged+=sourceChanged;
                for(var i=3;i<=72;i++)source.Emit(Packet(i));
                checks["newLiveFramesReachNativeImage"]=await Until(()=>!ReferenceEquals(firstBitmap,bitmapField.GetValue(surface)));await Task.Delay(120);
                checks["sameSizeFramesKeepNativeImageSourceIdentity"]=ReferenceEquals(live,image.Source);
                checks["sameSizeFramesAvoidMeasureInvalidatingSourceChanges"]=measureInvalidatingSourceChanges==0;
                image.PropertyChanged-=sourceChanged;
                checks["frameFloodDoesNotDriveBusinessLayoutNotifications"]=changed-notifications<=1;
                viewer.UpdateLayout();var root=(Visual)viewer.Content!;
                nativeDrawSnapshot=Path.ChangeExtension(output,".png");
                using(var rendered=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(root.Bounds.Width),(int)Math.Ceiling(root.Bounds.Height)),new Vector(96,96)))
                {rendered.Render(root);rendered.Save(nativeDrawSnapshot);}
                checks["liveImageDrawsWithNativeDrawingBackend"]=File.Exists(nativeDrawSnapshot);
                var retainedBitmap=bitmapField.GetValue(surface);
                var captured=(DateTimeOffset)capturedField.GetValue(surface)!;
                var expectedCaption="更新于 "+captured.ToLocalTime().ToString("HH:mm:ss");
                await session.StopAsync("已归还，仅观看");
                // Session notifications are posted to the UI dispatcher; wait for that callback,
                // not merely the session's phase, before asserting the caption and image.
                checks["normalReturnShowsActualCaptureTime"]=await Until(()=>surface.FrameStatus==expectedCaption);
                checks["normalReturnKeepsLastLiveImage"]=session.Phase==PreviewControlPhase.Viewing&&surface.HasLiveFrame&&ReferenceEquals(retainedBitmap,bitmapField.GetValue(surface));
                viewer.ShowFrame(Passive(DateTimeOffset.UtcNow.AddMinutes(-1)),30);
                checks["olderPassiveFrameCannotReplaceRetainedLiveImage"]=ReferenceEquals(retainedBitmap,bitmapField.GetValue(surface));
                viewer.ShowFrame(PreviewFrame.Off("waiting"),30);
                checks["passiveWaitingCannotEraseRetainedLiveImage"]=surface.HasLiveFrame&&ReferenceEquals(retainedBitmap,bitmapField.GetValue(surface));
                viewer.ShowFrame(Passive(),30);
                checks["newerPassiveFrameReplacesRetainedLiveImage"]=surface.HasLiveFrame&&!ReferenceEquals(retainedBitmap,bitmapField.GetValue(surface));
                var second=session.EnableAsync();await Until(()=>source.Epoch is not null);source.Emit(Packet(1));
                checks["secondGenerationCanPresentAfterReturn"]=await Until(()=>backend.BeginCalls==2&&session.Phase==PreviewControlPhase.Controlling);
                await second;source.Unavailable();
                checks["streamFailureReturnsAndClearsNativeImage"]=await Until(()=>session.Phase==PreviewControlPhase.Viewing&&!surface.HasLiveFrame);
                service.HandleOrdinaryPollingFailure(new IOException("synthetic poll timeout after stream failure"));
                checks["streamFailureThenPollFailureStopsClaimingConnection"]=!service.PreviewControlReady&&!service.Snapshot.IsOnline;
                service.UpdateState(current=>current with{IsOnline=true});
                viewer.ShowFrame(Passive(DateTimeOffset.UtcNow.AddMinutes(-1)),30);
                checks["failureCannotResurrectOldPassiveImage"]=!surface.HasLiveFrame;
                viewer.ShowFrame(Passive(),30);checks["passiveViewingResumesAfterOperation"]=surface.HasLiveFrame;
                viewer.SetConnectionState(false,"设备断开");
                checks["disconnectionClearsRetainedImage"]=!surface.HasLiveFrame;
                viewer.SetConnectionState(true,"已连接");viewer.ShowFrame(Passive(),30);
                checks["freshPassiveFrameCanRecoverAfterDisconnection"]=surface.HasLiveFrame;
                viewer.ShowFrame(PreviewFrame.Off("local-off"),30);
                checks["explicitPreviewOffClearsImage"]=!surface.HasLiveFrame;
                viewer.ShowFrame(Passive(),30);
                var sourceStatus=new PreviewSourceStatus(Guid.NewGuid(),1,"enabled");var oldRequest=Guid.NewGuid();var newRequest=Guid.NewGuid();
                viewer.SetPassiveContext(true,oldRequest,sourceStatus);
                viewer.ShowFrame(Passive() with{RequestId=oldRequest,SourceStatus=sourceStatus,CaptureSequence=1},30);
                var previousGenerationBitmap=bitmapField.GetValue(surface);
                viewer.SetPassiveContext(true,newRequest,sourceStatus);
                viewer.ShowFrame(Passive(DateTimeOffset.UtcNow.AddHours(1)) with{RequestId=oldRequest,SourceStatus=sourceStatus,CaptureSequence=2},30);
                checks["lateOldGenerationCannotReplaceImage"]=ReferenceEquals(previousGenerationBitmap,bitmapField.GetValue(surface));
                viewer.ShowFrame(Passive(DateTimeOffset.UtcNow.AddHours(-1)) with{RequestId=newRequest,SourceStatus=sourceStatus,CaptureSequence=1},30);
                checks["newGenerationAcceptsSourceClockRollback"]=surface.HasLiveFrame&&!ReferenceEquals(previousGenerationBitmap,bitmapField.GetValue(surface));
                var newGenerationBitmap=bitmapField.GetValue(surface);
                viewer.ShowFrame(Passive(DateTimeOffset.UtcNow.AddHours(-2)) with{RequestId=newRequest,SourceStatus=sourceStatus,CaptureSequence=2,JpegBytes=bytes.ToArray()},30);
                checks["newerSequenceAcceptsEarlierClockWithinGeneration"]=surface.HasLiveFrame&&!ReferenceEquals(newGenerationBitmap,bitmapField.GetValue(surface));
                var newerSequenceBitmap=bitmapField.GetValue(surface);
                viewer.ShowFrame(Passive(DateTimeOffset.UtcNow.AddHours(2)) with{RequestId=newRequest,SourceStatus=sourceStatus,CaptureSequence=1,JpegBytes=bytes.ToArray()},30);
                checks["lateLowerSequenceCannotReplaceNewerImage"]=ReferenceEquals(newerSequenceBitmap,bitmapField.GetValue(surface));
                var reopened=sourceStatus with{Revision=3,Availability="enabled"};
                viewer.SetPassiveContext(true,newRequest,reopened);
                checks["skippedRemoteCloseAndReopenClearsPriorRevision"]=!surface.HasLiveFrame;
                viewer.ShowFrame(Passive() with{RequestId=newRequest,SourceStatus=sourceStatus,CaptureSequence=3},30);
                checks["skippedRemoteCloseCannotRestoreOldRevision"]=!surface.HasLiveFrame;
                viewer.ShowFrame(Passive() with{RequestId=newRequest,SourceStatus=reopened,CaptureSequence=3},30);
                checks["newSourceRevisionCanShowFreshFrame"]=surface.HasLiveFrame;
                viewer.SetPassiveContext(true,newRequest,sourceStatus with{Revision=4,Availability="locked"});
                checks["explicitRemoteLockClearsImage"]=!surface.HasLiveFrame;
                viewer.ShowFrame(Passive() with{RequestId=newRequest,SourceStatus=reopened,CaptureSequence=4},30);
                checks["lateOldSourceRevisionCannotResurrectImage"]=!surface.HasLiveFrame;
                viewer.SetPassiveContext(true,newRequest,sourceStatus with{Revision=5,Availability="enabled"});
            }
            else
            {
                await session.StopAsync("夹具未收到实际呈现");try{await enabling;}catch{}
                checks["liveCaptionIsIndependentOfPassiveCadence"]=false;checks["passiveWaitingCannotEraseOperationImage"]=false;
                checks["newLiveFramesReachNativeImage"]=false;checks["frameFloodDoesNotDriveBusinessLayoutNotifications"]=false;
                checks["sameSizeFramesKeepNativeImageSourceIdentity"]=false;checks["sameSizeFramesAvoidMeasureInvalidatingSourceChanges"]=false;
                checks["liveImageDrawsWithNativeDrawingBackend"]=false;
                foreach(var key in new[]{"businessPollFailureDoesNotCancelHealthyOperationInRealWindow","normalReturnKeepsLastLiveImage","normalReturnShowsActualCaptureTime","olderPassiveFrameCannotReplaceRetainedLiveImage","passiveWaitingCannotEraseRetainedLiveImage","newerPassiveFrameReplacesRetainedLiveImage","secondGenerationCanPresentAfterReturn","streamFailureReturnsAndClearsNativeImage","streamFailureThenPollFailureStopsClaimingConnection","failureCannotResurrectOldPassiveImage","passiveViewingResumesAfterOperation","disconnectionClearsRetainedImage","freshPassiveFrameCanRecoverAfterDisconnection","explicitPreviewOffClearsImage","lateOldGenerationCannotReplaceImage","newGenerationAcceptsSourceClockRollback","newerSequenceAcceptsEarlierClockWithinGeneration","lateLowerSequenceCannotReplaceNewerImage","skippedRemoteCloseAndReopenClearsPriorRevision","skippedRemoteCloseCannotRestoreOldRevision","newSourceRevisionCanShowFreshFrame","explicitRemoteLockClearsImage","lateOldSourceRevisionCannotResurrectImage"})checks[key]=false;
            }
            if(checks["presentedLiveBitmapAllowsHandoff"])
            {
                var third=session.EnableAsync();await Until(()=>source.Epoch is not null);source.Emit(Packet(1));
                checks["thirdGenerationCanPresentAfterRecovery"]=await Until(()=>backend.BeginCalls==3&&session.Phase==PreviewControlPhase.Controlling);
                viewer.Close();await Until(()=>source.StopCalls>=3);
                try{await third;}catch{}
                checks["closeStopsOwnedSourceAndClearsImage"]=source.StopCalls>=3&&!surface.HasLiveFrame;
            }
            else {viewer.Close();checks["thirdGenerationCanPresentAfterRecovery"]=false;checks["closeStopsOwnedSourceAndClearsImage"]=false;}
        }
        catch(Exception error){errors.Add(error.ToString());try{viewer.Close();}catch{}}
        var passed=errors.Count==0&&checks.Count>=9&&checks.Values.All(value=>value);
        var report=new{mode="live-preview-native-render",syntheticFrameOnly=true,realDesktopCaptured=false,networkClientStarted=false,nativeInputTargetConstructed=false,
            source.StartCalls,source.StopCalls,backend.BeginCalls,backend.EndCalls,nativeDrawSnapshot,checks,errors,passed};
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(report));application.Shutdown(passed?0:2);
    }
    private sealed class LiveRenderSource:IControlLivePreviewSource
    {
        public bool IsSupported=>true;public string UnavailableHint=>"fixture";public Guid? Epoch;
        public int StartCalls,StopCalls;
        public event Action<Guid,LivePreviewCapturePacket>? PacketReceived;
        public event Action<Guid,string>? StreamClosed;
        public Task StartAsync(Guid operationId,CancellationToken token=default){Epoch=operationId;StartCalls++;return Task.CompletedTask;}
        public Task StopAsync(Guid operationId,CancellationToken token=default){if(Epoch==operationId){Epoch=null;StopCalls++;}return Task.CompletedTask;}
        public void Emit(LivePreviewCapturePacket packet){if(Epoch is{} epoch)PacketReceived?.Invoke(epoch,packet);}
        public void Unavailable(){if(Epoch is{} epoch)StreamClosed?.Invoke(epoch,"capture-failed");}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class LiveRenderBackend:IDeskflowPreviewControl
    {
        private DeskflowPreviewState current=new(){ReturnShortcut="Ctrl+Alt+F12"};
        public int BeginCalls,EndCalls;public bool PreviewHandoffSupported=>true;public string PreviewHandoffHint=>"fixture";public string PreviewReturnShortcut=>"Ctrl+Alt+F12";
        public Task<DeskflowPreviewState> BeginPreviewAsync(CancellationToken token=default){BeginCalls++;return Task.FromResult(current=new(){Phase=DeskflowPreviewPhase.Active,Id="fixture-live-owned",ReturnShortcut=PreviewReturnShortcut});}
        public Task<DeskflowPreviewState> RenewPreviewAsync(string id,CancellationToken token=default)=>Task.FromResult(current);
        public Task<DeskflowPreviewState> EndPreviewAsync(string id,CancellationToken token=default){EndCalls++;return Task.FromResult(current=new(){ReturnShortcut=PreviewReturnShortcut});}
        public Task<DeskflowPreviewState> ReadPreviewAsync(CancellationToken token=default)=>Task.FromResult(current);
    }
}
