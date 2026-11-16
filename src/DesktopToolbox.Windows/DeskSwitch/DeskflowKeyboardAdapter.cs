using DesktopToolbox.DeskLink;
using DesktopToolbox.DeskLink.Coordination;
namespace DesktopToolbox.Windows.DeskSwitch;

internal sealed class DeskflowKeyboardAdapter(DeskflowSession session) : IKeyboardHandoffControl
{
    public async Task<NativeControlReply> GetStateAsync(CancellationToken token)
    {
        var state = await session.GetStateAsync(token);
        return new()
        {
            Ok = state.ProcessState == DeskLinkProcessState.Running && state.NativeControlAvailable,
            ActiveComputer = state.ActiveComputer, ConnectedComputers = state.ConnectedComputers.ToArray(),
            Revision = state.Revision, Error = state.LastError ?? (!state.NativeControlAvailable ? "共享控制接口尚未就绪。" : null)
        };
    }
    public Task<NativeControlReply> SwitchAsync(string computer, CancellationToken token) => session.SwitchAsync(computer, token);
}