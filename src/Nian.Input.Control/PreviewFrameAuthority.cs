using System.Security.Cryptography;

namespace Nian.Input.Control;

/// <summary>Source-owned short-lived proofs. No JPEGs, input values or secrets are stored in this registry.</summary>
public sealed class PreviewFrameAuthority
{
    private sealed record Entry(PreviewFrameProof Proof,long IssuedAt);
    private readonly object gate=new();
    private readonly Func<PreviewSourceGeometry?> currentSource;
    private readonly TimeProvider clock;
    private readonly TimeSpan maximumAge;
    private readonly Dictionary<string,Entry> entries=new(StringComparer.Ordinal);
    private long? lastTimestamp;
    public PreviewFrameAuthority(Func<PreviewSourceGeometry?> currentSource,TimeProvider? clock=null,TimeSpan? maximumAge=null)
    {
        ArgumentNullException.ThrowIfNull(currentSource);this.currentSource=currentSource;this.clock=clock??TimeProvider.System;
        this.maximumAge=maximumAge??TimeSpan.FromSeconds(2);
        if(this.maximumAge<=TimeSpan.Zero||this.maximumAge>TimeSpan.FromSeconds(2))throw new ArgumentOutOfRangeException(nameof(maximumAge));
    }
    public int Count {get{lock(gate)return entries.Count;}}
    public PreviewSourceGeometry? CurrentGeometry
    {
        get{lock(gate){try{var source=ReadCurrent();Prune(ReadTimestamp(),source);return source;}catch{entries.Clear();return null;}}}
    }
    public bool HasFreshFrame
    {
        get{lock(gate){try{Prune(ReadTimestamp(),ReadCurrent());return entries.Count>0;}catch{entries.Clear();return false;}}}
    }
    public PreviewFrameProof Register(byte[] jpeg,PreviewSourceGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(jpeg);ArgumentNullException.ThrowIfNull(geometry);
        if(jpeg.Length is <1 or >1_048_576||!geometry.IsValid)throw new ArgumentException("The controlled preview frame is invalid.");
        lock(gate)
        {
            var now=ReadTimestamp();var current=ReadCurrent();Prune(now,current);
            if(!geometry.SameSource(current))throw new InvalidOperationException("The actual preview source changed.");
            while(entries.Count>=4)entries.Remove(entries.MinBy(item=>item.Value.IssuedAt).Key);
            var proof=new PreviewFrameProof(Guid.NewGuid().ToString("N"),Convert.ToHexString(SHA256.HashData(jpeg)),geometry);
            entries.Add(proof.Id,new(proof,now));return proof;
        }
    }
    public ResolvedPreviewInput Resolve(PreviewInputCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if(!PreviewInputValidation.Command(command))throw new ArgumentException("The preview action is invalid.");
        lock(gate)
        {
            Prune(ReadTimestamp(),ReadCurrent());
            if(!entries.TryGetValue(command.FrameId,out var entry)||!SameHash(command.ContentSha256,entry.Proof.ContentSha256))
                throw new InvalidOperationException("Refresh the controlled preview before operating.");
            var source=entry.Proof.Geometry;
            return new(command.Sequence,command.Kind,source.Left+command.U*(source.Width-1),source.Top+command.V*(source.Height-1),command.Code,command.Delta,source);
        }
    }
    public void Invalidate(){lock(gate)entries.Clear();}
    public static bool ContentMatches(PreviewFrameProof? proof,byte[] jpeg,int width,int height)
    {
        if(proof is null||proof.Geometry is null||!proof.Geometry.IsValid||jpeg is null||jpeg.Length is <1 or >1_048_576||
            proof.Id is not{Length:32}||!proof.Id.All(Uri.IsHexDigit)||proof.ContentSha256 is not{Length:64}||!proof.ContentSha256.All(Uri.IsHexDigit)||
            width!=proof.Geometry.ImageWidth||height!=proof.Geometry.ImageHeight)return false;
        return SameHash(proof.ContentSha256,Convert.ToHexString(SHA256.HashData(jpeg)));
    }
    private long ReadTimestamp()
    {
        var now=clock.GetTimestamp();
        if(lastTimestamp is{} previous&&now<previous){entries.Clear();lastTimestamp=now;throw new InvalidOperationException("The control clock changed.");}
        lastTimestamp=now;return now;
    }
    private PreviewSourceGeometry ReadCurrent()
    {
        try{var current=currentSource();if(current?.IsValid==true)return current;}catch{ }
        entries.Clear();throw new InvalidOperationException("The native preview source is unavailable.");
    }
    private void Prune(long now,PreviewSourceGeometry current)
    {
        foreach(var pair in entries.ToArray())
        {
            var age=clock.GetElapsedTime(pair.Value.IssuedAt,now);
            if(age<TimeSpan.Zero||age>maximumAge||!pair.Value.Proof.Geometry.SameSource(current))entries.Remove(pair.Key);
        }
    }
    private static bool SameHash(string a,string b)=>CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a),Convert.FromHexString(b));
}
