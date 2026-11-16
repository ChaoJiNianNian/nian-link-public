namespace DesktopToolbox.Mac.Models;

public sealed record MacDisplayBridgeOptions(string Action, uint VendorId = 0, uint ProductId = 0, ushort Input = 0)
{
    public static MacDisplayBridgeOptions Parse(string[] args)
    {
        if (args.Length == 1 && args[0] == "list") return new("list");
        if (args.Length == 0 || args[0] is not ("read" or "set")) throw new ArgumentException("Unknown display bridge action.");
        var values = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--vendor" or "--product" or "--input") ||
                !uint.TryParse(args[index + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
                !values.TryAdd(args[index], value)) throw new ArgumentException("Invalid or duplicate display bridge argument.");
        }
        if (!values.TryGetValue("--vendor", out var vendor) || vendor is 0 or > 65535 ||
            !values.TryGetValue("--product", out var product) || product is 0 or > 65535) throw new ArgumentException("Invalid display identity.");
        if (args[0] == "read")
        {
            if (values.Count != 2) throw new ArgumentException("Read does not accept an input value.");
            return new("read", vendor, product);
        }
        if (!values.TryGetValue("--input", out var input) || input is 0 or > 255 || values.Count != 3) throw new ArgumentException("Invalid display input value.");
        return new("set", vendor, product, (ushort)input);
    }
}
