using System.Buffers.Binary;
using System.Text;

namespace NPKStudio.TestSupport;

/// <summary>
///     手工拼出最小的 Ogg 字节流（只有头部页 + 一个末页），用来测音效识别与替换，不需要真实音频。
///     <para>不算 CRC：被测代码不校验 CRC。</para>
/// </summary>
internal static class OggTestData
{
    /// <summary>Ogg Vorbis：识别头页 + 一个 granule = <paramref name="totalSamples" /> 的末页。</summary>
    public static byte[] Vorbis(int channels, int sampleRate, long totalSamples, int nominalBitrate = 128000, byte fill = 0xAB)
    {
        var ident = new byte[30];
        ident[0] = 0x01;
        Encoding.ASCII.GetBytes("vorbis").CopyTo(ident, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(ident.AsSpan(7), 0);
        ident[11] = (byte)channels;
        BinaryPrimitives.WriteInt32LittleEndian(ident.AsSpan(12), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(ident.AsSpan(16), 0);
        BinaryPrimitives.WriteInt32LittleEndian(ident.AsSpan(20), nominalBitrate);
        BinaryPrimitives.WriteInt32LittleEndian(ident.AsSpan(24), 0);
        ident[28] = 0xB8;
        ident[29] = 0x01;

        return Concat(Page(0x02, 0, 0, ident), Page(0x04, totalSamples, 1, Filler(200, fill)));
    }

    /// <summary>Ogg Opus：OpusHead 页 + 一个末页（granule 按 48kHz，含 pre-skip）。</summary>
    public static byte[] Opus(int channels, int inputRate, ushort preSkip, long granule)
    {
        var head = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1;
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), preSkip);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(12), inputRate);

        return Concat(Page(0x02, 0, 0, head), Page(0x04, granule, 1, Filler(100, 0x11)));
    }

    /// <summary>是 Ogg 页头，但第一个包既不是 Vorbis 也不是 Opus。</summary>
    public static byte[] UnknownCodec() => Page(0x02, 0, 0, Encoding.ASCII.GetBytes("NOT-VORBIS-OR-OPUS-HEADER-PACKET"));

    /// <summary>一个 Ogg 页：27 字节页头 + 段表 + 数据（数据按 255 字节切段）。</summary>
    private static byte[] Page(byte headerType, long granule, int sequence, byte[] packet)
    {
        var segments = new List<byte>();
        var remaining = packet.Length;
        while (remaining >= 255)
        {
            segments.Add(255);
            remaining -= 255;
        }

        segments.Add((byte)remaining);

        var page = new byte[27 + segments.Count + packet.Length];
        Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0);
        page[4] = 0;
        page[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(14), 1);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)segments.Count;
        segments.ToArray().CopyTo(page, 27);
        packet.CopyTo(page, 27 + segments.Count);
        return page;
    }

    private static byte[] Filler(int length, byte value)
    {
        var data = new byte[length];
        Array.Fill(data, value);
        return data;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
