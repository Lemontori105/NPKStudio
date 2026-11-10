using System.Collections.ObjectModel;

using Avalonia;

using NPKStudio.Core.Formats;
using NPKStudio.Core.Formats.Npk;
using NPKStudio.Core.Imaging;
using NPKStudio.Desktop.Controls;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.Services;
using NPKStudio.Desktop.ViewModels.Channels;

namespace NPKStudio.Desktop.ViewModels;

/// <summary>
///     主窗口的<b>外壳</b>：窗口级状态（缩放读数、标尺 / 参考线、坐标基准、栏位折叠）+ 通道集合。
///     <para>
///         所有与"某个打开的 IMG"绑定的状态（条目表、帧列表、选中、播放、可见性、变更标记、
///         画布包围盒、撤销栈、搜索过滤、状态栏文案）都在 <see cref="FrameChannel" /> 里，
///         每个通道一份；这里只把公开成员转发到 <see cref="ActiveChannel" />。
///     </para>
///     <para>
///         <b>为什么保留同名转发而不是让 XAML 直接绑通道</b>：桌面端 167 条测试与所有 <c>.axaml</c>
///         编译绑定都依赖 <see cref="MainWindowViewModel" /> 的成员名；转发层让"换成多通道"这件事
///         对界面与测试完全透明，也让将来多通道 UI 只需换绑 <see cref="ActiveChannel" />。
///     </para>
///     <para>
///         两种打开模式（NPK 包 / 源目录）由通道内的 <see cref="DocumentSession" /> 分流，外壳不感知。
///     </para>
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase {
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    /// <summary>切换活动通道后必须重新广播的转发属性（绑定要重新去新通道取值）。</summary>
    private static readonly string[] ForwardedProperties = [
        nameof(Entries), nameof(FilteredEntries), nameof(EntryFilter), nameof(Frames), nameof(Inspector),
        nameof(SelectedFrames), nameof(SelectedFrame), nameof(CurrentImage), nameof(CurrentImgFile),
        nameof(Archive), nameof(Folder), nameof(FileName), nameof(WindowTitle), nameof(Status),
        nameof(FrameCounter), nameof(SelectionGeometryText), nameof(LeftPanelCountText),
        nameof(RightPanelCountText), nameof(IsBusy), nameof(IsDirty), nameof(CanUndo), nameof(CanRedo),
        nameof(ChangedFrameCount), nameof(CanTrim), nameof(HasEntries), nameof(HasFrames),
        nameof(IsPassthroughVersion), nameof(IsExperimentalVersion), nameof(CanEditFrames),
        nameof(FrameAreaEmptyText), nameof(HasArchive), nameof(IsFolderMode), nameof(CanSave),
        nameof(ModeText), nameof(SaveTooltip), nameof(ManifestPath), nameof(EffectiveFps),
        nameof(RateOptions), nameof(SelectedRateText), nameof(LoopPlayback), nameof(AdvanceCount),
        nameof(CanvasSize), nameof(FrameOffset), nameof(CanvasOrigin), nameof(Backdrop),
        nameof(BackdropShortName), nameof(BackdropTooltip), nameof(Effect), nameof(EffectNames),
        nameof(SelectedEffectName), nameof(UseLinearDodge), nameof(RateIndex), nameof(IsPlaying),
        nameof(HiddenFrameCount), nameof(HasHiddenFrames), nameof(IsSelectedFrameHidden),
        nameof(CurrentImageKey), nameof(LastOpenSucceeded), nameof(EntryCountText), nameof(SelectedEntry),
        nameof(PendingFrameLoad), nameof(IsAudioEntry), nameof(CanReplaceAudio), nameof(AudioInfoText),
        nameof(HasUnsavedAudio)
    ];

    // 音效试听：全窗口同一时间只播一段（ffplay 子进程），不属于任何通道
    private readonly AudioTools _audio = new();
    private bool _isAudioPlaying;

    // 全局设置（`播放倍率` 与 `按 IMG 记忆的效果` 的落盘）、资源清单路径、文件工作区
    private readonly AppSettingsStore _settingsStore = AppSettingsStore.Load();
    private readonly PathLibrary _paths;
    private readonly FileWorkspace _workspace = new();

    private string _zoomText = Loc.F("status.zoom", 100);
    private string _mousePositionText = string.Empty;
    private bool _isRulersVisible = true;
    private bool _isGuidesVisible = true;
    private bool _isGuideLabelsVisible = true;

    // DNF 是像素艺术：默认开像素对齐（缩放吸附整数档 + 最近邻绘制）
    private bool _isPixelAlignEnabled = true;

    // 坐标基准：默认「图像」（0,0 = 画布左上角），符合 Photoshop 等工具的通用约定
    private RulerOriginMode _originMode = RulerOriginMode.Image;
    private int _guideCount;
    private bool _isLeftCollapsed;
    private bool _isRightCollapsed;

    /// <summary>实验性版本写回前的确认回调（窗口注入；每个新成为活动通道的通道都要拿到同一份）。</summary>
    private Func<ImgVersion, Task<bool>>? _confirmExperimentalSave;

    /// <summary>当前已挂上 <c>PropertyChanged</c> 监听的通道（切通道时先摘再挂，后台通道不再打扰外壳）。</summary>
    private FrameChannel? _subscribed;

    public MainWindowViewModel() {
        _paths = new PathLibrary(_settingsStore);
        Channels = new ChannelCollection(_settingsStore, _workspace, _paths);
        Channels.ActiveChannelChanged += (_, _) => OnActiveChannelChanged();
        Subscribe(Channels.ActiveChannel);
        // ffplay 播完 / 被停：进程事件在线程池上，切回 UI 线程再改绑定属性
        _audio.PlaybackEnded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => IsAudioPlaying = _audio.IsPlaying);
    }

    /// <summary>窗口持有的通道集合（当前只开一个，集合本身支持 N 个）。</summary>
    internal ChannelCollection Channels { get; }

    /// <summary>当前正在看的通道。</summary>
    internal FrameChannel ActiveChannel => Channels.ActiveChannel;

    /// <summary>程序集版本号，唯一来源是 csproj 的 <c>&lt;Version&gt;</c>。</summary>
    public static string AppVersion => AppInfo.Version;

    // ── 转发：文档 / 条目 ─────────────────────────────────────────────────────

    /// <summary>完整条目集合（不受搜索过滤影响）。列表绑定的是 <see cref="FilteredEntries" />。</summary>
    public ObservableCollection<EntryItem> Entries => ActiveChannel.Entries;

    /// <summary>左侧列表实际展示的条目（<see cref="Entries" /> 经 <see cref="EntryFilter" /> 过滤后的视图）。</summary>
    public ObservableCollection<EntryItem> FilteredEntries => ActiveChannel.FilteredEntries;

    /// <summary>搜索关键字：按条目路径做不区分大小写的包含匹配；空串表示全量。</summary>
    public string EntryFilter {
        get => ActiveChannel.EntryFilter;
        set => ActiveChannel.EntryFilter = value;
    }

    /// <summary>当前选中的条目。</summary>
    public EntryItem? SelectedEntry {
        get => ActiveChannel.SelectedEntry;
        set => ActiveChannel.SelectedEntry = value;
    }

    /// <summary>最近一次帧加载任务，便于调用方等待加载完成（例如"打开后立即导出"）。</summary>
    public Task? PendingFrameLoad => ActiveChannel.PendingFrameLoad;

    /// <summary>当前资源包（文件夹模式下为 <c>null</c>）。</summary>
    public NpkArchive? Archive => ActiveChannel.Archive;

    /// <summary>当前源目录（NPK 模式下为 <c>null</c>）。</summary>
    public FolderEntrySource? Folder => ActiveChannel.Folder;

    /// <summary>标题栏文件名（文件夹模式下为文件夹名）。</summary>
    public string FileName => ActiveChannel.FileName;

    /// <summary>
    ///     窗口标题：带上文件名与版本号。
    ///     <para>版本号唯一来源是 csproj 的 <c>&lt;Version&gt;</c>，避免"软件里显示的版本和发布包不一致"。</para>
    /// </summary>
    public string WindowTitle => string.IsNullOrEmpty(FileName)
        ? Loc.F("appTitle", AppVersion)
        : Loc.F("appTitleWithFile", FileName, AppVersion);

    // ── 转发：帧 ─────────────────────────────────────────────────────────────

    /// <summary>右侧帧卡片列表。</summary>
    public ObservableCollection<FrameViewModel> Frames => ActiveChannel.Frames;

    /// <summary>帧检查器（右栏第三段）：位置 / 尺寸的编辑与只读信息。</summary>
    internal FrameInspector Inspector => ActiveChannel.Inspector;

    /// <summary>帧网格的多选集合（双向绑定到 <c>ListBox.SelectedItems</c>，与控件共用同一个实例）。</summary>
    public ObservableCollection<FrameViewModel> SelectedFrames {
        get => ActiveChannel.SelectedFrames;
        set => ActiveChannel.SelectedFrames = value;
    }

    /// <summary>当前选中的帧。</summary>
    public FrameViewModel? SelectedFrame {
        get => ActiveChannel.SelectedFrame;
        set => ActiveChannel.SelectedFrame = value;
    }

    /// <summary>画布当前显示的帧像素。</summary>
    public RgbaImage? CurrentImage => ActiveChannel.View.CurrentImage;

    /// <summary>当前 IMG 的底层模型（测试与检查器校验用；界面一律走 <see cref="Frames" />）。</summary>
    internal ImgFile? CurrentImgFile => ActiveChannel.CurrentImgFile;

    /// <summary>画布坐标系尺寸 = 当前 IMG 所有帧的<b>联合包围盒</b>。</summary>
    public Size CanvasSize => ActiveChannel.View.CanvasSize;

    /// <summary>当前帧左上角在画布坐标系中的位置（= 帧坐标 - 包围盒左上角）。</summary>
    public Point FrameOffset => ActiveChannel.View.FrameOffset;

    /// <summary>画布坐标原点对应的角色坐标（标尺与状态栏显示角色坐标用）。</summary>
    public Point CanvasOrigin => ActiveChannel.View.CanvasOrigin;

    // ── 转发：状态栏 / 派生可用性 ─────────────────────────────────────────────

    /// <summary>状态栏文案。</summary>
    public string Status {
        get => ActiveChannel.Status;
        set => ActiveChannel.Status = value;
    }

    /// <summary>帧计数文案。</summary>
    public string FrameCounter => ActiveChannel.FrameCounter;

    /// <summary>状态栏的"选中帧几何"文案，形如 <c>X:24 Y:48 96×112</c>；没有选中帧时为空串。</summary>
    public string SelectionGeometryText => ActiveChannel.SelectionGeometryText;

    /// <summary>折叠态左竖条上的条目计数。</summary>
    public string LeftPanelCountText => ActiveChannel.LeftPanelCountText;

    /// <summary>折叠态右竖条上的帧计数。</summary>
    public string RightPanelCountText => ActiveChannel.RightPanelCountText;

    /// <summary>是否正在加载。</summary>
    public bool IsBusy => ActiveChannel.IsBusy;

    /// <summary>是否有未保存的修改（标题栏的 <c>●</c> 与关窗确认都用它）：帧编辑或音效替换。</summary>
    public bool IsDirty => ActiveChannel.IsDirty;

    // ── 音效条目 ───────────────────────────────────────────────────────────────

    /// <summary>当前条目是不是音效（.ogg）：是的话画布中间显示音频面板。</summary>
    public bool IsAudioEntry => ActiveChannel.IsAudioEntry;

    /// <summary>能否替换当前音效（NPK 模式）。</summary>
    public bool CanReplaceAudio => ActiveChannel.CanReplaceAudio;

    /// <summary>音频面板上的信息行。</summary>
    public string AudioInfoText => ActiveChannel.AudioInfoText;

    /// <summary>是否有替换过、还没保存的音效。</summary>
    public bool HasUnsavedAudio => ActiveChannel.HasUnsavedAudio;

    /// <summary>是否正在试听。</summary>
    public bool IsAudioPlaying {
        get => _isAudioPlaying;
        private set => SetProperty(ref _isAudioPlaying, value);
    }

    /// <summary>试听当前音效（先停掉正在播的）；失败时把原因写到状态栏。</summary>
    public bool PlayCurrentAudio() {
        if (!IsAudioEntry) return false;

        if (!_audio.Play(ActiveChannel.CurrentAudio, out var error, out var missing)) {
            IsAudioPlaying = false;
            Status = missing is not null ? MissingToolText(missing) : Loc.F("audio.playFailed", error);
            return false;
        }

        IsAudioPlaying = true;
        Status = Loc.F("audio.playing", SelectedEntry?.Name);
        return true;
    }

    /// <summary>停止试听。</summary>
    public void StopAudio() {
        _audio.Stop();
        IsAudioPlaying = false;
    }

    /// <summary>空格键 / 播放按钮：在播就停，没播就播。</summary>
    public void ToggleAudio() {
        if (_audio.IsPlaying) {
            StopAudio();
        } else {
            PlayCurrentAudio();
        }
    }

    /// <summary>
    ///     用一个音频文件替换当前音效：按原音效的采样率 / 声道转成 Ogg Vorbis（已是同参数的 Ogg Vorbis 则原样用），
    ///     保存时写进 NPK。
    /// </summary>
    public async Task ReplaceCurrentAudioAsync(string path) {
        if (!CanReplaceAudio) return;

        StopAudio();
        var channel = ActiveChannel;
        var entry = SelectedEntry;
        var entryName = entry?.Name;
        var original = channel.CurrentAudioInfo;
        channel.IsBusy = true;
        Status = Loc["audio.converting"];
        try {
            var result = await _audio.ToOggVorbisAsync(path, original?.SampleRate ?? 0, original?.Channels ?? 0);
            if (result.Ogg is null) {
                Status = result.MissingTool is { } missing ? MissingToolText(missing) : Loc.F("audio.convertFailed", result.Error);
                return;
            }

            // 转换期间用户可能切走了条目：只替换发起时的那一条
            if (!ReferenceEquals(channel, ActiveChannel) || !ReferenceEquals(SelectedEntry, entry) || !channel.CanReplaceAudio) {
                Status = Loc["audio.replaceCancelled"];
                return;
            }

            channel.ReplaceCurrentAudio(result.Ogg);
            Status = Loc.F(result.Reencoded ? "audio.replacedReencoded" : "audio.replacedAsIs", entryName);
        } catch (Exception ex) {
            Status = Loc.F("audio.convertFailed", ex.Message);
        } finally {
            channel.IsBusy = false;
        }
    }

    /// <summary>导出当前音效，或包里全部音效（按包内路径建子目录）。</summary>
    public async Task ExportAudioAsync(string folder, bool currentOnly) {
        var channel = ActiveChannel;
        channel.IsBusy = true;
        Status = Loc["audio.exporting"];
        try {
            var count = await channel.ExportAudioAsync(folder, currentOnly);
            Status = Loc.F("audio.exported", count, folder);
        } catch (Exception ex) {
            Status = Loc.F("audio.exportFailed", ex.Message);
        } finally {
            channel.IsBusy = false;
        }
    }

    /// <summary>把当前音效原样写到一个文件（替换过还没保存的就是替换后的内容）。</summary>
    public async Task ExportCurrentAudioToFileAsync(string path) {
        var bytes = ActiveChannel.CurrentAudio.ToArray();
        if (bytes.Length == 0) return;

        try {
            await File.WriteAllBytesAsync(path, bytes);
            Status = Loc.F("audio.exportedOne", path);
        } catch (Exception ex) {
            Status = Loc.F("audio.exportFailed", ex.Message);
        }
    }

    /// <summary>缺工具时的提示：ffplay / ffmpeg 来自 Homebrew 的 ffmpeg，oggenc 来自 vorbis-tools。</summary>
    private static string MissingToolText(string tool) =>
        Loc.F("audio.missingTool", tool, tool == "oggenc" ? "vorbis-tools" : "ffmpeg");

    /// <summary>是否可撤销（绑定工具栏按钮）。</summary>
    public bool CanUndo => ActiveChannel.Editor.CanUndo;

    /// <summary>是否可重做（绑定工具栏按钮）。</summary>
    public bool CanRedo => ActiveChannel.Editor.CanRedo;

    /// <summary>有变更标记的帧数（纯提示：不写文件、不进撤销栈；保存后归零）。</summary>
    public int ChangedFrameCount => ActiveChannel.Editor.ChangedFrameCount;

    /// <summary>能否裁剪当前帧：有选中帧、不是 LINK、且版本可写。</summary>
    public bool CanTrim => ActiveChannel.CanTrim;

    /// <summary>文件列表是否非空。</summary>
    public bool HasEntries => ActiveChannel.HasEntries;

    /// <summary>帧列表是否非空。</summary>
    public bool HasFrames => ActiveChannel.HasFrames;

    /// <summary>当前 IMG 是否"仅透传"（v7/v8/v9 与非 IMG 条目）。</summary>
    public bool IsPassthroughVersion => ActiveChannel.IsPassthroughVersion;

    /// <summary>当前 IMG 是否"实验性"（v4/v5/v6）：能读写，但写回前要如实提示风险。</summary>
    public bool IsExperimentalVersion => ActiveChannel.IsExperimentalVersion;

    /// <summary>帧编辑类操作（替换 / 裁剪 / 导出 GIF）是否可用。</summary>
    public bool CanEditFrames => ActiveChannel.CanEditFrames;

    /// <summary>帧区空状态文案：区分"还没选条目 / 解析失败 / 版本不支持 / 确实没有帧"。</summary>
    public string FrameAreaEmptyText => ActiveChannel.FrameAreaEmptyText;

    /// <summary>状态栏的 IMG 计数文案；筛选时同时给出总量与命中数。</summary>
    public string EntryCountText => ActiveChannel.EntryCountText;

    /// <summary>是否已打开 NPK 包。</summary>
    public bool HasArchive => ActiveChannel.HasArchive;

    /// <summary>是否以"源目录"模式打开。</summary>
    public bool IsFolderMode => ActiveChannel.IsFolderMode;

    /// <summary>是否存在可保存的内容（NPK 包或源目录均算）。</summary>
    public bool CanSave => ActiveChannel.CanSave;

    /// <summary>状态栏右侧的模式标识。</summary>
    public string ModeText => ActiveChannel.ModeText;

    /// <summary>保存按钮的提示语随模式变化：文件夹模式是"写回 .img"，NPK 模式是"另存为 NPK"。</summary>
    public string SaveTooltip => ActiveChannel.SaveTooltip;

    /// <summary>最近一次打开是否成功（供自动化脚本判断退出码；GUI 不依赖它）。</summary>
    public bool LastOpenSucceeded => ActiveChannel.Document.LastOpenSucceeded;

    /// <summary>当前 IMG 的记忆键：文件夹模式用「文件夹名/条目路径」，NPK 模式用「包名/条目路径」。</summary>
    public string? CurrentImageKey => ActiveChannel.Document.CurrentImageKey;

    /// <summary>
    ///     实验性版本写回前的确认回调；由主窗口注入。
    ///     <para><b>必须允许为 <c>null</c></b>：无头测试宿主与自动化模式不弹窗（会卡死），直接走默认"继续"。</para>
    /// </summary>
    internal Func<ImgVersion, Task<bool>>? ConfirmExperimentalSave {
        get => _confirmExperimentalSave;
        set {
            _confirmExperimentalSave = value;
            ActiveChannel.ConfirmExperimentalSave = value;
        }
    }

    // ── 转发：播放 ───────────────────────────────────────────────────────────

    /// <summary>当前倍率对应的实际帧率。</summary>
    public int EffectiveFps => ActiveChannel.EffectiveFps;

    /// <summary>下拉选项：倍率 + 对应 fps（用户对着 ani 的 [DELAY] 调时不用自己换算）。</summary>
    public IReadOnlyList<string> RateOptions => ActiveChannel.RateOptions;

    /// <summary>下拉当前项（ComboBox 直接绑字符串）。</summary>
    public string SelectedRateText {
        get => ActiveChannel.SelectedRateText;
        set => ActiveChannel.SelectedRateText = value;
    }

    /// <summary>是否循环播放。</summary>
    public bool LoopPlayback {
        get => ActiveChannel.LoopPlayback;
        set => ActiveChannel.LoopPlayback = value;
    }

    /// <summary>播放推进次数（自动化统计实际 fps 用）。</summary>
    public int AdvanceCount {
        get => ActiveChannel.AdvanceCount;
        set => ActiveChannel.AdvanceCount = value;
    }

    /// <summary>播放倍率下标（0~8）。改值会重建定时器并落盘。</summary>
    public int RateIndex {
        get => ActiveChannel.RateIndex;
        set => ActiveChannel.RateIndex = value;
    }

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying => ActiveChannel.IsPlaying;

    /// <summary>播放 / 暂停。</summary>
    public void TogglePlay() => ActiveChannel.TogglePlay();

    /// <summary>停止播放（切换 IMG / 另存前必调）。</summary>
    public void Stop() => ActiveChannel.Stop();

    /// <summary>推进一拍（测试 / 自动化直接驱动，不必等真实定时器）。</summary>
    internal void AdvanceFrame() => ActiveChannel.AdvanceFrame();

    /// <summary>手动步进（正数向后、负数向前），到两端回绕。</summary>
    public void StepFrame(int delta) => ActiveChannel.StepFrame(delta);

    // ── 转发：画布观感 ───────────────────────────────────────────────────────

    /// <summary>画布背景档位（只影响显示）。</summary>
    public CanvasBackground Backdrop {
        get => ActiveChannel.Backdrop;
        set => ActiveChannel.Backdrop = value;
    }

    /// <summary>背景档位短名。</summary>
    public string BackdropShortName => ActiveChannel.BackdropShortName;

    /// <summary>背景档位完整名（提示语）。</summary>
    public string BackdropTooltip => ActiveChannel.BackdropTooltip;

    /// <summary>画布效果（改动只影响画布显示，并按 IMG 记忆）。</summary>
    public CanvasEffect Effect {
        get => ActiveChannel.Effect;
        set => ActiveChannel.Effect = value;
    }

    /// <summary>效果下拉选项。</summary>
    public IReadOnlyList<string> EffectNames => ActiveChannel.EffectNames;

    /// <summary>效果下拉当前项。</summary>
    public string SelectedEffectName {
        get => ActiveChannel.SelectedEffectName;
        set => ActiveChannel.SelectedEffectName = value;
    }

    /// <summary>是否走线性减淡。</summary>
    public bool UseLinearDodge => ActiveChannel.UseLinearDodge;

    /// <summary>逐字节复刻 pvfUtility 的 <c>LinearBrun</c>（实现见 <see cref="FrameChannel.CreateLinearDodge" />）。</summary>
    public static RgbaImage CreateLinearDodge(RgbaImage source) => ChannelViewState.CreateLinearDodge(source);

    /// <summary>
    ///     语言切换后调用：清掉本地化缓存并通知绑定刷新。
    ///     <para>
    ///         设置窗口切换语言时由 <see cref="OnLanguageChanged" /> 调用。先发"选项列表变了"再发"选中项变了"，
    ///         下拉框换完列表才能按新文字选回原来那一项。
    ///     </para>
    /// </summary>
    public void RefreshLocalizedOptions() {
        foreach (var channel in Channels.Channels) {
            channel.InvalidateLocalizedOptions();
        }

        OnPropertyChanged(nameof(EffectNames));
        OnPropertyChanged(nameof(RateOptions));
        OnPropertyChanged(nameof(SelectedEffectName));
        OnPropertyChanged(nameof(SelectedRateText));
        OnPropertyChanged(nameof(OriginModeText));
        OnPropertyChanged(nameof(OriginModeTip));
    }

    /// <summary>设置里保存的界面语言（<c>null</c> = 跟随系统）。</summary>
    public string? SavedLanguage => _settingsStore.Language;

    /// <summary>工具栏上的设置按钮只在非 macOS 显示：macOS 的设置入口在应用菜单「设置… ⌘,」（苹果的习惯）。</summary>
    public bool ShowSettingsButton => !OperatingSystem.IsMacOS();

    /// <summary>
    ///     设置窗口里切换语言：保存选择（<c>null</c> = 跟随系统），立刻切换词表。
    ///     <para>
    ///         XAML 里 {loc:Tr} 的绑定自己会刷新；代码拼出来的文案由 <see cref="LanguageRefresh" />
    ///         通知所有 ViewModel 重算（本类见 <see cref="OnLanguageChanged" />）。
    ///     </para>
    /// </summary>
    public void ApplyLanguagePreference(string? language) {
        _settingsStore.Language = language;
        _settingsStore.Save();
        TranslationCatalog.Instance.SetLanguage(language ?? UiLanguage.SystemLanguage());
    }

    /// <inheritdoc />
    protected override void OnLanguageChanged() => RefreshLocalizedOptions();

    /// <summary>立即把按 IMG 记忆的设置写盘（关窗口时调用，避免防抖定时器还没触发就退出）。</summary>
    public void FlushSettings() {
        foreach (var channel in Channels.Channels) {
            channel.Flush();
        }
    }

    // ── 转发：文件 / 帧编辑命令 ───────────────────────────────────────────────

    /// <summary>打开文件或文件夹：目录走 <see cref="FolderEntrySource" />，其余按 NPK / IMG 处理。</summary>
    public Task OpenAsync(string path) => ActiveChannel.Document.OpenAsync(path);

    /// <summary>打开一个"NPK 源目录"（与 <see cref="OpenAsync" /> 分流）。</summary>
    public Task OpenFolderAsync(string path) => ActiveChannel.Document.OpenFolderAsync(path);

    /// <summary>导出当前帧为 PNG（永远导出原始解码结果）。</summary>
    public void ExportCurrentPng(string path) => ActiveChannel.Document.ExportCurrentPng(path);

    /// <summary>保存：NPK 模式另存；文件夹模式原子写回原 .img。</summary>
    public Task SaveAsync(string? path = null) => ActiveChannel.Document.SaveAsync(path);

    /// <summary>把当前包另存到指定路径（未修改条目按原始字节直拷）。</summary>
    public Task SaveArchiveAsync(string path) => ActiveChannel.Document.SaveArchiveAsync(path);

    /// <summary>把"NPK 源目录 / 单个 img"打包成一个 NPK。</summary>
    public Task ExportFolderToNpkAsync(FolderEntrySource folder, string? manifestPath, string outputPath) =>
        ActiveChannel.Document.ExportFolderToNpkAsync(folder, manifestPath, outputPath);

    /// <summary>已记住的资源清单路径；<c>null</c> 表示还没设置过。</summary>
    public string? ManifestPath => ActiveChannel.Document.ManifestPath;

    /// <summary>记住（或清除）资源清单，立即落盘。</summary>
    public string SetManifest(string? path) => ActiveChannel.Document.SetManifest(path);

    /// <summary>扫描一个装满 NPK 的目录，反向生成资源清单（清单的标准来源）。</summary>
    public Task GenerateManifestAsync(string npkDirectory, string outputPath) =>
        ActiveChannel.Document.GenerateManifestAsync(npkDirectory, outputPath);

    /// <summary>用一张图片替换当前选中的帧（换图后刷新缩略图与画布）。</summary>
    public Task ReplaceSelectedFrameAsync(string imagePath) => ActiveChannel.Editor.ReplaceSelectedAsync(imagePath);

    /// <summary>在当前帧之后插入一帧 1×1 全透明帧，并选中它。</summary>
    public void InsertFrameAfterSelected() => ActiveChannel.Editor.InsertAfterSelected();

    /// <summary>删除当前选区（多选时全部删除），删完落回原位置那一帧。</summary>
    public void DeleteSelectedFrames() => ActiveChannel.Editor.DeleteSelected();

    /// <summary>拖拽排序：把一帧放到"插入缝"上（与上移 / 下移同一条模型路径）。</summary>
    public void MoveFrameTo(FrameViewModel? frame, int insertIndex) => ActiveChannel.Editor.MoveTo(frame, insertIndex);

    /// <summary>把当前帧在 IMG 里前后挪动 <paramref name="delta" /> 位（越界不动）。</summary>
    public void MoveSelectedFrame(int delta) => ActiveChannel.Editor.MoveSelected(delta);

    /// <summary>裁剪选区（多选时逐帧处理）：裁剪算<b>一次</b>撤销单元。</summary>
    public void TrimSelectedFrames() => ActiveChannel.Editor.TrimSelected();

    /// <summary>撤销一步。</summary>
    public void UndoEdit() => ActiveChannel.Editor.Undo();

    /// <summary>重做一步。</summary>
    public void RedoEdit() => ActiveChannel.Editor.Redo();

    /// <summary>批量导出 PNG 到指定目录（每帧一个文件，命名 <c>{IMG名}_{序号:D3}.png</c>）。</summary>
    public void ExportPngBatch(string directory, IReadOnlyList<FrameViewModel> frames) =>
        ActiveChannel.Editor.ExportPngBatch(directory, frames);

    /// <summary>把当前 IMG 编码成动画 GIF（多选时只导出选中帧，否则导出全部帧）。</summary>
    public Task ExportGifAsync(string path) => ActiveChannel.Editor.ExportGifAsync(path);

    /// <summary>重算帧级变更标记并刷新「改 N」计数（测试 / 自动化用）。</summary>
    internal void RefreshChangeMarkers() => ActiveChannel.Editor.RefreshChangeMarkers();

    /// <summary>按 <see cref="EntryFilter" /> 重建过滤视图（内部可见性供测试直接触发，跳过 150ms 防抖）。</summary>
    internal void ApplyFilter() => ActiveChannel.ApplyFilter();

    // ── 转发：可见性 ─────────────────────────────────────────────────────────

    /// <summary>当前被隐藏（播放时跳过）的帧数。</summary>
    public int HiddenFrameCount => ActiveChannel.View.HiddenFrameCount;

    /// <summary>是否存在隐藏帧：右键菜单「全部显示」的启停条件。</summary>
    public bool HasHiddenFrames => ActiveChannel.View.HasHiddenFrames;

    /// <summary>当前帧是否已隐藏（画布仍显示它，只是播放会跳过）。</summary>
    public bool IsSelectedFrameHidden => ActiveChannel.View.IsSelectedFrameHidden;

    /// <summary>隐藏选区。纯视图状态：不置脏、不进撤销栈。</summary>
    public void HideSelectedFrames() => ActiveChannel.View.HideSelected();

    /// <summary>把选区重新设为可见。</summary>
    public void ShowSelectedFrames() => ActiveChannel.View.ShowSelected();

    /// <summary>把整个 IMG 的帧全部设为可见。</summary>
    public void ShowAllFrames() => ActiveChannel.View.ShowAll();

    /// <summary>快捷键 <c>H</c>：切换选区的可见性。</summary>
    public void ToggleSelectedFramesVisibility() => ActiveChannel.View.ToggleSelected();

    /// <summary>自动化截图用：外部直接改过卡片可见性之后重算计数。</summary>
    internal void RefreshFrameVisibility() => ActiveChannel.View.RefreshVisibility();

    // ── 窗口级视图状态（不属于任何通道） ─────────────────────────────────────

    /// <summary>
    ///     状态栏的画布缩放文案，形如 <c>128%</c>。
    ///     <para>缩放状态属于 <see cref="Controls.ImageCanvas" />，这里只是给它一个可绑定的展示位。</para>
    /// </summary>
    public string ZoomText {
        get => _zoomText;
        internal set => SetProperty(ref _zoomText, value);
    }

    /// <summary>状态栏的光标坐标文案，形如 <c>X:24 Y:48</c>；鼠标离开画布后清空。</summary>
    public string MousePositionText {
        get => _mousePositionText;
        internal set => SetProperty(ref _mousePositionText, value);
    }

    /// <summary>画布标尺是否显示（快捷键 ⌘R，Windows 为 Ctrl+R；默认显示）。</summary>
    public bool IsRulersVisible {
        get => _isRulersVisible;
        set => SetProperty(ref _isRulersVisible, value);
    }

    /// <summary>画布参考线是否显示（快捷键 ⌘;，Windows 为 Ctrl+;；隐藏时数据保留）。</summary>
    public bool IsGuidesVisible {
        get => _isGuidesVisible;
        set => SetProperty(ref _isGuidesVisible, value);
    }

    /// <summary>参考线边缘的坐标标签是否显示（默认显示；关掉后只画线不画数字）。</summary>
    public bool IsGuideLabelsVisible {
        get => _isGuideLabelsVisible;
        set => SetProperty(ref _isGuideLabelsVisible, value);
    }

    /// <summary>像素对齐（默认开启）：缩放只落在整数倍 / 整数分之一档位，画布主图按最近邻绘制。</summary>
    public bool IsPixelAlignEnabled {
        get => _isPixelAlignEnabled;
        set => SetProperty(ref _isPixelAlignEnabled, value);
    }

    /// <summary>
    ///     标尺 / 游标 / 参考线标签的坐标基准（默认图像坐标：0,0 = 画布左上角）。
    ///     <para>只改"读数"。参考线仍按角色坐标存储，检查器的 X/Y 永远保持角色坐标。</para>
    /// </summary>
    public RulerOriginMode OriginMode {
        get => _originMode;
        set {
            if (_originMode == value) return;
            _originMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsImageOrigin));
            OnPropertyChanged(nameof(IsRoleOrigin));
            OnPropertyChanged(nameof(OriginModeText));
            OnPropertyChanged(nameof(OriginModeTip));
        }
    }

    /// <summary>右键菜单「图像坐标」勾选态（与 <see cref="OriginMode" /> 互斥）。</summary>
    public bool IsImageOrigin {
        get => _originMode == RulerOriginMode.Image;
        set {
            if (value) OriginMode = RulerOriginMode.Image;
        }
    }

    /// <summary>右键菜单「角色坐标」勾选态（与 <see cref="OriginMode" /> 互斥）。</summary>
    public bool IsRoleOrigin {
        get => _originMode == RulerOriginMode.Role;
        set {
            if (value) OriginMode = RulerOriginMode.Role;
        }
    }

    /// <summary>状态栏的极短基准标识，形如 <c>基准:图像</c>。</summary>
    public string OriginModeText => Loc.F("status.origin",
        Loc[_originMode == RulerOriginMode.Image ? "status.origin.image" : "status.origin.role"]);

    /// <summary>标尺交叉角 / 基准标识的提示文案。</summary>
    public string OriginModeTip => Loc.F("canvas.origin.tip",
        Loc[_originMode == RulerOriginMode.Image ? "canvas.origin.image" : "canvas.origin.role"]);

    /// <summary>画布上参考线的总条数（画布是唯一真相源，由 <c>MainWindow</c> 推进来）。</summary>
    public int GuideCount {
        get => _guideCount;
        internal set {
            if (_guideCount == value) return;
            _guideCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasGuides));
        }
    }

    /// <summary>是否已有参考线：右键菜单「清除参考线」的启停条件。</summary>
    public bool HasGuides => _guideCount > 0;

    /// <summary>左栏是否已折叠为 40px 竖条（列宽由 code-behind 负责）。</summary>
    public bool IsLeftCollapsed {
        get => _isLeftCollapsed;
        internal set => SetProperty(ref _isLeftCollapsed, value);
    }

    /// <summary>右栏是否已折叠为 40px 竖条。</summary>
    public bool IsRightCollapsed {
        get => _isRightCollapsed;
        internal set => SetProperty(ref _isRightCollapsed, value);
    }

    // ── 通道切换 ─────────────────────────────────────────────────────────────

    private void OnActiveChannelChanged() {
        Subscribe(ActiveChannel);

        // 转发属性必须整体重新广播：绑定要重新去新通道取值，否则界面会停在旧通道的数据上
        foreach (var name in ForwardedProperties) {
            OnPropertyChanged(name);
        }
    }

    /// <summary>挂上/摘下当前活动通道的通知转发（同一个属性永远只有一处 raise）。</summary>
    private void Subscribe(FrameChannel channel) {
        if (_subscribed is not null) {
            _subscribed.PropertyChanged -= OnChannelPropertyChanged;
        }

        _subscribed = channel;
        channel.PropertyChanged += OnChannelPropertyChanged;
        channel.ConfirmExperimentalSave = _confirmExperimentalSave;
    }

    /// <summary>
    ///     活动通道的广播：属性名与外壳的转发属性一致，原样转发即可让 XAML 绑定自动刷新。
    ///     <para><see cref="FileName" /> 是唯一需要补一条的：标题栏由外壳拼装。</para>
    /// </summary>
    private void OnChannelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (e.PropertyName is not { } name) {
            OnPropertyChanged();
            return;
        }

        OnPropertyChanged(name);

        if (name == nameof(FileName)) {
            OnPropertyChanged(nameof(WindowTitle));
        }

        // 换了条目就停掉上一段试听：否则听到的不是当前选中的音效
        if (name == nameof(SelectedEntry) && _audio.IsPlaying) {
            StopAudio();
        }
    }

    /// <summary>窗口关闭时停掉试听进程、清掉临时文件。</summary>
    public void ShutdownAudio() => _audio.Dispose();
}
