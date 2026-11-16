using System.Text.Json.Serialization;

namespace Nian.Input.Security;

public enum DesktopSwitchTarget { Local,Remote }

/// <summary>Display intent changes only for explicit full-switch actions, independently of temporary preview ownership.</summary>
public sealed record DesktopControlState(bool Available,string LocalName,string RemoteName,string? ActiveComputer,bool InputActive,
    long InputRevision,long DisplayIntentRevision,string? DisplayIntentComputer=null,string? Error=null);
public sealed record DesktopFullSwitchRequest([property:JsonRequired]long Sequence,[property:JsonRequired]DesktopSwitchTarget Target,[property:JsonRequired]long ExpectedDisplayIntentRevision);
public sealed record DesktopSwitchResult(bool Ok,DesktopControlState State,bool InputTransferred,string VideoStatus,string? Error=null);

internal static class DesktopControlValidation
{
    internal static bool Request(DesktopFullSwitchRequest value)=>value.Sequence>0&&Enum.IsDefined(value.Target)&&value.ExpectedDisplayIntentRevision>=0;
    internal static bool State(DesktopControlState? value)=>value is not null&&TrustValidation.DeviceName(value.LocalName)&&TrustValidation.DeviceName(value.RemoteName)&&
        !string.Equals(value.LocalName,value.RemoteName,StringComparison.OrdinalIgnoreCase)&&value.InputRevision>=0&&value.DisplayIntentRevision>=0&&
        Computer(value.ActiveComputer,value)&&Computer(value.DisplayIntentComputer,value)&&Text(value.Error);
    internal static bool Result(DesktopSwitchResult? value)=>value is not null&&State(value.State)&&Text(value.Error)&&
        value.VideoStatus is "disabled" or "notAttempted" or "verified" or "acceptedUnverified" or "failed" or "canceled"&&
        (!value.Ok||value.InputTransferred&&value.VideoStatus is "disabled" or "verified");
    private static bool Computer(string? name,DesktopControlState value)=>name is null||name==value.LocalName||name==value.RemoteName;
    private static bool Text(string? value)=>value is null||value.Length<=512&&!value.Any(c=>char.IsControl(c)&&c is not '\r' and not '\n' and not '\t');
}
