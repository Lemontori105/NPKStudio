using Avalonia.Headless.XUnit;

using NPKStudio.Core.Formats.Npk;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.Services;
using NPKStudio.Desktop.ViewModels;
using NPKStudio.Desktop.ViewModels.Channels;
using NPKStudio.Desktop.Views;
using NPKStudio.TestSupport;

using Xunit;

namespace NPKStudio.Desktop.Tests;

/// <summary>
///     音效条目（NPK 里的 .ogg）：识别、信息行、导出、替换后的脏状态。
///     <para>试听与重新编码要靠外部的 ffplay / ffmpeg / oggenc，这里只测不依赖外部工具的部分。</para>
/// </summary>
public sealed class AudioEntryUiTests
{
    private static readonly byte[] Hello = OggTestData.Vorbis(channels: 1, sampleRate: 22050, totalSamples: 22050, fill: 0x01);
    private static readonly byte[] Bye = OggTestData.Vorbis(channels: 2, sampleRate: 44100, totalSamples: 88200, fill: 0x02);

    private static string WriteSoundPack() {
        var path = Path.Combine(Path.GetTempPath(), $"sounds_{Guid.NewGuid():N}.NPK");
        NpkWriter.WriteFileStreaming(path, [
            NpkEntrySource.FromBytes("sounds/voice/hello.ogg", Hello),
            NpkEntrySource.FromBytes("sounds/voice/bye.ogg", Bye)
        ]);
        return path;
    }

    private static async Task<(MainWindow Window, MainWindowViewModel Vm, string Pack)> OpenPackAsync() {
        var pack = WriteSoundPack();
        var window = new MainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        await vm.OpenAsync(pack);

        // 列表顺序由程序决定（可能按路径排序），用例只认 hello.ogg
        var hello = vm.Entries.Single(e => e.Name == "hello.ogg");
        if (!ReferenceEquals(vm.SelectedEntry, hello)) {
            vm.SelectedEntry = hello;
            await vm.PendingFrameLoad!;
        }

        return (window, vm, pack);
    }

    [AvaloniaFact]
    public async Task OpeningSoundPack_ShowsAudioPanelState() {
        var (window, vm, pack) = await OpenPackAsync();
        try {
            Assert.Equal(2, vm.Entries.Count);
            Assert.True(vm.IsAudioEntry);
            Assert.True(vm.CanReplaceAudio);
            Assert.Contains("Vorbis", vm.AudioInfoText, StringComparison.Ordinal);
            Assert.Contains("22050", vm.AudioInfoText, StringComparison.Ordinal);
            Assert.Equal(TranslationCatalog.Instance["panel.audioEntry"], vm.FrameAreaEmptyText);
            Assert.False(vm.IsDirty);
        } finally {
            window.Close();
            File.Delete(pack);
        }
    }

    /// <summary>导出全部：按包内路径建子目录，字节与包里一致。</summary>
    [AvaloniaFact]
    public async Task ExportAll_WritesEverySoundUnderPackagePaths() {
        var (window, vm, pack) = await OpenPackAsync();
        var folder = Path.Combine(Path.GetTempPath(), $"sounds_out_{Guid.NewGuid():N}");
        try {
            await vm.ExportAudioAsync(folder, currentOnly: false);

            Assert.Equal(Hello, File.ReadAllBytes(Path.Combine(folder, "sounds", "voice", "hello.ogg")));
            Assert.Equal(Bye, File.ReadAllBytes(Path.Combine(folder, "sounds", "voice", "bye.ogg")));
        } finally {
            window.Close();
            File.Delete(pack);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    ///     替换后：标记"有未保存的修改"，而且换到别的条目也不会丢（帧编辑的脏标记会随换条目清零，音效不能这样）；
    ///     导出拿到的是替换后的内容。
    /// </summary>
    [AvaloniaFact]
    public async Task Replace_MarksDirty_SurvivesEntrySwitch_AndExportsNewBytes() {
        var (window, vm, pack) = await OpenPackAsync();
        var folder = Path.Combine(Path.GetTempPath(), $"sounds_out_{Guid.NewGuid():N}");
        try {
            var first = vm.SelectedEntry!;
            var replacement = OggTestData.Vorbis(channels: 1, sampleRate: 22050, totalSamples: 44100, fill: 0x09);

            vm.ActiveChannel.ReplaceCurrentAudio(replacement);
            Assert.True(vm.IsDirty);
            Assert.True(vm.HasUnsavedAudio);
            Assert.EndsWith(TranslationCatalog.Instance["audio.modifiedSuffix"], vm.AudioInfoText, StringComparison.Ordinal);

            vm.SelectedEntry = vm.Entries.Single(e => !ReferenceEquals(e, first));
            await vm.PendingFrameLoad!;
            Assert.True(vm.IsDirty, "换条目后音效替换的脏标记不能丢");

            await vm.ExportAudioAsync(folder, currentOnly: false);
            Assert.Equal(replacement, File.ReadAllBytes(Path.Combine(folder, first.Path.Replace('/', Path.DirectorySeparatorChar))));
        } finally {
            window.Close();
            File.Delete(pack);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AudioExportPath_RejectsPathsEscapingTheFolder() {
        var folder = Path.Combine(Path.GetTempPath(), "npk-audio-export");

        Assert.EndsWith(Path.Combine("sounds", "a.ogg"), FrameChannel.AudioExportPath(folder, "sounds/a.ogg"), StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("sounds", "a.ogg"), FrameChannel.AudioExportPath(folder, "sounds\\a.ogg"), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => FrameChannel.AudioExportPath(folder, "../evil.ogg"));
        Assert.Throws<InvalidOperationException>(() => FrameChannel.AudioExportPath(folder, "sounds/../../evil.ogg"));
    }

    /// <summary>源文件已经是参数一致的 Ogg Vorbis：原样使用，不调用任何外部工具。</summary>
    [Fact]
    public async Task ToOggVorbis_MatchingVorbisInput_IsUsedAsIs() {
        var input = Path.Combine(Path.GetTempPath(), $"as_is_{Guid.NewGuid():N}.ogg");
        try {
            await File.WriteAllBytesAsync(input, Bye);
            using var tools = new AudioTools();

            var result = await tools.ToOggVorbisAsync(input, sampleRate: 44100, channels: 2);

            Assert.Null(result.Error);
            Assert.Null(result.MissingTool);
            Assert.False(result.Reencoded);
            Assert.Equal(Bye, result.Ogg);
        } finally {
            File.Delete(input);
        }
    }

    [Fact]
    public void Find_ReturnsNullForMissingTool() =>
        Assert.Null(AudioTools.Find($"no-such-tool-{Guid.NewGuid():N}"));
}
