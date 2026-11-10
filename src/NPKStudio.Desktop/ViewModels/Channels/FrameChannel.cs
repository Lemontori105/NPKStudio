using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

using Avalonia;

using NPKStudio.Core.Audio;
using NPKStudio.Core.Formats;
using NPKStudio.Core.Formats.Npk;
using NPKStudio.Core.Imaging;
using NPKStudio.Desktop.Controls;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.Services;

namespace NPKStudio.Desktop.ViewModels.Channels;

/// <summary>
///     一个通道的<b>聚合根</b>：通道身份（会话 / 条目表 / 文件名）、帧集合、选中、状态栏文案，
///     以及四个协作者的组装。
///     <para>
///         具体职责按"改什么"分给 <c>internal sealed</c> 协作者：<see cref="Document" />（打开 / 加载 /
///         保存 / 打包 / 清单）、<see cref="Editor" />（帧结构编辑 + 撤销栈 + 变更标记）、
///         <see cref="View" />（可见性 / 过滤 / 当前图像 / 包围盒 / 检查器）、<see cref="StatusText" />（状态栏纯文案）。
///     </para>
///     <para>
///         <b>协作者之间不通信</b>：没有事件、没有委托字段。要回写状态就写 <see cref="Status" />、
///         或调 <see cref="Notify" /> —— 都是 sealed 类型上的非虚调用（JIT 可去虚拟化并内联），
///         所以拆分本身零运行时开销。
///     </para>
///     <para>
///         <b>没有静态成员、也不互相引用别的通道</b>：两个通道打开两个 IMG 时，帧数、播放位置、
///         隐藏了哪几帧、改了几帧彼此完全无关。
///     </para>
/// </summary>
internal sealed class FrameChannel : ViewModelBase
{
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    private readonly DocumentSession _session = new();
    private readonly FrameCardPresenter _cards;
    private readonly FrameEditPresenter _editor;
    private readonly EntryFilterPresenter _entryFilter;
    private readonly CanvasAppearance _appearance;
    private readonly ChannelPlayback _playback;
    private readonly FrameChangeTracker _changes = new();

    private EntryItem? _selectedEntry;
    private FrameViewModel? _selectedFrame;
    private ObservableCollection<FrameViewModel> _selectedFrames = new();
    private string _fileName = "NPKStudio";
    private string _status = Loc["status.ready"];
    private string _frameCounter = "0 / 0";
    private string _selectionGeometryText = string.Empty;
    private bool _isBusy;
    private bool _bulkLoading;

