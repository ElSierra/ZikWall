using System.Buffers.Binary;

namespace ZikWall.WinUI.Services;

internal static class DnsPacketParser
{
    internal static ReadOnlySpan<byte> WifiPayload(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 24) return [];
        ushort control = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        // Data, excluding null/control frames, fragments and A-MSDU aggregation.
        if ((control & 0x4f) != 8 || (control & 0x400) != 0 || (frame[22] & 15) != 0) return [];
        int header = (control & 0x300) == 0x300 ? 30 : 24;
        if ((control & 0x80) != 0)
        {
            if (frame.Length < header + 2 || (frame[header] & 0x80) != 0) return [];
            header += 2;
            if ((control & 0x8000) != 0) header += 4;
        }
        if (frame.Length < header + 8 || frame[header] != 0xaa || frame[header + 1] != 0xaa || frame[header + 2] != 3 ||
            frame[header + 3] != 0 || frame[header + 4] != 0 || frame[header + 5] is not (0 or 0xf8)) return [];
        int type = U16(frame, header + 6);
        var ip = frame[(header + 8)..];
        if (type is not (0x800 or 0x86dd) || ip.IsEmpty || ip[0] >> 4 != (type == 0x800 ? 4 : 6)) return [];
        return Payload(ip, true);
    }
    // Returns only a complete UDP/53 response payload. Fragmented IP and TCP
    // streams require reassembly and are deliberately not guessed at.
    internal static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> frame, bool rawIp = false)
    {
        if (!rawIp)
        {
            if (frame.Length < 14) return [];
            int offset = 14, type = U16(frame, 12);
            for (int tags = 0; type is 0x8100 or 0x88a8 && tags < 2; tags++)
            {
                if (frame.Length < offset + 4) return [];
                type = U16(frame, offset + 2); offset += 4;
            }
            if (type is not (0x800 or 0x86dd)) return [];
            frame = frame[offset..];
            if (frame.Length == 0 || frame[0] >> 4 != (type == 0x800 ? 4 : 6)) return [];
        }
        if (frame.Length < 20) return [];
        int transport, end, protocol;
        if (frame[0] >> 4 == 4)
        {
            transport = (frame[0] & 15) * 4; end = U16(frame, 2); protocol = frame[9];
            if (transport < 20 || end > frame.Length || end < transport || (U16(frame, 6) & 0x3fff) != 0) return [];
        }
        else if (frame[0] >> 4 == 6)
        {
            if (frame.Length < 40) return [];
            end = 40 + U16(frame, 4); transport = 40; protocol = frame[6];
            if (end > frame.Length) return [];
            int extensions = 0;
            while (protocol is 0 or 43 or 60 or 51 or 44)
            {
                if (++extensions > 8 || transport + 8 > end) return [];
                int length;
                if (protocol == 44)
                {
                    if (U16(frame, transport + 2) != 0) return []; // Atomic fragments only.
                    length = 8;
                }
                else length = protocol == 51 ? (frame[transport + 1] + 2) * 4 : (frame[transport + 1] + 1) * 8;
                protocol = frame[transport]; transport += length;
                if (transport > end) return [];
            }
        }
        else return [];
        if (protocol != 17 || transport + 8 > end || U16(frame, transport) != 53) return [];
        int udpLength = U16(frame, transport + 4);
        if (udpLength < 20 || transport + udpLength != end) return [];
        return frame.Slice(transport + 8, udpLength - 8);
    }
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
}
