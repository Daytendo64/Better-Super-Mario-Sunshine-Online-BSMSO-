using System.Buffers.Binary;

namespace SMSO.Net.MarioPack;

/// <summary>Nintendo Yaz0 compress / decompress for GameCube SZS archives.</summary>
public static class Yaz0
{
    public static bool IsYaz0(ReadOnlySpan<byte> data) =>
        data.Length >= 16 &&
        data[0] == (byte)'Y' &&
        data[1] == (byte)'a' &&
        data[2] == (byte)'z' &&
        data[3] == (byte)'0';

    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (!IsYaz0(data))
            throw new InvalidDataException("Not a Yaz0 stream.");

        var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        if (uncompressedSize == 0 || uncompressedSize > 64 * 1024 * 1024)
            throw new InvalidDataException("Invalid Yaz0 uncompressed size.");

        var output = new byte[uncompressedSize];
        int src = 16;
        int dst = 0;
        byte group = 0;
        int bitsLeft = 0;

        while (dst < output.Length)
        {
            if (bitsLeft == 0)
            {
                if (src >= data.Length)
                    throw new InvalidDataException("Truncated Yaz0 stream.");
                group = data[src++];
                bitsLeft = 8;
            }

            bitsLeft--;
            if ((group & (1 << bitsLeft)) != 0)
            {
                if (src >= data.Length || dst >= output.Length)
                    throw new InvalidDataException("Truncated Yaz0 stream.");
                output[dst++] = data[src++];
                continue;
            }

            if (src + 1 >= data.Length)
                throw new InvalidDataException("Truncated Yaz0 match.");

            int b1 = data[src++];
            int b2 = data[src++];
            int dist = ((b1 & 0x0F) << 8) | b2;
            int copyLen = b1 >> 4;
            if (copyLen == 0)
            {
                if (src >= data.Length)
                    throw new InvalidDataException("Truncated Yaz0 long match.");
                copyLen = data[src++] + 0x12;
            }
            else
            {
                copyLen += 2;
            }

            int copySrc = dst - dist - 1;
            if (copySrc < 0)
                throw new InvalidDataException("Invalid Yaz0 back-reference.");

            for (int i = 0; i < copyLen && dst < output.Length; i++)
                output[dst++] = output[copySrc++];
        }

        return output;
    }

    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        // Simple RLE-friendly encoder: emit literals when no useful match is found.
        // Correctness over ratio — packs are installed once, not streamed.
        var output = new List<byte>(data.Length + 16);
        output.AddRange(new byte[] { (byte)'Y', (byte)'a', (byte)'z', (byte)'0' });
        var sizeBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sizeBytes, (uint)data.Length);
        output.AddRange(sizeBytes);
        output.AddRange(new byte[8]); // reserved

        int src = 0;
        while (src < data.Length)
        {
            int groupPos = output.Count;
            output.Add(0);
            byte group = 0;

            for (int bit = 7; bit >= 0 && src < data.Length; bit--)
            {
                FindMatch(data, src, out int matchOff, out int matchLen);
                if (matchLen >= 3)
                {
                    int dist = src - matchOff - 1;
                    if (matchLen < 0x12)
                    {
                        output.Add((byte)(((matchLen - 2) << 4) | (dist >> 8)));
                        output.Add((byte)(dist & 0xFF));
                    }
                    else
                    {
                        int encLen = Math.Min(matchLen, 0x111);
                        output.Add((byte)(dist >> 8));
                        output.Add((byte)(dist & 0xFF));
                        output.Add((byte)(encLen - 0x12));
                        matchLen = encLen;
                    }

                    src += matchLen;
                }
                else
                {
                    group |= (byte)(1 << bit);
                    output.Add(data[src++]);
                }
            }

            output[groupPos] = group;
        }

        return output.ToArray();
    }

    private static void FindMatch(ReadOnlySpan<byte> data, int src, out int matchOff, out int matchLen)
    {
        matchOff = 0;
        matchLen = 0;
        if (src < 1)
            return;

        int windowStart = Math.Max(0, src - 0x1000);
        int maxLen = Math.Min(0x111, data.Length - src);
        if (maxLen < 3)
            return;

        for (int i = windowStart; i < src; i++)
        {
            int len = 0;
            while (len < maxLen && data[i + len] == data[src + len])
                len++;
            if (len > matchLen)
            {
                matchLen = len;
                matchOff = i;
                if (matchLen == maxLen)
                    return;
            }
        }
    }
}
