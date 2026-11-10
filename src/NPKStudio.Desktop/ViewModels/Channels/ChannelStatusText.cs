using NPKStudio.Core.Audio;
using NPKStudio.Core.Formats;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.Services;

namespace NPKStudio.Desktop.ViewModels.Channels;

/// <summary>
///     通道状态栏的<b>纯文案组合</b>：帧计数、选中帧几何、面板计数。
///     <para>
///         <b>无状态、无通知、不持有任何对象</b>：只把数字拼成本地化字符串，存值与 raise 由调用方负责。
///         所以它可以被安全地共享，也可以随时被丢弃——不参与任何生命周期。
///     </para>
///     <para>
///         <b>为什么是 sealed 实例而不是 static 类</b>：通道按"聚合根 + 三个协作者"组合，
///         调用形状统一为 <c>StatusText.Xxx(...)</c>，读代码时一眼能看出"这里只是在拼文案"。
///         类型 sealed、方法非虚、无字段，JIT 可去虚拟化并内联，运行时开销与 static 相同。
///     </para>
///     <para>
///         <b>热路径纪律</b>：这里的每个方法都会分配字符串（<c>Loc.F</c> 与插值），
///         所以只在"帧计数 / 选中帧 / 可见性真的变了"时被调用，<b>绝不</b>放进逐帧渲染或逐像素循环。
///     </para>
/// </summary>
internal sealed class ChannelStatusText
{
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    /// <summary>
    ///     帧计数文案：1 基序号 / 总数；有隐藏帧追加「隐 N」、有变更帧追加「改 N」。
    /// </summary>
    /// <param name="index">当前帧的 0 基序号；<c>-1</c> 表示没有选中帧（显示 <c>0 / 总数</c>）。</param>
    /// <param name="total">帧总数。</param>
    /// <param name="hidden">被隐藏的帧数（0 时不追加）。</param>
    /// <param name="changed">有变更标记的帧数（0 时不追加）。</param>
    public string FrameCounter(int index, int total, int hidden, int changed) {
        var counter = index >= 0 ? $"{index + 1} / {total}" : $"0 / {total}";
        if (hidden > 0) {
            counter += Loc.F("panel.hiddenSuffix", hidden);
        }

        if (changed > 0) {
            counter += Loc.F("panel.changedSuffix", changed);
        }

        return counter;
    }

    /// <summary>
    ///     选中帧的几何文案，形如 <c>X:24 Y:48 96×112</c>；没有选中帧时为空串。
    ///     <para>X / Y 可能为负，直接带负号显示（DNF 的帧常带负偏移）。</para>
    /// </summary>
    /// <param name="frame">当前显示的帧（多选时是最后点击的帧）。</param>
    public string SelectionGeometry(FrameViewModel? frame) => frame is { } f
        ? Loc.F("status.selectionGeometry", f.Frame.X, f.Frame.Y, f.Frame.Width, f.Frame.Height)
        : string.Empty;

    /// <summary>折叠态竖条上的计数（条目数 / 帧数共用）。</summary>
    /// <param name="count">集合长度。</param>
    public string PanelCount(int count) => count.ToString();

    /// <summary>
    ///     帧区空状态文案：区分"还没选条目 / 解析失败 / 版本不支持 / 这个 IMG 确实没有帧"。
    /// </summary>
    /// <param name="entry">当前选中条目；<c>null</c> 表示还没选。</param>
    /// <param name="error">最近一次帧解析失败的原因。</param>
    /// <param name="image">当前 IMG（用于判断版本支持等级）。</param>
    public string FrameArea(EntryItem? entry, string? error, ImgFile? image) {
        if (entry is null) return Loc["panel.selectImage"];

        // 解析异常优先：此时 CurrentImg 已被清空，拿不到版本号
        if (error is { } message) return Loc.F("status.frameParseFailed", message);

        if (image is { } img && VersionSupport.Of(img.Version) == VersionSupportLevel.Passthrough) {
            if (img.Version == ImgVersion.Other && OggAudioInfo.IsOgg(img.RawContent.Span)) {
                return Loc["panel.audioEntry"];   // 音效条目：中间的音频面板可以试听 / 导出 / 替换
            }

            return img.Version == ImgVersion.Other
                ? Loc["panel.unsupportedOther"]
                : Loc.F("panel.unsupportedVersion", (int)img.Version);
        }

        return Loc["panel.emptyFrames"];
    }
}
