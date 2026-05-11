using System.Buffers.Binary;

namespace DotnetTun.Platforms.MacOS.Networking;

internal static class MacUtunFrame
{
    public const int HeaderLength = 4;

    private const uint AddressFamilyInet = 2;
    private const uint AddressFamilyInet6 = 30;

    public static bool TryWriteFrame(ReadOnlySpan<byte> packet, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (!TryGetAddressFamily(packet, out var addressFamily))
        {
            return false;
        }

        var requiredLength = HeaderLength + packet.Length;
        if (destination.Length < requiredLength)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination[..HeaderLength], addressFamily);
        packet.CopyTo(destination[HeaderLength..requiredLength]);
        bytesWritten = requiredLength;
        return true;
    }

    public static bool TryReadPayload(ReadOnlySpan<byte> frame, Span<byte> destination, out int payloadLength)
    {
        payloadLength = 0;
        if (frame.Length < HeaderLength)
        {
            return false;
        }

        var addressFamily = ReadAddressFamily(frame);
        if (!IsSupportedAddressFamily(addressFamily))
        {
            return false;
        }

        payloadLength = frame.Length - HeaderLength;
        if (destination.Length < payloadLength)
        {
            payloadLength = 0;
            return false;
        }

        frame.Slice(HeaderLength, payloadLength).CopyTo(destination);
        return true;
    }

    public static uint ReadAddressFamily(ReadOnlySpan<byte> frame)
        => BinaryPrimitives.ReadUInt32BigEndian(frame[..HeaderLength]);

    public static bool IsSupportedAddressFamily(uint addressFamily)
        => addressFamily is AddressFamilyInet or AddressFamilyInet6;

    private static bool TryGetAddressFamily(ReadOnlySpan<byte> packet, out uint addressFamily)
    {
        if (packet.IsEmpty)
        {
            addressFamily = 0;
            return false;
        }

        addressFamily = (packet[0] >> 4) switch
        {
            4 => AddressFamilyInet,
            6 => AddressFamilyInet6,
            _ => 0,
        };

        return addressFamily != 0;
    }
}
