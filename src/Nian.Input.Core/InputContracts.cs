namespace Nian.Input;

public enum InputKind { MoveRelative, ButtonDown, ButtonUp, Wheel, KeyDown, KeyUp }
public enum InputKey
{
    A=4,B,C,D,E,F,G,H,I,J,K,L,M,N,O,P,Q,R,S,T,U,V,W,X,Y,Z,
    Digit1=30,Digit2,Digit3,Digit4,Digit5,Digit6,Digit7,Digit8,Digit9,Digit0,
    Enter=40,Escape,Backspace,Tab,Space,Minus,Equal,LeftBracket,RightBracket,Backslash,
    Semicolon=51,Quote,Grave,Comma,Period,Slash,CapsLock,
    F1=58,F2,F3,F4,F5,F6,F7,F8,F9,F10,F11,F12,
    Insert=73,Home,PageUp,Delete,End,PageDown,Right,Left,Down,Up,
    LeftControl=224,LeftShift,LeftAlt,LeftSuper,RightControl,RightShift,RightAlt,RightSuper
}

public sealed record InputPacket(long Sequence, InputKind Kind, int Code=0, double X=0, double Y=0, int Delta=0);
public interface IInputTarget
{
    bool IsReady { get; }
    string UnavailableReason { get; }
    long ConsentVersion => 0;
    void Apply(InputPacket packet);
    void Apply(InputPacket packet, long expectedConsentVersion)
    {
        if (ConsentVersion != expectedConsentVersion) throw new InvalidOperationException("Local input consent changed.");
        Apply(packet);
    }
    void ReleaseAll();
    // Existing own-process targets retain their release behavior. Lease-aware
    // desktop targets override this and compare+release under their own gate.
    void ReleaseAll(long expectedConsentVersion) => ReleaseAll();
}

public sealed record InputRequest(string Action, string Session, string? Token=null, InputPacket? Packet=null)
{
    public override string ToString() => $"InputRequest {{ Action = {Action}, Session = {Session}, Token = [redacted] }}";
}
public sealed record InputReply(bool Ok, string Action, string Session, long Sequence=0, string? Error=null);
public sealed record InputPairing(string Host, int Port, string Fingerprint, string Token)
{
    public override string ToString() => $"InputPairing {{ Host = {Host}, Port = {Port}, Fingerprint = {Fingerprint}, Token = [redacted] }}";
}
