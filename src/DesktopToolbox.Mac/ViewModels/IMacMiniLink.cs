using System.ComponentModel;

namespace DesktopToolbox.Mac.ViewModels;

/// <summary>Keyboard connection used by the shared Mac mini window.</summary>
public interface IMacMiniLink : INotifyPropertyChanged
{
    string WindowsName { get; }
    string MacName { get; }
    string ErrorMessage { get; }
    bool CanStart { get; }
    bool CanStop { get; }
    bool CanSwitch { get; }
    bool CanReturnToWindows { get; }
    bool IsSharingRunning { get; }
    bool IsInputConnected { get; }
    bool IsWindowsActive { get; }
    bool IsMacActive { get; }
    Task StartAsync();
    Task StopAsync();
    Task SwitchAsync(string target);
}
