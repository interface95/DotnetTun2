namespace DotnetTun.Core.Packets;

public static class Ipv4Checksum
{
    public static bool IsValid(ReadOnlySpan<byte> ipv4Packet)
    {
        if (!Ipv4Packet.TryParse(ipv4Packet.ToArray(), out Ipv4Packet packet))
        {
            return false;
        }

        return InternetChecksum.IsValid(ipv4Packet[..packet.HeaderLength]);
    }
}
