using System.Text.Json;

namespace Nian.Input.Security;

public sealed class DeviceTrustStore : IDisposable
{
    private sealed record Store(int Version,TrustedDevice[] Devices);
    private readonly string path; private readonly FileStream lease; private readonly SemaphoreSlim mutation=new(1,1); private readonly object gate=new(); private int disposed;
    private Dictionary<string,TrustedDevice> devices;
    private DeviceTrustStore(string path,Dictionary<string,TrustedDevice> devices,FileStream lease) {this.path=path;this.devices=devices;this.lease=lease;}
    public event Action<string>? Changed;
    public IReadOnlyList<TrustedDevice> Devices {get{lock(gate)return devices.Values.OrderBy(d=>d.DeviceName).ToArray();}}
    public bool Allows(string fingerprint,DevicePermission permission=DevicePermission.Control)
    { if(!TrustValidation.Fingerprint(fingerprint))return false;lock(gate)return devices.TryGetValue(fingerprint.ToUpperInvariant(),out var device)&&(device.Permissions&permission)==permission&&permission!=DevicePermission.None; }
    public static async Task<DeviceTrustStore> OpenAsync(string profileDirectory,CancellationToken cancellationToken=default)
    {
        PrivateFiles.Directory(profileDirectory);var path=Path.Combine(profileDirectory,"trust.json");PrivateFiles.Check(path);
        var lockPath=Path.Combine(profileDirectory,".trust.lock");PrivateFiles.Check(lockPath);var lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);PrivateFiles.Protect(lockPath);
        try {
        if(!File.Exists(path))await PrivateFiles.WriteAsync(path,JsonSerializer.SerializeToUtf8Bytes(new Store(2,[]),TrustJson.Options),cancellationToken,false);
        var store=JsonSerializer.Deserialize<Store>(PrivateFiles.Read(path),TrustJson.Options)??throw new InvalidDataException("Device trust store is empty.");
        if(store.Version!=2||store.Devices is null||store.Devices.Length>64)throw new InvalidDataException("Device trust store is invalid.");
        var map=new Dictionary<string,TrustedDevice>(StringComparer.Ordinal);
        foreach(var device in store.Devices) { Validate(device);if(!map.TryAdd(device.Fingerprint.ToUpperInvariant(),device with{Fingerprint=device.Fingerprint.ToUpperInvariant()}))throw new InvalidDataException("Device trust contains duplicate identities."); }
        return new(path,map,lease);
        } catch {lease.Dispose();throw;}
    }
    public Task GrantAsync(TrustedDevice device,CancellationToken cancellationToken=default)
    {Validate(device);return ChangeAsync(device.Fingerprint,device with{Fingerprint=device.Fingerprint.ToUpperInvariant()},cancellationToken);}
    internal Task GrantAfterApprovalAsync(TrustedDevice device,Func<IDisposable> commitGuard,Action committed,CancellationToken cancellationToken)
    {Validate(device);return ChangeAsync(device.Fingerprint,device with{Fingerprint=device.Fingerprint.ToUpperInvariant()},cancellationToken,commitGuard,committed);}
    public Task RevokeAsync(string fingerprint,CancellationToken cancellationToken=default)=>ChangeAsync(TrustValidation.Normalize(fingerprint),null,cancellationToken);
    private async Task ChangeAsync(string fingerprint,TrustedDevice? device,CancellationToken ct,Func<IDisposable>? commitGuard=null,Action? committed=null)
    {
        fingerprint=TrustValidation.Normalize(fingerprint);await mutation.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed)!=0,this);
            Dictionary<string,TrustedDevice> next;lock(gate)next=new(devices,StringComparer.Ordinal);
            if(device is null)next.Remove(fingerprint);else next[fingerprint]=device;
            if(next.Count>64)throw new InvalidOperationException("The device trust limit has been reached.");
            await PrivateFiles.WriteAsync(path,JsonSerializer.SerializeToUtf8Bytes(new Store(2,next.Values.ToArray()),TrustJson.Options),ct,commitGuard:commitGuard,committed:()=>{lock(gate)devices=next;committed?.Invoke();});
            // Synchronous notification is a security boundary: revoke returns only after active inputs are released.
            if(Changed is { } handlers)foreach(Action<string> handler in handlers.GetInvocationList())try{handler(fingerprint);}catch{/* One presentation subscriber cannot suppress another subscriber's release notification. */}
        }
        finally {mutation.Release();}
    }
    private static void Validate(TrustedDevice device)
    {ArgumentNullException.ThrowIfNull(device);if(!TrustValidation.Fingerprint(device.Fingerprint)||!TrustValidation.DeviceName(device.DeviceName)||!TrustValidation.Permissions(device.Permissions))throw new InvalidDataException("The trusted device record is invalid.");}
    public void Dispose() {mutation.Wait();try{if(Interlocked.Exchange(ref disposed,1)==0)lease.Dispose();}finally{mutation.Release();}}
}
