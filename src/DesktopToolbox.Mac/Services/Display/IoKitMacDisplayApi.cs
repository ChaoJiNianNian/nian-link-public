namespace DesktopToolbox.Mac.Services.Display;
internal sealed class IoKitMacDisplayApi : IMacNativeDisplayApi
{
    public IReadOnlyList<MacDisplayDescriptor> List()
    {
        MacIoKit.EnsureAvailable();
        // IOServiceGetMatchingServices consumes the matching dictionary, including on failure.
        var matching=MacIoKit.IOServiceMatching("DCPAVServiceProxy");
        if(matching==0)throw new IOException("display_matching_unavailable");
        int error=MacIoKit.IOServiceGetMatchingServices(0,matching,out var iterator);
        using var iter=new IoReference(iterator);
        if(error!=0)throw new IOException($"display_enumeration_failed:{error}");
        var displays=new List<MacDisplayDescriptor>();
        uint entry;
        while((entry=MacIoKit.IOIteratorNext(iterator))!=0)
        {
            using var current=new IoReference(entry);
            if(MacIoKit.Property(entry,"Location")!="External")continue;
            if(MacIoKit.IORegistryEntryGetRegistryEntryID(entry,out var registryId)!=0||registryId==0)continue;
            using var service=new CfReference(MacIoKit.IOAVServiceCreateWithService(0,entry));
            if(service.IsInvalid)continue;
            var edid=MacIoKit.Edid(service.DangerousGetHandle());
            if(edid is null)continue;
            displays.Add(new(edid.Identity,registryId,entry,edid.Name,edid.SerialNumber,ChipAddress(entry)));
        }
        return displays;
    }
    public IMacDdcChannel Open(MacDisplayDescriptor descriptor)
    {
        MacIoKit.EnsureAvailable();
        var matching=MacIoKit.IORegistryEntryIDMatching(descriptor.RegistryId);
        if(matching==0)throw new IOException("display_registry_id_unavailable");
        using var current=new IoReference(MacIoKit.IOServiceGetMatchingService(0,matching));
        if(current.IsInvalid||MacIoKit.Property(current.Value,"Location")!="External")throw new IOException("display_changed");
        var service=new CfReference(MacIoKit.IOAVServiceCreateWithService(0,current.Value));
        try {
            if(service.IsInvalid||MacIoKit.Edid(service.DangerousGetHandle())?.Identity!=descriptor.Identity)throw new IOException("display_changed");
            return new IoKitDdcChannel(service,ChipAddress(current.Value));
        } catch {service.Dispose();throw;}
    }
    private static uint ChipAddress(uint entry)
    {
        if(MacIoKit.IORegistryEntryGetParentEntry(entry,"IOService",out var parent)!=0)return 0x37;
        using var owner=new IoReference(parent);
        return MacIoKit.Property(parent,"EPICProviderClass")=="AppleDCPMCDP29XX"?0xB7u:0x37u;
    }
}
internal sealed class IoKitDdcChannel(CfReference service,uint chipAddress):IMacDdcChannel
{
    public int Write(byte[] packet)=>MacIoKit.IOAVServiceWriteI2C(service.DangerousGetHandle(),chipAddress,0x51,packet,(uint)packet.Length);
    public (int Error,byte[] Bytes) Read(uint offset,int length)
    {
        var buffer=new byte[length];
        int error=MacIoKit.IOAVServiceReadI2C(service.DangerousGetHandle(),chipAddress,offset,buffer,(uint)buffer.Length);
        return (error,buffer);
    }
    public void Dispose()=>service.Dispose();
}
