using System.Net;
using System.Net.Sockets;

namespace DummySmtp;

public sealed class RelayNetwork
{
    private readonly byte[] network;
    public string Network { get; }
    public string Type { get; }
    public int PrefixLength { get; }
    private RelayNetwork(IPAddress address, int prefix, bool subnet)
    {
        network = address.GetAddressBytes(); PrefixLength = prefix;
        for (int i = 0; i < network.Length; i++)
        {
            int bits = Math.Clamp(prefix - i * 8, 0, 8);
            network[i] &= (byte)(0xff << (8 - bits));
        }
        Network = new IPAddress(network).ToString() + (subnet ? $"/{prefix}" : "");
        Type = (address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6") + (subnet ? " subnet" : " address");
    }
    public static RelayNetwork Parse(string text)
    {
        var parts = text.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address)) throw new ArgumentException($"Invalid IP address or CIDR: {text}");
        int max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > max))
            throw new ArgumentException($"Invalid prefix in '{text}': expected 0–{max}.");
        if (address.IsIPv4MappedToIPv6)
        {
            if (prefix < 96) throw new ArgumentException($"Use an IPv4 CIDR for mapped address '{text}'.");
            address = address.MapToIPv4(); prefix -= 96;
        }
        return new RelayNetwork(address, prefix, parts.Length == 2);
    }
    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length != network.Length) return false;
        for (int i = 0; i < bytes.Length; i++)
        {
            int bits = Math.Clamp(PrefixLength - i * 8, 0, 8);
            if ((bytes[i] & (byte)(0xff << (8 - bits))) != network[i]) return false;
        }
        return true;
    }
}

public static class RelayAccess
{
    public static string[] Entries(string value) => value.Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static RelayNetwork[] Parse(string value) => Entries(value).Select(RelayNetwork.Parse).ToArray();
}
