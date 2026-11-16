using System.Buffers.Binary;
using System.Text;
namespace DesktopToolbox.Mac.Services.Display;
public sealed record MacEdidInfo(MacDisplayIdentity Identity,string Name,string? SerialNumber);
public static class MacEdid
{
    public static bool TryParse(ReadOnlySpan<byte> bytes,out MacEdidInfo? info)
    {
        info=null;
        ReadOnlySpan<byte> header=[0,255,255,255,255,255,255,0];
        if(bytes.Length<128||!bytes[..8].SequenceEqual(header))return false;
        int sum=0;foreach(var value in bytes[..128])sum+=value;
        if((sum&255)!=0)return false;
        var identity=new MacDisplayIdentity(BinaryPrimitives.ReadUInt16BigEndian(bytes[8..]),BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]));
        if(identity.VendorId==0||identity.ProductId==0)return false;
        var serial=BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        string? name=null, textSerial=null;
        for(int offset=54;offset<=108;offset+=18)
        {
            if(bytes[offset]!=0||bytes[offset+1]!=0||bytes[offset+2]!=0)continue;
            var text=Encoding.ASCII.GetString(bytes.Slice(offset+5,13)).Trim('\0',' ','\r','\n');
            if(bytes[offset+3]==0xFC&&text.Length>0)name=text;
            if(bytes[offset+3]==0xFF&&text.Length>0)textSerial=text;
        }
        info=new(identity,name??$"Display {identity.VendorId:X4}:{identity.ProductId:X4}",textSerial??(serial==0?null:serial.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return true;
    }
}
public static class MacDisplaySelection
{
    public static MacDisplayDescriptor RequireUnique(IEnumerable<MacDisplayDescriptor> displays,MacDisplayIdentity identity)
    {
        var matches=displays.Where(x=>x.Identity==identity).Take(2).ToArray();
        return matches.Length switch {0=>throw new IOException("display_not_found"),1=>matches[0],_=>throw new IOException("display_ambiguous")};
    }
}
