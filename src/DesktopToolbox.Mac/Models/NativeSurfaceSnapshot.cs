namespace DesktopToolbox.Mac.Models;

/// <summary>Written by the UI thread; native safety timers only read this immutable atomic decision.</summary>
public sealed class NativeSurfaceSnapshot
{
    public const string ProductionBundleId="com.nian.desktoptoolbox.desklink";
    public const string CandidateBundleId="com.nian.desktoptoolbox.desklink.native-candidate";
    public const string PreviewCandidateBundleId="com.nian.desktoptoolbox.desklink.preview-candidate";
    public const string StandaloneBundleId="com.niannian.nianlink";
    private sealed record Decision(bool Protected,int? WindowId);
    private readonly object gate=new();
    private Decision decision=new(true,null);
    private int blocks;
    public bool ProtectsOwnSurface=>Volatile.Read(ref decision).Protected;
    public int? AllowedWindowId=>Volatile.Read(ref decision).WindowId;
    public bool IsProtectedWindow(int windowId)
    {
        var current=Volatile.Read(ref decision);
        return windowId<=0||current.Protected||current.WindowId!=windowId;
    }
    public void Update(bool ordinaryCompactSurface,bool securitySurfaceVisible,int? windowNumber)
    {
        lock(gate)
            Volatile.Write(ref decision,ordinaryCompactSurface&&!securitySurfaceVisible&&windowNumber is >0&&blocks==0
                ?new(false,windowNumber):new(true,null));
    }
    public void Protect(){lock(gate)Volatile.Write(ref decision,new(true,null));}
    public IDisposable Block()
    {
        lock(gate){blocks++;Volatile.Write(ref decision,new(true,null));}
        return new ProtectionScope(this);
    }
    private void EndBlock()
    {lock(gate){if(blocks>0)blocks--;Volatile.Write(ref decision,new(true,null));}}
    private sealed class ProtectionScope(NativeSurfaceSnapshot owner):IDisposable
    {
        private NativeSurfaceSnapshot? current=owner;
        public void Dispose()=>Interlocked.Exchange(ref current,null)?.EndBlock();
    }
    public static bool ExcludesPreviewOwner(string? bundle,int ownerPid,int ownPid)=>ownerPid<=0||ownPid<=0||ownerPid==ownPid||
        string.IsNullOrWhiteSpace(bundle)||string.Equals(bundle,ProductionBundleId,StringComparison.OrdinalIgnoreCase)||
        string.Equals(bundle,StandaloneBundleId,StringComparison.OrdinalIgnoreCase)||
        string.Equals(bundle,CandidateBundleId,StringComparison.OrdinalIgnoreCase)||
        string.Equals(bundle,PreviewCandidateBundleId,StringComparison.OrdinalIgnoreCase);
}

/// <summary>Candidate startup checks only path metadata before constructing a service or opening a profile.</summary>
public static class NativeCandidateProfile
{
    public static string Require(string? requestedDirectory,string productionDirectory)
    {
        if(string.IsNullOrWhiteSpace(requestedDirectory)||!Path.IsPathFullyQualified(requestedDirectory))
            throw new ArgumentException("原生候选程序必须显式指定 --data-dir 独立资料目录。");
        if(!Path.IsPathFullyQualified(productionDirectory))throw new ArgumentException("无法确认正式资料目录。");
        var candidate=Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedDirectory));
        var production=Path.TrimEndingDirectorySeparator(Path.GetFullPath(productionDirectory));
        if(Contains(production,candidate)||Contains(candidate,production))
            throw new ArgumentException("候选资料目录不能是正式资料目录、其子目录或祖先目录。");
        if(File.Exists(candidate))throw new IOException("候选资料目录不能是文件。");
        for(string? current=candidate;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
        {
            try
            {
                if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new IOException("候选资料目录不能经过文件链接。");
            }
            catch(FileNotFoundException){}
            catch(DirectoryNotFoundException){}
        }
        return candidate;
    }
    private static bool Contains(string root,string path)=>string.Equals(root,path,StringComparison.OrdinalIgnoreCase)||
        path.StartsWith(Path.EndsInDirectorySeparator(root)?root:root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
}
