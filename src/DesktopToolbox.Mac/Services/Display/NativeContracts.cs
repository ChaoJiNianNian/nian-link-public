namespace DesktopToolbox.Mac.Services.Display;
internal interface IMacNativeDisplayApi
{
    IReadOnlyList<MacDisplayDescriptor> List();
    IMacDdcChannel Open(MacDisplayDescriptor descriptor);
}
internal interface IMacDdcChannel : IDisposable
{
    int Write(byte[] packet);
    (int Error, byte[] Bytes) Read(uint offset, int length);
}
