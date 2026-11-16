namespace Nian.Input.Control;

/// <summary>An explicitly approved preview capability, independent of the physical keyboard backend.</summary>
public interface INativePreviewControl
{
    bool PreviewControlReady=>true;
    string PreviewControlHint=>"";
    PreviewFrameProof? BindCapturedFrame(byte[] jpeg,PreviewSourceGeometry? geometry);
    IPreviewControlChannel CreatePreviewChannel();
    void InvalidateCapturedFrames();
}
