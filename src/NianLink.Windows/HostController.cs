using DesktopToolbox.DeskLink;
using DesktopToolbox.Windows.DeskSwitch;
namespace DesktopToolbox.App.Hosting;
// Narrow adapter retained for the target-side return window; it owns no toolbox modules.
public sealed class HostController(IIntegratedDeskSwitchService service, Func<string> localName, Action<Exception> log)
{
    internal IntegratedDeskSwitchStatus? CurrentDeskSwitch => service.Snapshot;
    internal string DeskLocalName => localName();
    internal Task<DeskflowPreviewState> ReturnPreviewAsync(string id) => service is IDeskflowPreviewControl preview
        ? preview.EndPreviewAsync(id) : throw new InvalidOperationException("键鼠共享尚未就绪。");
    public void Log(Exception error) => log(error);
}
