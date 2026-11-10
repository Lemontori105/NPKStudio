using System.Buffers.Binary;

namespace NPKStudio.Core.Audio;

/// <summary>Ogg 容器里的音频编码。</summary>
public enum OggCodec
{
    /// <summary>识别不出来（是 Ogg，但第一个包不是 Vorbis / Opus 头）。</summary>
    Unknown,

    /// <summary>Ogg Vorbis（DNF 音效常见格式）。</summary>
    Vorbis,

    /// <summary>Ogg Opus。</summary>
    Opus
}

/// <summary>
///     Ogg 音频的基本信息：编码、声道、采样率、时长。只读头部与最后一页的 granule，不解码音频。
///     <para>
///         格式依据：Ogg 页头（RFC 3533：<c>"OggS"</c>、版本、类型、granule int64、序号、页号、CRC、段数、段表），
///         Vorbis 识别头（<c>0x01 "vorbis"</c> + 版本 u32 + 声道 u8 + 采样率 u32 + 码率 3×i32），
///         Opus 头（<c>"OpusHead"</c> + 版本 u8 + 声道 u8 + pre-skip u16 + 原始采样率 u32，时长按 48kHz 计）。
///     </para>
///     <para>不校验 CRC：这里只用来显示信息和判断"是不是音效"，不作为数据完整性的依据。</para>
/// </summary>
/// <param name="Codec">编码。</param>
/// <param name="Channels">声道数（未知时为 0）。</param>
/// <param name="SampleRate">采样率 Hz（Opus 是原始输入采样率；未知时为 0）。</param>
/// <param name="Duration">时长；最后一页 granule 无效时为 <c>null</c>。</param>
/// <param name="NominalBitrate">Vorbis 头里声明的标称码率（bit/s），没有时为 0。</param>
public sealed record OggAudioInfo(OggCodec Codec, int Channels, int SampleRate, TimeSpan? Duration, int NominalBitrate)
{
    private const int PageHeaderSize = 27;
    private const int OpusGranuleRate = 48000;

    /// <summary>是否以 Ogg 页头 <c>"OggS"</c> 开头。</summary>
    public static bool IsOgg(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S';

    /// <summary>解析 Ogg 音频信息；不是 Ogg 或头部截断时返回 <c>null</c>。</summary>
    public static OggAudioInfo? TryParse(ReadOnlySpan<byte> data)
    {
        if (!IsOgg(data) || data.Length < PageHeaderSize)
        {
            return null;
        }

        var segmentCount = data[26];
        var packetStart = PageHeaderSize + segmentCount;
        if (data.Length < packetStart)
        {
            return null;
        }

        var packet = data[packetStart..];
        var lastGranule = FindLastGranule(data);

        if (packet.Length >= 30 && packet[0] == 0x01 && packet[1..7].SequenceEqual("vorbis"u8))
        {
            var channels = packet[11];
            var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(packet[12..16]);
            var nominal = BinaryPrimitives.ReadInt32LittleEndian(packet[20..24]);
            TimeSpan? duration = sampleRate > 0 && lastGranule > 0
                ? TimeSpan.FromSeconds((double)lastGranule / sampleRate)
                : null;
            return new OggAudioInfo(OggCodec.Vorbis, channels, Math.Max(0, sampleRate), duration, Math.Max(0, nominal));
        }

        if (packet.Length >= 19 && packet[..8].SequenceEqual("OpusHead"u8))
        {
            var channels = packet[9];
            var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(packet[10..12]);
            var inputRate = BinaryPrimitives.ReadInt32LittleEndian(packet[12..16]);
            TimeSpan? duration = lastGranule > preSkip
                ? TimeSpan.FromSeconds((double)(lastGranule - preSkip) / OpusGranuleRate)
                : null;
            return new OggAudioInfo(OggCodec.Opus, channels, Math.Max(0, inputRate), duration, 0);
        }

        return new OggAudioInfo(OggCodec.Unknown, 0, 0, null, 0);
    }

    /// <summary>从末尾往前找最后一个完整的 Ogg 页头，返回它的 granule position（找不到或为 -1 时返回 0）。</summary>
    private static long FindLastGranule(ReadOnlySpan<byte> data)
    {
        for (var i = data.Length - PageHeaderSize; i >= 0; i--)
        {
            if (data[i] != (byte)'O' || !IsOgg(data[i..]) || data[i + 4] != 0)
            {
                continue;
            }

            var granule = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(i + 6, 8));
            return granule > 0 ? granule : 0;
        }

        return 0;
    }
}
