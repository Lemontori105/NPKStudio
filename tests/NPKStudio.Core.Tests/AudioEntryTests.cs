using NPKStudio.Core.Audio;
using NPKStudio.Core.Formats;
using NPKStudio.Core.Formats.Npk;
using NPKStudio.TestSupport;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NPKStudio.Core.Tests;

/// <summary>
///     音效条目（NPK 里的 .ogg）：识别头部信息，整段替换后保存只改目标条目。
/// </summary>
[TestClass]
public sealed class AudioEntryTests
{
    [TestMethod]
    public void TryParse_ReadsVorbisHeaderAndDuration()
    {
        var info = OggAudioInfo.TryParse(OggTestData.Vorbis(channels: 2, sampleRate: 44100, totalSamples: 88200));

        Assert.IsNotNull(info);
        Assert.AreEqual(OggCodec.Vorbis, info.Codec);
        Assert.AreEqual(2, info.Channels);
        Assert.AreEqual(44100, info.SampleRate);
        Assert.AreEqual(128000, info.NominalBitrate);
        Assert.IsNotNull(info.Duration);
        Assert.AreEqual(2.0, info.Duration.Value.TotalSeconds, 1e-9);
    }

    [TestMethod]
    public void TryParse_ReadsOpusHeader_DurationAt48kMinusPreSkip()
    {
        var info = OggAudioInfo.TryParse(OggTestData.Opus(channels: 1, inputRate: 22050, preSkip: 312, granule: 48000 + 312));

        Assert.IsNotNull(info);
        Assert.AreEqual(OggCodec.Opus, info.Codec);
        Assert.AreEqual(1, info.Channels);
        Assert.AreEqual(22050, info.SampleRate);
        Assert.AreEqual(1.0, info.Duration!.Value.TotalSeconds, 1e-9);
    }

    [TestMethod]
    public void TryParse_UnknownPacket_IsOggButUnknownCodec()
    {
        var data = OggTestData.UnknownCodec();

        Assert.IsTrue(OggAudioInfo.IsOgg(data));
        var info = OggAudioInfo.TryParse(data);
        Assert.IsNotNull(info);
        Assert.AreEqual(OggCodec.Unknown, info.Codec);
        Assert.IsNull(info.Duration);
    }

    [TestMethod]
    public void TryParse_RejectsNonOggAndTruncatedData()
    {
        Assert.IsFalse(OggAudioInfo.IsOgg("Neople Img File\0"u8));
        Assert.IsNull(OggAudioInfo.TryParse("Neople Img File\0"u8));
        Assert.IsNull(OggAudioInfo.TryParse("OggS"u8));
        Assert.IsNull(OggAudioInfo.TryParse([]));
        // 截在识别头中间：仍是 Ogg，但认不出编码，不抛异常
        var cut = OggTestData.Vorbis(2, 44100, 1000)[..40];
        Assert.AreEqual(OggCodec.Unknown, OggAudioInfo.TryParse(cut)!.Codec);
    }

    /// <summary>替换一个 .ogg 条目：只有它变成新字节，另一个条目逐字节不变；替换后的条目被标记为已修改。</summary>
    [TestMethod]
    public void ReplaceRawContent_SavesNewBytes_AndLeavesOtherEntriesUntouched()
    {
        var first = OggTestData.Vorbis(1, 22050, 22050, fill: 0x01);
        var second = OggTestData.Vorbis(2, 44100, 44100, fill: 0x02);
        var replacement = OggTestData.Vorbis(1, 22050, 44100, fill: 0x03);

        var source = Path.Combine(Path.GetTempPath(), $"audio_src_{Guid.NewGuid():N}.NPK");
        var target = Path.Combine(Path.GetTempPath(), $"audio_dst_{Guid.NewGuid():N}.NPK");
        try
        {
            NpkWriter.WriteFileStreaming(source, [
                NpkEntrySource.FromBytes("sounds/voice/hello.ogg", first),
                NpkEntrySource.FromBytes("sounds/voice/bye.ogg", second)
            ]);

            using (var archive = NpkArchive.Open(source))
            {
                var entry = archive.Entries.Single(e => e.Path == "sounds/voice/hello.ogg");
                var img = archive.LoadImg(entry);

                Assert.AreEqual(ImgVersion.Other, img.Version);
                CollectionAssert.AreEqual(first, img.RawContent.ToArray());
                Assert.IsFalse(entry.IsModified);

                img.ReplaceRawContent(replacement);

                Assert.IsTrue(entry.IsModified);
                CollectionAssert.AreEqual(replacement, img.RawContent.ToArray());
                archive.Save(target);
            }

            using var saved = NpkArchive.Open(target);
            Assert.AreEqual(2, saved.Entries.Count);
            CollectionAssert.AreEqual(replacement, saved.ReadRaw(saved.Entries.Single(e => e.Path == "sounds/voice/hello.ogg")));
            CollectionAssert.AreEqual(second, saved.ReadRaw(saved.Entries.Single(e => e.Path == "sounds/voice/bye.ogg")));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }

    [TestMethod]
    public void ReplaceRawContent_OnImg_IsRejected_AndRawContentIsEmpty()
    {
        var img = ImgFile.Create(ImgVersion.Ver2, "sprite/test.img");

        Assert.AreEqual(0, img.RawContent.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => img.ReplaceRawContent([1, 2, 3]));
    }
}
