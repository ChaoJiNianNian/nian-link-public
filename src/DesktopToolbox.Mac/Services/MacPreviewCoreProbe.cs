using System.Diagnostics;

namespace DesktopToolbox.Mac.Services;

internal static class MacPreviewCoreProbe
{
    internal static bool HasOtherCore(int? ownedClientPid)
    {
        try
        {
            foreach(var name in new[]{"deskflow-core","deskflow","barrier","synergy-core","input-leap"})
            {
                var processes=Process.GetProcessesByName(name);
                try{if(processes.Any(process=>process.Id!=ownedClientPid&&!process.HasExited))return true;}
                finally{foreach(var process in processes)process.Dispose();}
            }
            return false;
        }
        catch{return true;}
    }
}