    public FrameChannel(AppSettingsStore settings, FileWorkspace workspace, PathLibrary paths) {
        _appearance = new CanvasAppearance(settings);
        _playback = new ChannelPlayback(settings, Frames);
        _playback.CurrentFrame = () => _selectedFrame;
        _playback.ShowFrame = frame => SelectedFrame = frame;
        _playback.AllFramesHidden += (_, _) => Status = Loc["status.playbackAllHidden"];
        _playback.PropertyChanged += OnPresenterPropertyChanged;

        // 检查器只拿会话，不反向持有通道；它用属性通知广播"状态文案 / 几何变了"
        Inspector = new FrameInspector(_session);
        Inspector.PropertyChanged += OnInspectorPropertyChanged;
        _cards = new FrameCardPresenter(Frames, _selectedFrames, Inspector);
        _editor = new FrameEditPresenter(_session, workspace, _cards, Frames, _changes);
        _editor.PropertyChanged += OnPresenterPropertyChanged;

        _entryFilter = new EntryFilterPresenter(_session.Entries);
        _entryFilter.FilterSettled += (_, _) => ApplyFilter();
        _entryFilter.PropertyChanged += OnEntryFilterPropertyChanged;

        // 协作者构造顺序：StatusText / Document 无依赖，View 无依赖，Editor 需要 View（结构变了要重算包围盒）
        StatusText = new ChannelStatusText();
        Document = new ChannelDocument(this, _session, workspace, paths);
        View = new ChannelViewState(this, _session, _cards);
        Editor = new ChannelFrameEditor(this, View, _session, _editor, _cards, Frames, _changes);

        _selectedFrames.CollectionChanged += OnSelectedFramesChanged;

        Entries.CollectionChanged += (_, _) => {
            if (_bulkLoading) return;
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(EntryCountText));
            OnPropertyChanged(nameof(LeftPanelCountText));
        };
        Frames.CollectionChanged += (_, _) => {
            OnPropertyChanged(nameof(HasFrames));
            OnPropertyChanged(nameof(CanTrim));
            OnPropertyChanged(nameof(CanEditFrames));
            OnPropertyChanged(nameof(FrameAreaEmptyText));
            OnPropertyChanged(nameof(RightPanelCountText));
            OnPropertyChanged(nameof(FilteredFrames));
            OnPropertyChanged(nameof(CurrentIndex));
            // 删掉 / 重建卡片会改变隐藏帧数量（卡片对象按引用复用，隐藏状态本身保留）
            OnPropertyChanged(nameof(HiddenFrameCount));
            OnPropertyChanged(nameof(HasHiddenFrames));
            UpdateFrameCounter();
        };
    }

    /// <summary>帧结构编辑（插入 / 删除 / 移动 / 裁剪 / 换图 / 导出 + 撤销栈 + 变更标记）。</summary>
    public ChannelFrameEditor Editor { get; }

    /// <summary>文档生命周期（打开 / 加载 / 保存 / 打包 / 资源清单）。</summary>
    public ChannelDocument Document { get; }

    /// <summary>视图状态（可见性 / 搜索过滤 / 当前图像 / 包围盒几何 / 检查器快照）。</summary>
    public ChannelViewState View { get; }

    /// <summary>状态栏纯文案组合。</summary>
    public ChannelStatusText StatusText { get; }

    /// <summary>右侧帧卡片列表。</summary>
    public ObservableCollection<FrameViewModel> Frames { get; } = new();

    /// <summary>帧检查器（右栏第三段）：位置 / 尺寸的编辑与只读信息。</summary>
    internal FrameInspector Inspector { get; }

    /// <summary>完整条目集合（不受搜索过滤影响）。列表绑定的是 <see cref="FilteredEntries" />。</summary>
    public ObservableCollection<EntryItem> Entries => _session.Entries;

    /// <summary>左侧列表实际展示的条目（<see cref="Entries" /> 经 <see cref="EntryFilter" /> 过滤后的视图）。</summary>
    public ObservableCollection<EntryItem> FilteredEntries => _entryFilter.Filtered;

    /// <summary>搜索关键字（按条目路径做不区分大小写的包含匹配）；空串表示全量。</summary>
    public string EntryFilter {
        get => _entryFilter.Filter;
        set => _entryFilter.Filter = value ?? string.Empty;
    }

    /// <summary>
    ///     帧网格的多选集合（双向绑定到 <c>ListBox.SelectedItems</c>，与控件共用同一个实例）。
    ///     <para>控件可能把自己的集合推上来，所以 setter 要换实例并重新挂事件。</para>
    /// </summary>
    public ObservableCollection<FrameViewModel> SelectedFrames {
        get => _selectedFrames;
        set {
            if (ReferenceEquals(_selectedFrames, value)) return;

            _selectedFrames.CollectionChanged -= OnSelectedFramesChanged;
            _selectedFrames = value ?? new ObservableCollection<FrameViewModel>();
            _selectedFrames.CollectionChanged += OnSelectedFramesChanged;
            _cards.Selection = _selectedFrames;
            OnPropertyChanged();
            View.SyncInspector();
        }
    }

    /// <summary>当前 IMG 的底层模型（测试与检查器校验用；界面一律走 <see cref="Frames" />）。</summary>
    internal ImgFile? CurrentImgFile => _session.CurrentImg;

    /// <summary>当前资源包（文件夹模式下为 <c>null</c>）。</summary>
    public NpkArchive? Archive => _session.Archive;

    /// <summary>当前源目录（NPK 模式下为 <c>null</c>）。</summary>
    public FolderEntrySource? Folder => _session.Folder;

    /// <summary>标题栏文件名（文件夹模式下为文件夹名）。</summary>
    public string FileName {
        get => _fileName;
        internal set => SetProperty(ref _fileName, value);
    }

    /// <summary>状态栏文案（协作者也直接写这里）。</summary>
    public string Status {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>帧计数文案。</summary>
    public string FrameCounter {
        get => _frameCounter;
        private set => SetProperty(ref _frameCounter, value);
    }

    /// <summary>状态栏的"选中帧几何"文案；没有选中帧时为空串。</summary>
    public string SelectionGeometryText {
        get => _selectionGeometryText;
        private set => SetProperty(ref _selectionGeometryText, value);
    }

    /// <summary>折叠态左竖条上的条目计数。</summary>
    public string LeftPanelCountText => StatusText.PanelCount(Entries.Count);

    /// <summary>折叠态右竖条上的帧计数。</summary>
    public string RightPanelCountText => StatusText.PanelCount(Frames.Count);

    /// <summary>是否正在加载（本通道）。</summary>
    public bool IsBusy {
        get => _isBusy;
        internal set => SetProperty(ref _isBusy, value);
    }

    /// <summary>能否裁剪当前帧：有选中帧、不是 LINK（LINK 帧没有自己的像素）、且版本可写。</summary>
    public bool CanTrim => _selectedFrame is { Frame.IsLink: false } && !IsPassthroughVersion;

    /// <summary>文件列表是否非空。</summary>
    public bool HasEntries => Entries.Count > 0;

    /// <summary>帧列表是否非空。</summary>
    public bool HasFrames => Frames.Count > 0;

    /// <summary>当前 IMG 是否"仅透传"（v7/v8/v9 与非 IMG 条目）。</summary>
    public bool IsPassthroughVersion => _session.CurrentImg is { } img
                                        && VersionSupport.Of(img.Version) == VersionSupportLevel.Passthrough;

    /// <summary>当前 IMG 是否"实验性"（v4/v5/v6）：能读写，但写回前要如实提示风险。</summary>
    public bool IsExperimentalVersion => _session.CurrentImg is { } img
                                         && VersionSupport.Of(img.Version) == VersionSupportLevel.Experimental;

    /// <summary>帧编辑类操作（替换 / 裁剪 / 导出 GIF）是否可用：有帧且不是仅透传版本。</summary>
    public bool CanEditFrames => HasFrames && !IsPassthroughVersion;

    /// <summary>帧区空状态文案：区分"还没选条目 / 解析失败 / 版本不支持 / 这个 IMG 确实没有帧"。</summary>
    public string FrameAreaEmptyText =>
        StatusText.FrameArea(_selectedEntry, Document.FrameLoadError, _session.CurrentImg);

    /// <summary>
    ///     实验性版本写回前的确认回调；由主窗口注入。
    ///     <para><b>必须允许为 <c>null</c></b>：无头测试宿主与自动化模式不弹窗（会卡死），直接走默认"继续"。</para>
    /// </summary>
    internal Func<ImgVersion, Task<bool>>? ConfirmExperimentalSave { get; set; }

    /// <summary>是否已打开 NPK 包。</summary>
    public bool HasArchive => _session.Archive is not null;

    /// <summary>是否以"源目录"模式打开。</summary>
    public bool IsFolderMode => _session.Folder is not null;

    /// <summary>是否存在可保存的内容（NPK 包或源目录均算）。</summary>
    public bool CanSave => _session.Archive is not null || _session.Folder is not null;

    /// <summary>状态栏右侧的模式标识。</summary>
    public string ModeText =>
        _session.Folder is not null ? Loc["mode.folder"] :
        _session.Archive is not null ? Loc["mode.npk"] : "—";

    /// <summary>保存按钮的提示语随模式变化：文件夹模式是"写回 .img"，NPK 模式是"另存为 NPK"。</summary>
    public string SaveTooltip => IsFolderMode ? Loc["tip.writeBack"] : Loc["tip.save"];

    /// <summary>状态栏的 IMG 计数文案；筛选时同时给出总量与命中数。</summary>
    public string EntryCountText => _entryFilter.CountText;

    // ── 播放 ───────────────────────────────────────────────────────────────────

    /// <summary>当前倍率对应的实际帧率。</summary>
    public int EffectiveFps => _playback.EffectiveFps;

    /// <summary>下拉选项：倍率 + 对应 fps（用户对着 ani 的 [DELAY] 调时不用自己换算）。</summary>
    public IReadOnlyList<string> RateOptions => _playback.RateOptions;

    /// <summary>下拉当前项（ComboBox 直接绑字符串）。</summary>
    public string SelectedRateText {
        get => _playback.SelectedRateText;
        set => _playback.SelectedRateText = value;
    }

    /// <summary>是否循环播放。</summary>
    public bool LoopPlayback {
        get => _playback.LoopPlayback;
        set => _playback.LoopPlayback = value;
    }

    /// <summary>播放推进次数（自动化统计实际 fps 用）。</summary>
    public int AdvanceCount {
        get => _playback.AdvanceCount;
        set => _playback.AdvanceCount = value;
    }

    /// <summary>播放倍率下标（0~8）。改值会重建定时器并落盘。</summary>
    public int RateIndex {
        get => _playback.RateIndex;
        set => _playback.RateIndex = value;
    }

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying => _playback.IsPlaying;

    /// <summary>播放 / 暂停。</summary>
    public void TogglePlay() => _playback.TogglePlay();

    /// <summary>停止播放（切换 IMG / 另存前必调）。</summary>
    public void Stop() => _playback.Stop();

    /// <summary>推进一拍（测试 / 自动化直接驱动，不必等真实定时器）。</summary>
    internal void AdvanceFrame() => _playback.Step();

    /// <summary>清掉播放器的"当前帧高亮"记录（重建帧列表时调用）。</summary>
    internal void ResetFrameHighlight() => _playback.ResetHighlight();

    /// <summary>手动步进（正数向后、负数向前），到两端回绕。</summary>
    public void StepFrame(int delta) {
        if (_playback.StepFrame(_selectedFrame, delta) is { } next) {
            SelectedFrame = next;
        }
    }

    // ── 画布观感 ───────────────────────────────────────────────────────────────

    /// <summary>画布背景档位（只影响显示）。</summary>
    public CanvasBackground Backdrop {
        get => _appearance.Backdrop;
        set {
            if (!_appearance.SetBackdrop(value)) return;

            OnPropertyChanged();
            OnPropertyChanged(nameof(BackdropShortName));
            OnPropertyChanged(nameof(BackdropTooltip));
        }
    }

    /// <summary>背景档位短名。</summary>
    public string BackdropShortName => _appearance.BackdropShortName;

    /// <summary>背景档位完整名（提示语）。</summary>
    public string BackdropTooltip => _appearance.BackdropTooltip;

    /// <summary>画布效果（改动只影响画布显示，并按 IMG 记忆）。</summary>
    public CanvasEffect Effect {
        get => _appearance.Effect;
        set {
            if (!_appearance.SetEffect(value, Document.CurrentImageKey)) return;

            OnPropertyChanged();
            OnPropertyChanged(nameof(UseLinearDodge));
            OnPropertyChanged(nameof(SelectedEffectName));
            View.UpdateCurrentImage();
        }
    }

    /// <summary>效果下拉选项。</summary>
    public IReadOnlyList<string> EffectNames => _appearance.EffectNames;

    /// <summary>效果下拉当前项。</summary>
    public string SelectedEffectName {
        get => _appearance.SelectedEffectName;
        set {
            if (_appearance.TryParseEffect(value, out var effect)) {
                Effect = effect;
            }
        }
    }

    /// <summary>是否走线性减淡。</summary>
    public bool UseLinearDodge => _appearance.UseLinearDodge;

    /// <summary>语言切换后清掉本地化缓存（效果名 / 倍率文案）。</summary>
    public void InvalidateLocalizedOptions() {
        _appearance.InvalidateNames();
        _playback.InvalidateRateOptions();
    }

    /// <summary>
    ///     语言切换：清缓存；状态栏里是上一种语言拼好的一句话（"已打开…"之类的一次性提示），
    ///     没法按新语言重拼，直接换成新语言的"就绪"。
    /// </summary>
    protected override void OnLanguageChanged() {
        InvalidateLocalizedOptions();
        Status = Loc["status.ready"];
    }

    /// <summary>立即把按 IMG 记忆的设置写盘（关窗口时调用，避免防抖定时器还没触发就退出）。</summary>
    public void Flush() {
        _entryFilter.StopDebounce();
        _appearance.Flush();
    }

    // ── 条目 / 选中 ────────────────────────────────────────────────────────────

    /// <summary>当前选中的条目：换条目会停表、丢弃撤销栈与变更基线、恢复效果，并异步重载帧。</summary>
    public EntryItem? SelectedEntry {
        get => _selectedEntry;
        set {
            if (!SetProperty(ref _selectedEntry, value)) return;

            // 过滤重建期间列表会把 SelectedItem 冲成 null，这不是用户操作，不应触发重新解析
            if (_entryFilter.SuppressSelectionReload) return;

            // 切换 IMG 必须先停表：否则 timer 会继续推一个即将被替换的帧列表
            Stop();
            View.ResetTransparentHint();    // 换 IMG 后允许重新提示一次
            Document.ResetSaveConfirmation();  // 换 IMG 后实验性版本要重新确认
            _editor.Detach();               // 旧 IMG 的撤销快照不能再作用到新模型上
            Editor.RefreshChangeMarkers();  // 变更基线随旧 IMG 一起丢弃
            RestoreEffect();
            PendingFrameLoad = Document.LoadFramesAsync(value);
        }
    }

    /// <summary>最近一次帧加载任务，便于调用方等待加载完成（例如"打开后立即导出"）。</summary>
    public Task? PendingFrameLoad { get; private set; }

    /// <summary>当前选中的帧（这里是选中状态的唯一真源，协作者只读写它）。</summary>
    public FrameViewModel? SelectedFrame {
        get => _selectedFrame;
        set {
            if (!SetProperty(ref _selectedFrame, value)) return;
            OnPropertyChanged(nameof(CurrentFrame));
            OnPropertyChanged(nameof(CurrentIndex));
            OnPropertyChanged(nameof(CanTrim));
            OnPropertyChanged(nameof(IsSelectedFrameHidden));
            UpdateSelectionGeometry();
            _playback.Highlight(value);
            View.UpdateCurrentImage();
            View.SyncInspector();

            // 选中的是隐藏帧时给一句明确说明（画布仍显示它，只是播放会跳过）
            if (_selectedFrame is { IsVisible: false }) {
                Status = Loc["status.currentFrameHidden"];
            }
        }
    }

    /// <summary>画布当前显示的帧（与 <see cref="SelectedFrame" /> 同一个对象，通道内的通用叫法）。</summary>
    public FrameViewModel? CurrentFrame {
        get => _selectedFrame;
        set => SelectedFrame = value;
    }

    /// <summary>当前帧在帧列表里的下标；没有选中帧时为 <c>-1</c>。</summary>
    public int CurrentIndex => _selectedFrame is null ? -1 : Frames.IndexOf(_selectedFrame);

    // ── 条目表 ─────────────────────────────────────────────────────────────────

    /// <summary>批量替换完整条目集合，然后<b>只重建一次</b>过滤视图（逐条 Add 会触发 300+ 次通知）。</summary>
    internal void LoadEntries(IEnumerable<EntryItem> items) {
        _bulkLoading = true;
        try {
            Entries.Clear();
            foreach (var item in items) {
                Entries.Add(item);
            }
        } finally {
            _bulkLoading = false;
        }

        ApplyFilter();
        OnPropertyChanged(nameof(HasEntries));
    }

    /// <summary>
    ///     立即按 <see cref="EntryFilter" /> 重建过滤视图（<b>内部可见性供测试直接触发</b>，跳过 150ms 防抖）。
    ///     <para>重建会把 <c>SelectedItem</c> 冲成 <c>null</c>：这里屏蔽误触发的重新解析，事后再把选中项推回去。</para>
    /// </summary>
    internal void ApplyFilter() {
        var keep = _selectedEntry;
        var survived = _entryFilter.Apply(keep);

        OnPropertyChanged(nameof(FilteredEntries));
        OnPropertyChanged(nameof(EntryCountText));

        if (keep is null) {
            return;
        }

        if (survived) {
            if (!ReferenceEquals(_selectedEntry, keep)) {
                _selectedEntry = keep;
                OnPropertyChanged(nameof(SelectedEntry));
            }

            return;
        }

        SelectedEntry = null;
    }

    // ── 状态栏 / 内部同步 ──────────────────────────────────────────────────────

    /// <summary>协作者回写通知的唯一出口（sealed 类型上的非虚调用，JIT 可内联）。</summary>
    internal void Notify(string name) => OnPropertyChanged(name);

    /// <summary>更新状态栏的帧序号文案（1 基序号 / 总数），有隐藏帧追加「隐 N」、有变更帧追加「改 N」。</summary>
    internal void UpdateFrameCounter() {
        var total = Frames.Count;
        var index = _selectedFrame?.Frame.Index ?? -1;
        FrameCounter = StatusText.FrameCounter(index, total, View.HiddenFrameCount, Editor.ChangedFrameCount);
        UpdateSelectionGeometry();
    }

    /// <summary>状态栏几何文案：坐标 / 尺寸被检查器改动后不会换帧，所以必须独立于选中变化重算。</summary>
    private void UpdateSelectionGeometry() =>
        SelectionGeometryText = StatusText.SelectionGeometry(_selectedFrame);

    /// <summary>检查器改了位置/尺寸后：登记一次撤销单元，并重算联合包围盒与当前帧像素。</summary>
    private void RefreshFrameGeometry() {
        _editor.Record();
        Editor.RefreshChangeMarkers();
        View.ResetCanvasBounds(_session.CurrentImg);
        View.UpdateCurrentImage();

        if (!Inspector.LastCommitResizedPixels || _selectedFrame is not { } frame || View.RawImage is null) return;

        // 只有像素尺寸真的变了才重建这一帧的缩略图（结构操作一律复用旧卡片）
        frame.Thumbnail = DocumentSession.CreateThumbnail(
            View.RawImage, DocumentSession.ThumbnailMaxWidth, DocumentSession.ThumbnailMaxHeight);
        frame.RefreshMetrics();
    }

    /// <summary>切换 IMG 时恢复它记忆的「效果」（恢复期间不写回；重绘由紧随其后的 UpdateCurrentImage 完成）。</summary>
    private void RestoreEffect() {
        _appearance.Restore(Document.CurrentImageKey);

        OnPropertyChanged(nameof(Effect));
        OnPropertyChanged(nameof(SelectedEffectName));
        OnPropertyChanged(nameof(UseLinearDodge));
    }

    private void OnSelectedFramesChanged(object? sender, NotifyCollectionChangedEventArgs e) => View.SyncInspector();

    /// <summary>过滤层的广播：属性名与通道不同，这里做一次显式映射。</summary>
    private void OnEntryFilterPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(EntryFilterPresenter.Filtered):
                OnPropertyChanged(nameof(FilteredEntries));
                break;
            case nameof(EntryFilterPresenter.CountText):
                // 批量装载时集合会逐条变更：屏蔽逐条计数通知，装载完由 ApplyFilter 统一 raise
                if (_bulkLoading) break;
                OnPropertyChanged(nameof(EntryCountText));
                break;
            case nameof(EntryFilterPresenter.Filter):
                OnPropertyChanged(nameof(EntryFilter));
                break;
        }
    }

    /// <summary>子 presenter 的广播：属性名与通道的转发属性一致，原样转发即可。</summary>
    private void OnPresenterPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is { } name) {
            OnPropertyChanged(name);
        }
    }

    /// <summary>检查器的广播：状态文案转给状态栏，几何变化触发画布重算。</summary>
    private void OnInspectorPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(FrameInspector.StatusMessage):
                if (Inspector.StatusMessage.Length > 0) {
                    Status = Inspector.StatusMessage;
                }

                break;
            case nameof(FrameInspector.GeometryVersion):
                RefreshFrameGeometry();
                break;
        }
    }

    // ── 兼容入口：实现已搬到协作者，名字保持可用（MainWindowViewModel 直接走协作者） ──

    /// <summary>打开文件或文件夹。</summary>
    public Task OpenAsync(string path) => Document.OpenAsync(path);

    /// <summary>打开一个"NPK 源目录"。</summary>
    public Task OpenFolderAsync(string path) => Document.OpenFolderAsync(path);

    /// <summary>
    ///     是否有未保存的修改：帧编辑（随条目切换重置）或音效替换（不随条目切换重置，保存 / 重新打开才清掉）。
    /// </summary>
    public bool IsDirty => Editor.IsDirty || HasUnsavedAudio;

    // ── 音效条目（NPK 里的 .ogg） ─────────────────────────────────────────────

    private bool _hasUnsavedAudio;

    /// <summary>当前条目是不是音效（非 IMG，内容以 Ogg 页头开头）。</summary>
    public bool IsAudioEntry => _session.CurrentImg is { Version: ImgVersion.Other } img
                                && OggAudioInfo.IsOgg(img.RawContent.Span);

    /// <summary>当前音效条目的原始字节（不是音效时为空）。</summary>
    internal ReadOnlyMemory<byte> CurrentAudio => IsAudioEntry ? _session.CurrentImg!.RawContent : ReadOnlyMemory<byte>.Empty;

    /// <summary>当前音效条目的头部信息（不是音效时为 <c>null</c>）。</summary>
    internal OggAudioInfo? CurrentAudioInfo => IsAudioEntry ? OggAudioInfo.TryParse(CurrentAudio.Span) : null;

    /// <summary>只有 NPK 模式能替换音效（文件夹模式没有音效条目的写回路径）。</summary>
    public bool CanReplaceAudio => IsAudioEntry && _session.Archive is not null;

    /// <summary>音效面板上的说明：编码 / 采样率 / 声道 / 时长 / 大小，替换过但没保存时加注。</summary>
    public string AudioInfoText {
        get {
            if (!IsAudioEntry) return string.Empty;

            var size = CurrentAudio.Length;
            var info = CurrentAudioInfo;
            var text = info is null || info.Codec == OggCodec.Unknown
                ? Loc.F("audio.infoUnknown", FormatSize(size))
                : Loc.F("audio.info", info.Codec, info.SampleRate, info.Channels,
                    info.Duration is { } d ? d.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "?",
                    FormatSize(size));
            return HasUnsavedAudio && SelectedEntry?.Npk?.IsModified == true ? text + Loc["audio.modifiedSuffix"] : text;
        }
    }

    /// <summary>有替换过、还没保存的音效（跨条目累计）。</summary>
    public bool HasUnsavedAudio {
        get => _hasUnsavedAudio;
        private set {
            if (!SetProperty(ref _hasUnsavedAudio, value)) return;
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    /// <summary>用新的 Ogg 字节替换当前音效条目（保存时写入）。</summary>
    internal void ReplaceCurrentAudio(byte[] ogg) {
        if (!CanReplaceAudio || _session.CurrentImg is not { } img) {
            throw new InvalidOperationException(Loc["audio.notReplaceable"]);
        }

        img.ReplaceRawContent(ogg);
        HasUnsavedAudio = true;
        NotifyAudio();
    }

    /// <summary>保存成功或重新打开文件后清掉"有未保存的音效替换"。</summary>
    internal void MarkAudioSaved() => HasUnsavedAudio = false;

    /// <summary>重发音效相关属性（换条目、替换、保存后）。</summary>
    internal void NotifyAudio() {
        OnPropertyChanged(nameof(IsAudioEntry));
        OnPropertyChanged(nameof(CanReplaceAudio));
        OnPropertyChanged(nameof(AudioInfoText));
    }

    /// <summary>
    ///     导出音效到文件夹（按包内路径建子目录，原样写出字节）。
    ///     <paramref name="currentOnly" /> 为真只导当前条目，否则导出包里所有以 Ogg 页头开头的条目。
    ///     替换过还没保存的条目导出的是替换后的内容。
    /// </summary>
    /// <returns>导出的文件数。</returns>
    internal Task<int> ExportAudioAsync(string folder, bool currentOnly) {
        var archive = _session.Archive;
        if (archive is null) return Task.FromResult(0);

        var current = SelectedEntry?.Npk;
        var currentBytes = CurrentAudio.ToArray();

        // 替换过的条目在 IMG 缓存里（永不淘汰）：缓存不是线程安全的，所以先在当前线程取好；
        // 后台线程只走 ReadRaw（RandomAccess 按偏移读，可并发）。
        var modified = new Dictionary<NpkEntry, byte[]>(ReferenceEqualityComparer.Instance);
        if (!currentOnly) {
            foreach (var entry in archive.Entries) {
                if (entry.IsModified) {
                    modified[entry] = archive.LoadImg(entry).RawContent.ToArray();
                }
            }
        }

        return Task.Run(() => {
            var count = 0;
            foreach (var entry in archive.Entries) {
                if (currentOnly && !ReferenceEquals(entry, current)) continue;

                var bytes = currentOnly
                    ? currentBytes
                    : modified.TryGetValue(entry, out var replaced) ? replaced : archive.ReadRaw(entry);

                if (!OggAudioInfo.IsOgg(bytes)) continue;

                var target = AudioExportPath(folder, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
                count++;
            }

            return count;
        });
    }

    /// <summary>包内路径 → 导出路径：统一分隔符，拒绝跳出目标文件夹的路径。</summary>
    internal static string AudioExportPath(string folder, string entryPath) {
        var relative = entryPath.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal)) {
            throw new InvalidOperationException(Loc.F("audio.unsafePath", entryPath));
        }

        return full;
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KB";

    /// <summary>是否可撤销。</summary>
    public bool CanUndo => Editor.CanUndo;

    /// <summary>是否可重做。</summary>
    public bool CanRedo => Editor.CanRedo;

    /// <summary>有变更标记的帧数。</summary>
    public int ChangedFrameCount => Editor.ChangedFrameCount;

    /// <summary>重算帧级变更标记并刷新「改 N」计数（测试 / 自动化直接驱动）。</summary>
    internal void RefreshChangeMarkers() => Editor.RefreshChangeMarkers();

    /// <summary>在当前帧之后插入一帧 1×1 全透明帧，并选中它。</summary>
    public void InsertFrameAfterSelected() => Editor.InsertAfterSelected();

    /// <summary>删除当前选区（多选时全部删除）。</summary>
    public void DeleteSelectedFrames() => Editor.DeleteSelected();

    /// <summary>拖拽排序：把一帧放到"插入缝"上。</summary>
    public void MoveFrameTo(FrameViewModel? frame, int insertIndex) => Editor.MoveTo(frame, insertIndex);

    /// <summary>把当前帧在 IMG 里前后挪动 <paramref name="delta" /> 位（越界不动）。</summary>
    public void MoveSelectedFrame(int delta) => Editor.MoveSelected(delta);

    /// <summary>裁剪选区（多选时逐帧处理）：裁剪算<b>一次</b>撤销单元。</summary>
    public void TrimSelectedFrames() => Editor.TrimSelected();

    /// <summary>撤销一步。</summary>
    public void UndoEdit() => Editor.Undo();

    /// <summary>重做一步。</summary>
    public void RedoEdit() => Editor.Redo();

    /// <summary>批量导出 PNG 到指定目录（每帧一个文件）。</summary>
    public void ExportPngBatch(string directory, IReadOnlyList<FrameViewModel> frames) =>
        Editor.ExportPngBatch(directory, frames);

    /// <summary>把当前 IMG 编码成动画 GIF（多选时只导出选中帧）。</summary>
    public Task ExportGifAsync(string path) => Editor.ExportGifAsync(path);

    /// <summary>用一张图片替换当前选中的帧。</summary>
    public Task ReplaceSelectedFrameAsync(string imagePath) => Editor.ReplaceSelectedAsync(imagePath);

    /// <summary>帧列表的搜索关键字（按 <c>#序号</c> / <c>宽×高</c> 匹配）。</summary>
    public string FilterText {
        get => View.FilterText;
        set => View.FilterText = value;
    }

    /// <summary>按 <see cref="FilterText" /> 过滤后的帧视图（空关键字时就是全部帧）。</summary>
    public IReadOnlyList<FrameViewModel> FilteredFrames => View.FilteredFrames;

    /// <summary>画布当前显示的帧像素。</summary>
    public RgbaImage? CurrentImage => View.CurrentImage;

    /// <summary>画布坐标系尺寸 = 当前 IMG 所有帧的<b>联合包围盒</b>（LINK 帧跳过）。</summary>
    public Size CanvasSize => View.CanvasSize;

    /// <summary>当前帧左上角在画布坐标系中的位置（= 帧坐标 - 包围盒左上角）。</summary>
    public Point FrameOffset => View.FrameOffset;

    /// <summary>画布坐标原点对应的角色坐标（标尺与状态栏显示角色坐标用）。</summary>
    public Point CanvasOrigin => View.CanvasOrigin;

    /// <summary>当前被隐藏（播放时跳过）的帧数。</summary>
    public int HiddenFrameCount => View.HiddenFrameCount;

    /// <summary>是否存在隐藏帧：右键菜单「全部显示」的启停条件。</summary>
    public bool HasHiddenFrames => View.HasHiddenFrames;

    /// <summary>当前帧是否已隐藏（画布仍显示它，只是播放会跳过）。</summary>
    public bool IsSelectedFrameHidden => View.IsSelectedFrameHidden;

    /// <summary>隐藏选区（纯视图状态：不置脏、不进撤销栈）。</summary>
    public void HideSelectedFrames() => View.HideSelected();

    /// <summary>把选区重新设为可见。</summary>
    public void ShowSelectedFrames() => View.ShowSelected();

    /// <summary>把整个 IMG 的帧全部设为可见。</summary>
    public void ShowAllFrames() => View.ShowAll();

    /// <summary>快捷键 <c>H</c>：切换选区的可见性。</summary>
    public void ToggleSelectedFramesVisibility() => View.ToggleSelected();

    /// <summary>自动化截图用：外部直接改过卡片可见性之后重算计数。</summary>
    internal void RefreshFrameVisibility() => View.RefreshVisibility();

    /// <summary>逐字节复刻 pvfUtility 的 <c>LinearBrun</c>（实现见 <see cref="ChannelViewState" />）。</summary>
    public static RgbaImage CreateLinearDodge(RgbaImage source) => ChannelViewState.CreateLinearDodge(source);
}
