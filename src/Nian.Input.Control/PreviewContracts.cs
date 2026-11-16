namespace Nian.Input.Control;

public enum PreviewInputKind { LeftClick, RightClick, DoubleClick, Wheel, KeyPress, PointerDown, PointerMove, PointerUp }
public enum DesktopControlMode { Keyboard, Preview }

/// <summary>Native injection coordinates; image dimensions are encoded pixels, never DPI-scaled coordinates.</summary>
public sealed record PreviewSourceGeometry(string SourceId,string Revision,double Left,double Top,double Width,double Height,int ImageWidth,int ImageHeight)
{
    public bool IsValid => SourceId is {Length:>0 and <=128} && Revision is {Length:>0 and <=128} &&
        !SourceId.Any(char.IsControl) && !Revision.Any(char.IsControl) &&
        double.IsFinite(Left) && double.IsFinite(Top) && Math.Abs(Left)<=1_000_000 && Math.Abs(Top)<=1_000_000 &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width is >=1 and <=100_000 && Height is >=1 and <=100_000 &&
        ImageWidth is >0 and <=1920 && ImageHeight is >0 and <=1080;
    public bool SameSource(PreviewSourceGeometry? other)=>other is not null && SourceId==other.SourceId && Revision==other.Revision &&
        Left==other.Left && Top==other.Top && Width==other.Width && Height==other.Height;
}
public sealed record PreviewFrameProof(string Id,string ContentSha256,PreviewSourceGeometry Geometry);
public sealed record PreviewInputCommand(long Sequence,string FrameId,string ContentSha256,PreviewInputKind Kind,double U=0,double V=0,int Code=0,int Delta=0);
public sealed record ResolvedPreviewInput(long Sequence,PreviewInputKind Kind,double X,double Y,int Code,int Delta,PreviewSourceGeometry Geometry);
public interface IPreviewInputTarget : IInputTarget
{
    void ApplyPreview(ResolvedPreviewInput input,long expectedConsentVersion);
}
