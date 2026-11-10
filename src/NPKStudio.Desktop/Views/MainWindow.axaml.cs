using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NPKStudio.Core.Diagnostics;
using NPKStudio.Core.Formats;
using NPKStudio.Core.Imaging;
using NPKStudio.Desktop.Input;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.Services;
using NPKStudio.Desktop.ViewModels;
using NPKStudio.Desktop.Views.Dialogs;

namespace NPKStudio.Desktop.Views;

public sealed partial class MainWindow : Window {
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;

    private const double DefaultLeftPanelWidth = 280;
    private const double DefaultRightPanelWidth = 320;
    private const double CollapsedPanelWidth = 40;

    /// <summary>面板折叠 / 展开的宽度过渡时长（PRD §4.6：180ms ease-out）。</summary>
    private static readonly TimeSpan PanelWidthTransition = TimeSpan.FromMilliseconds(180);

    /// <summary>
    ///     宽度过渡。<see cref="ColumnDefinition" /> 不在可视树里、也不支持 <c>Transitions</c>，
    ///     所以 180ms 的缓动挂在面板 <see cref="Border" /> 自身的 <c>Width</c> 上（列宽仍然瞬时到位）。
    /// </summary>
    private static readonly Transitions PanelWidthTransitions = new() {
        new DoubleTransition {
            Property = Layoutable.WidthProperty,
            Duration = PanelWidthTransition,
            Easing = new CubicEaseOut()
        }
    };

    private readonly MainWindowViewModel _viewModel = new();
    private readonly string? _initialFile;
    private bool _closePromptOpen;
    private bool _suppressDirtyPrompt;
    private bool _guideDragHint;

    /// <summary>帧拖拽的启动阈值（像素）：小于它只算一次普通点击，避免和"点选"抢动作。</summary>
    private const double FrameDragThreshold = 4;

    /// <summary>边缘自动滚动的触边宽度 / 每次移动滚动的像素。</summary>
    private const double FrameAutoScrollEdge = 24;
    private const double FrameAutoScrollStep = 16;

    private FrameViewModel? _dragCandidate;
    private ListBoxItem? _dragSourceItem;
    private IPointer? _dragPointer;
    private Point _dragOrigin;
    private bool _isFrameDragging;
    private int _dragInsertIndex = -1;
    private RenderTargetBitmap? _dragGhostBitmap;

    /// <summary>帧列表内部的滚动容器（首次拖拽时解析一次，避免每次 PointerMoved 走查可视树）。</summary>
    private ScrollViewer? _frameListScroll;

    private double _leftExpandedWidth = DefaultLeftPanelWidth;
    private double _rightExpandedWidth = DefaultRightPanelWidth;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialFile) {
        _initialFile = initialFile;
        InitializeComponent();
        DataContext = _viewModel;

        // 实验性版本（v4/v5/v6）写回前的确认。无头宿主 / 自动化模式在回调里直接返回"继续"，
        // 不会弹出一个没人点的对话框把流程卡住（与关闭流程同一个约束）。
        _viewModel.ConfirmExperimentalSave = AskExperimentalSaveAsync;

        // ContextMenu 不在可视树里，DataContext 不会自动继承；不显式挂上，菜单里的
        // 「标尺 / 参考线」勾选绑定会静默失效（值永远是 false）。
        if (FrameCanvas.ContextMenu is { } canvasMenu) {
            canvasMenu.DataContext = _viewModel;
        }

        // 帧列表右键菜单（隐藏 / 显示 / 全部显示）同理：不挂 DataContext，「全部显示」的
        // IsEnabled="{Binding HasHiddenFrames}" 永远是 false。
        if (FrameList.ContextMenu is { } frameMenu) {
            frameMenu.DataContext = _viewModel;
        }

        // 帧拖拽排序：按下只记录候选，移动越过阈值才真正开始拖，松手才改顺序。
        FrameList.AddHandler(InputElement.PointerPressedEvent, OnFrameListPointerPressed, RoutingStrategies.Tunnel);
        FrameList.AddHandler(InputElement.PointerMovedEvent, OnFrameListPointerMoved, RoutingStrategies.Tunnel);
        FrameList.AddHandler(InputElement.PointerReleasedEvent, OnFrameListPointerReleased, RoutingStrategies.Tunnel);
        FrameList.AddHandler(InputElement.PointerCaptureLostEvent, OnFrameListCaptureLost, RoutingStrategies.Tunnel);

        FrameCanvas.ZoomChanged += (_, _) => UpdateZoomText();

        // 鼠标坐标：控件只在整数坐标变化 / 指针离开时发通知，状态栏不会每 Move 都刷一次。
        FrameCanvas.PointerPositionChanged += (_, _) => UpdatePointerText();

        // 运行时切换语言：这两处文字是代码直接写进控件 / VM 的，不走绑定，要自己重算一次
        Loc.PropertyChanged += OnLanguageChanged;

        // 参考线数量的唯一真相源是画布：新增 / 拖出 / 拖回删除 / 清除都要推给 VM，
        // 右键菜单「清除参考线」才能在没有线时正确置灰。
        FrameCanvas.GuidesChanged += (_, _) => _viewModel.GuideCount = FrameCanvas.GuideCount;
        _viewModel.GuideCount = FrameCanvas.GuideCount;

        // 拖拽期间给一句状态栏提示，松手后换成"当前共 N 条"，不把提示留在那里
        FrameCanvas.GuideDragChanged += (_, _) => {
            if (FrameCanvas.IsDraggingGuide) {
                _guideDragHint = true;
                _viewModel.Status = Loc["status.guideDragging"];
            } else if (_guideDragHint) {
                _guideDragHint = false;
                _viewModel.Status = Loc.F("status.guideCount", FrameCanvas.GuideCount);
            }
        };

        // 折叠 / 拆分只改列宽；面板自身的宽度跟着列走（折叠动画需要有显式起止宽度）
        LeftPanelHost.SizeChanged += (_, e) => SyncPanelWidth(LeftPanel, e.NewSize.Width);
        RightPanelHost.SizeChanged += (_, e) => SyncPanelWidth(RightPanel, e.NewSize.Width);

        LeftSplitter.AddHandler(InputElement.PointerPressedEvent, OnLeftSplitterPressed, RoutingStrategies.Tunnel);
        RightSplitter.AddHandler(InputElement.PointerPressedEvent, OnRightSplitterPressed, RoutingStrategies.Tunnel);

        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DragDrop.SetAllowDrop(this, true);
    }

    /// <summary>
    ///     把面板宽度钉到当前列宽。过渡进行中（<c>Transitions</c> 非空）不跟：
    ///     那时面板正从旧宽度插值到新宽度，跟上去会把动画掐断。
    /// </summary>
    private static void SyncPanelWidth(Border panel, double width) {
        if (panel.Transitions is null) {
            panel.Width = width;
        }
    }

    /// <summary>折叠 / 展开某一栏：列宽立刻到位，面板宽度做 180ms ease-out 过渡。</summary>
    private static void SetPanelWidth(ColumnDefinition column, Border panel, double width) {
        panel.Transitions = PanelWidthTransitions;
        column.Width = new GridLength(width);
        panel.Width = width;

        // 过渡结束后摘掉：之后拖分隔条要跟手，不能被 180ms 缓动拖慢
        DispatcherTimer.RunOnce(
            () => panel.Transitions = null,
            PanelWidthTransition + TimeSpan.FromMilliseconds(40));
    }

    /// <summary>Windows 下让系统标题栏跟随应用主题，避免深色界面顶一条白条。</summary>
    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ==================== 帧拖拽排序 ====================

    /// <summary>
    ///     按下：只记住"可能是拖拽起点"的卡片与坐标，<b>不置 Handled</b>——
    ///     否则 ListBox 的点击选中、右键菜单、双击都收不到事件。
    /// </summary>
    private void OnFrameListPointerPressed(object? sender, PointerPressedEventArgs e) {
        ResetFrameDrag();

        if (!e.GetCurrentPoint(FrameList).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item) return;
        if (item.DataContext is not FrameViewModel frame) return;

        _dragCandidate = frame;
        _dragSourceItem = item;
        _dragOrigin = e.GetPosition(FrameList);
    }

    /// <summary>移动：越过 4px 才开始拖（之前一律当点击），之后每次移动都刷新指示线与幽灵卡片。</summary>
    private void OnFrameListPointerMoved(object? sender, PointerEventArgs e) {
        if (_dragCandidate is null || _dragSourceItem is null) return;

        var point = e.GetPosition(FrameList);

        if (!_isFrameDragging) {
            if (!e.GetCurrentPoint(FrameList).Properties.IsLeftButtonPressed) {
                ResetFrameDrag();
                return;
            }

            var dx = point.X - _dragOrigin.X;
            var dy = point.Y - _dragOrigin.Y;
            if (dx * dx + dy * dy < FrameDragThreshold * FrameDragThreshold) return;

            BeginFrameDrag(e.Pointer);
        }

        UpdateFrameDrag(point);
        AutoScrollFrameList(point);
        e.Handled = true;
    }

    /// <summary>松手：按指示线的位置把这一帧搬过去（复用 MoveFrame 语义，⌘Z 可还原，Windows 为 Ctrl+Z）。</summary>
    private void OnFrameListPointerReleased(object? sender, PointerReleasedEventArgs e) {
        if (!_isFrameDragging) {
            ResetFrameDrag();
            return;
        }

        var frame = _dragCandidate;
        var insertIndex = _dragInsertIndex;

        ResetFrameDrag();

        if (frame is null || insertIndex < 0) return;
        _viewModel.MoveFrameTo(frame, Math.Clamp(insertIndex, 0, _viewModel.Frames.Count));
    }

    /// <summary>指针捕获被系统抢走（Alt+Tab 等）：放弃这次拖拽，不改顺序。</summary>
    private void OnFrameListCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ResetFrameDrag();

    /// <summary>进入拖拽态：捕获指针、把原卡片压到 0.4、生成跟随指针的半透明幽灵。</summary>
    private void BeginFrameDrag(IPointer pointer) {
        if (_dragSourceItem is null) return;

        _isFrameDragging = true;
        _dragPointer = pointer;
        pointer.Capture(FrameList);

        // 先截图再压暗：RenderTargetBitmap 会把 Opacity 一起算进去，
        // 反过来做的话幽灵会变成 0.4×0.75，几乎看不见
        CreateDragGhost(_dragSourceItem);
        _dragSourceItem.Opacity = 0.4;
    }

    /// <summary>更新插入缝与视觉：2px 竖线画在"该插到哪个卡片左边"，幽灵跟着指针。</summary>
    private void UpdateFrameDrag(Point point) {
        var rects = FrameCardRects();
        _dragInsertIndex = FrameDropPlanner.InsertIndex(rects, point);
        ShowDropIndicator(rects, _dragInsertIndex);

        Canvas.SetLeft(FrameDragGhost, point.X - FrameDragGhost.Width / 2);
        Canvas.SetTop(FrameDragGhost, point.Y - FrameDragGhost.Height / 2);
    }

    /// <summary>拖到列表上下边缘时滚动内部 ScrollViewer（每次移动滚一小步，不做惯性）。</summary>
    private void AutoScrollFrameList(Point point) {
        // 可视树走查只做一次：拖拽期间每次 PointerMoved 都遍历整棵树 + 分配迭代器是纯浪费。
        var scroll = _frameListScroll ??= FrameList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;

        var maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        if (maxOffset <= 0) return;

        var delta = point.Y < FrameAutoScrollEdge
            ? -FrameAutoScrollStep
            : point.Y > FrameList.Bounds.Height - FrameAutoScrollEdge
                ? FrameAutoScrollStep
                : 0;
        if (delta == 0) return;

        var offset = scroll.Offset;
        scroll.Offset = new Vector(offset.X, Math.Clamp(offset.Y + delta, 0, maxOffset));
    }

    /// <summary>各卡片在 <see cref="FrameList" /> 坐标系里的矩形（顺序与帧列表一致）。</summary>
    /// <remarks>
    ///     列表用的是非虚拟化的 <c>WrapPanel</c>，每个卡片都有容器；
    ///     若将来换成虚拟化面板，缺容器的下标会错位，这里会先返回较短列表——届时必须改成"带下标的矩形"。
    /// </remarks>
    private List<Rect> FrameCardRects() {
        var rects = new List<Rect>(_viewModel.Frames.Count);
        for (var i = 0; i < _viewModel.Frames.Count; i++) {
            if (FrameList.ContainerFromIndex(i) is not Control container) continue;
            if (container.TranslatePoint(new Point(0, 0), FrameList) is not { } origin) continue;

            rects.Add(new Rect(origin, container.Bounds.Size));
        }

        return rects;
    }

    /// <summary>插入指示线：画在目标卡片左边缘（末端时画在最后一张右边缘）。</summary>
    private void ShowDropIndicator(IReadOnlyList<Rect> rects, int insertIndex) {
        if (rects.Count == 0) {
            FrameDropIndicator.IsVisible = false;
            return;
        }

        var target = insertIndex >= rects.Count ? rects[^1] : rects[Math.Max(0, insertIndex)];
        var x = insertIndex >= rects.Count ? target.Right - 3 : target.Left - 3;

        Canvas.SetLeft(FrameDropIndicator, x);
        Canvas.SetTop(FrameDropIndicator, target.Top);
        FrameDropIndicator.Height = target.Height;
        FrameDropIndicator.IsVisible = true;
        FrameDragGhost.IsVisible = _isFrameDragging;
    }

    /// <summary>把卡片渲染成位图当"幽灵"（半透明跟随）；渲染失败就只保留原卡片变淡。</summary>
    private void CreateDragGhost(ListBoxItem item) {
        var width = Math.Max(1, item.Bounds.Width);
        var height = Math.Max(1, item.Bounds.Height);

        _dragGhostBitmap?.Dispose();
        _dragGhostBitmap = null;

        try {
            var bitmap = new RenderTargetBitmap(
                new PixelSize(Math.Max(1, (int)Math.Ceiling(width)), Math.Max(1, (int)Math.Ceiling(height))),
                new Vector(96, 96));
            bitmap.Render(item);
            _dragGhostBitmap = bitmap;
            FrameDragGhostImage.Source = bitmap;
        } catch {
            // 渲染失败不影响拖拽本身（只是没有幽灵图）
            FrameDragGhostImage.Source = null;
        }

        FrameDragGhost.Width = width;
        FrameDragGhost.Height = height;
        FrameDragGhost.IsVisible = _isFrameDragging;
    }

    /// <summary>清掉所有拖拽痕迹（不改模型、不改选区）。</summary>
    private void ResetFrameDrag() {
        if (_isFrameDragging) {
            _dragPointer?.Capture(null);
        }

        _dragPointer = null;

        if (_dragSourceItem is not null) {
            _dragSourceItem.Opacity = 1;
        }

        _isFrameDragging = false;
        _dragCandidate = null;
        _dragSourceItem = null;
        _dragInsertIndex = -1;

        FrameDropIndicator.IsVisible = false;
        FrameDragGhost.IsVisible = false;
        FrameDragGhostImage.Source = null;

        _dragGhostBitmap?.Dispose();
        _dragGhostBitmap = null;
    }

    /// <summary>
    ///     截图专用：把列表摆成"正在拖第 <paramref name="from" /> 张、插入缝在 <paramref name="insert" />"的样子，
    ///     <b>不移动任何帧</b>。只在自动化参数 <c>--drag-preview</c> 下调用。
    /// </summary>
    /// <param name="from">被拖的卡片下标（0 基）。</param>
    /// <param name="insert">插入缝下标（0..帧数）。</param>
    internal void ShowFrameDragPreview(int from, int insert) {
        if (from < 0 || from >= _viewModel.Frames.Count) return;
        if (FrameList.ContainerFromIndex(from) is not ListBoxItem item) return;

        _dragCandidate = _viewModel.Frames[from];
        _dragSourceItem = item;
        _isFrameDragging = true;

        // 顺序与真实拖拽一致：先截幽灵，再压暗原卡片
        CreateDragGhost(item);
        item.Opacity = 0.4;

        var rects = FrameCardRects();
        _dragInsertIndex = Math.Clamp(insert, 0, rects.Count);
        ShowDropIndicator(rects, _dragInsertIndex);

        // 幽灵贴在指示线左边（不压住 2px 指示线），略微上抬表现"被拎起来"
        var target = _dragInsertIndex >= rects.Count ? rects[^1] : rects[Math.Max(0, _dragInsertIndex)];
        var ghostLeft = _dragInsertIndex >= rects.Count
            ? target.Right - FrameDragGhost.Width - 10
            : target.Left - FrameDragGhost.Width - 10;
        Canvas.SetLeft(FrameDragGhost, Math.Max(4, ghostLeft));
        Canvas.SetTop(FrameDragGhost, Math.Max(4, target.Top - 8));
        FrameDragGhost.IsVisible = true;
    }


    /// <summary>
    ///     以指定退出码退出应用。
    ///     <para>
    ///         必须<b>推迟到当前事件处理完之后</b>再关：在 <see cref="OnOpened" /> 里同步
    ///     <c>Close()</c>（Windows 上）会崩在原生窗口销毁路径里（实测段错误）。
    ///         用 <c>Shutdown(code)</c> 而不是 <c>Environment.ExitCode</c>，
    ///         因为 Avalonia 结束 <c>StartWithClassicDesktopLifetime</c> 时会用
    ///     <c>lifetime.ExitCode</c> 覆盖进程退出码。
    ///     </para>
    /// </summary>
    private void ShutdownWithCode(int exitCode) {
        Dispatcher.UIThread.Post(() => {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
                desktop.Shutdown(exitCode);
                return;
            }

            Environment.ExitCode = exitCode;
            Close();
        });
    }

    /// <summary>
    ///     是否处于无人值守的自动化模式（带任一自动化开关）。
    ///     <para>此模式下"打开失败"必须自己退出并置非 0 退出码，否则批处理会卡在一个永远不动的窗口上。</para>
    /// </summary>
    private bool IsAutomationMode =>
        !string.IsNullOrEmpty(ExportPngOnOpen) || !string.IsNullOrEmpty(SaveNpkOnOpen) ||
        !string.IsNullOrEmpty(ReplacePngOnOpen) || !string.IsNullOrEmpty(PlayOnOpen) ||
        !string.IsNullOrEmpty(ZoomOnOpen) || !string.IsNullOrEmpty(RenderShotOnOpen) ||
        !string.IsNullOrEmpty(DumpStateOnOpen) || !string.IsNullOrEmpty(WindowShotOnOpen) || CollapseLeftOnOpen ||
        !string.IsNullOrEmpty(PointerOnOpen) || !string.IsNullOrEmpty(GuidesOnOpen) ||
        !string.IsNullOrEmpty(OriginModeOnOpen) || !string.IsNullOrEmpty(HideFramesOnOpen) ||
        !string.IsNullOrEmpty(DragPreviewOnOpen) || !string.IsNullOrEmpty(FrameMenuShotOnOpen) ||
        !string.IsNullOrEmpty(EntryOnOpen) || !string.IsNullOrEmpty(ChangeMarksOnOpen) ||
        !string.IsNullOrEmpty(RulersOnOpen);

    /// <summary>
    ///     实验性版本写回前问一次。
    ///     <para>
    ///         自动化模式与无头宿主（<c>ApplicationLifetime</c> 不是桌面生命周期）<b>一律不弹</b>，
    ///         直接走默认"继续"——弹窗会和关闭流程一样卡在一个永远不会有人点的地方。
    ///     </para>
    /// </summary>
    /// <param name="version">当前 IMG 版本。</param>
    /// <returns><c>true</c> 继续保存。</returns>
    private async Task<bool> AskExperimentalSaveAsync(ImgVersion version) {
        if (IsAutomationMode || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime) {
            return true;
        }

        return await ExperimentalSaveDialog.AskAsync(this, version);
    }

    protected override async void OnOpened(EventArgs e) {
        base.OnOpened(e);

        try {
            await RunOpenedAsync();
        } catch (Exception ex) {
            // async void 里的异常没人接就会直接带走进程；这里转成"状态栏 + 日志 + 自动化退出码"。
            TraceLog.Instance.Error("启动/自动化失败", ex);
            _viewModel.Status = Loc.F("status.startFailed", ex.Message);

            if (IsAutomationMode) {
                ShutdownWithCode(1);
            }
        }
    }

    /// <summary>
    ///     <see cref="OnOpened" /> 的实际内容（打开目标 + 可选自动化流程）。
    ///     <para>拆出来是为了让 <see cref="OnOpened" /> 能包住整个流程做异常兜底。</para>
    /// </summary>
    private async Task RunOpenedAsync() {
        ApplyTitleBarTheme();

        var target = _initialFile;
        var hasTarget = !string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target));

        if (!hasTarget) {
            if (IsAutomationMode) {
                ShutdownWithCode(1);
            }

            // GUI 模式：保持原行为，停在这里等用户自己拖文件进来
            return;
        }

        await _viewModel.OpenAsync(target!);

        if (IsAutomationMode && !_viewModel.LastOpenSucceeded) {
            ShutdownWithCode(1);
            return;
        }

        // 截图 / 验收专用：切到指定条目（真实 NPK 里挑一个帧多的 IMG）
        if (!string.IsNullOrEmpty(EntryOnOpen) && int.TryParse(EntryOnOpen, out var entryIndex) &&
            entryIndex >= 0 && entryIndex < _viewModel.Entries.Count) {
            _viewModel.SelectedEntry = _viewModel.Entries[entryIndex];
            if (_viewModel.PendingFrameLoad is { } entryLoad) {
                await entryLoad;
            }

            await Task.Delay(200);
        }

        // 截图对比用：必须在 --zoom 之前关掉，否则缩放会被重新吸附回整数档
        if (NoPixelAlignOnOpen) {
            _viewModel.IsPixelAlignEnabled = false;
        }

        // 截图专用：标尺开关（main-window 这类"干净画面"不要标尺）
        if (!string.IsNullOrEmpty(RulersOnOpen)) {
            _viewModel.IsRulersVisible = !string.Equals(RulersOnOpen, "off", StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(BackgroundOnOpen)) {
            _viewModel.Backdrop = BackgroundOnOpen.ToLowerInvariant() switch {
                "dark" => Controls.CanvasBackground.Dark,
                "light" => Controls.CanvasBackground.Light,
                "green" => Controls.CanvasBackground.Green,
                "black" => Controls.CanvasBackground.Black,
                "checkerlight" => Controls.CanvasBackground.CheckerLight,
                "checkerdark" => Controls.CanvasBackground.CheckerDark,
                _ => Controls.CanvasBackground.Checker
            };
        }

        // 播放与缩放同时给定时并发跑，用来验证"播放中连续缩放"不崩不卡
        if (!string.IsNullOrEmpty(PlayOnOpen) && !string.IsNullOrEmpty(ZoomOnOpen)) {
            var playback = RunPlaybackProbeAsync(PlayOnOpen);
            await RunZoomProbeAsync(ZoomOnOpen);
            await playback;
        } else {
            if (!string.IsNullOrEmpty(ZoomOnOpen)) {
                await RunZoomProbeAsync(ZoomOnOpen);
            }

            if (!string.IsNullOrEmpty(PlayOnOpen)) {
                await RunPlaybackProbeAsync(PlayOnOpen);
            }
        }

        if (!string.IsNullOrEmpty(ReplacePngOnOpen)) {
            await _viewModel.ReplaceSelectedFrameAsync(ReplacePngOnOpen);
        }

        if (!string.IsNullOrEmpty(SaveNpkOnOpen)) {
            await _viewModel.SaveAsync(SaveNpkOnOpen);
        }

        if (!string.IsNullOrEmpty(EffectOnOpen)) {
            _viewModel.Effect = string.Equals(EffectOnOpen, "lineardodge", StringComparison.OrdinalIgnoreCase)
                ? Controls.CanvasEffect.LinearDodge
                : Controls.CanvasEffect.None;
        }

        if (!string.IsNullOrEmpty(FrameOnOpen) && int.TryParse(FrameOnOpen, out var frameIndex) &&
            frameIndex >= 0 && frameIndex < _viewModel.Frames.Count) {
            _viewModel.SelectedFrame = _viewModel.Frames[frameIndex];
            await Task.Delay(250);
        }

        // 截图专用：制造"替换 / 插入 / 移动"三种帧级变更（真实走 VM 入口，标记按基线算出来）
        if (!string.IsNullOrEmpty(ChangeMarksOnOpen)) {
            await ApplyChangeMarksAsync(ChangeMarksOnOpen);
        }

        // 截图专用：隐藏若干帧（0 基）。不改模型，只改卡片可见性
        if (!string.IsNullOrEmpty(HideFramesOnOpen)) {
            foreach (var token in HideFramesOnOpen.Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                                 StringSplitOptions.TrimEntries)) {
                if (!int.TryParse(token, out var hideIndex)) continue;
                if (hideIndex < 0 || hideIndex >= _viewModel.Frames.Count) continue;

                _viewModel.Frames[hideIndex].IsVisible = false;
            }

            _viewModel.RefreshFrameVisibility();
            await Task.Delay(150);
        }

        // 截图专用：摆出拖拽中的样子（from:insert），不移动任何帧
        if (!string.IsNullOrEmpty(DragPreviewOnOpen)) {
            await Task.Delay(150);
            var parts = DragPreviewOnOpen.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var dragFrom) &&
                int.TryParse(parts[1], out var dragInsert)) {
                ShowFrameDragPreview(dragFrom, dragInsert);
            }
        }

        // 倍率：独立于播放探针，单独传 --rate 也会生效（也便于验证记忆）
        if (int.TryParse(RateOnOpen, out var rateIdx)) {
            _viewModel.RateIndex = Math.Clamp(rateIdx, 0, 8);
        } else if (int.TryParse(PlaybackFps, out var legacyFps)) {
            // 兼容旧参数：把目标 fps 映射到最接近的倍率档
            var best = 0;
            var bestDelta = double.MaxValue;
            for (var i = 0; i < 9; i++) {
                var candidate = Math.Clamp((int)Math.Round(8 * RateMultiplierAt(i)), 1, 120);
                var delta = Math.Abs(candidate - legacyFps);
                if (delta >= bestDelta) continue;
                bestDelta = delta;
                best = i;
            }

            _viewModel.RateIndex = best;
        }

        if (!string.IsNullOrEmpty(DumpStateOnOpen)) {
            File.WriteAllText(DumpStateOnOpen,
                $"key={_viewModel.CurrentImageKey};effect={_viewModel.Effect};" +
                $"name={_viewModel.SelectedEffectName};linearDodge={_viewModel.UseLinearDodge};" +
                $"rateIndex={_viewModel.RateIndex};fps={_viewModel.EffectiveFps};" +
                $"rateText={_viewModel.SelectedRateText};rates=[{string.Join("|", _viewModel.RateOptions)}];" +
                $"options=[{string.Join(",", _viewModel.EffectNames)}]");
        }

        if (!string.IsNullOrEmpty(FrameProbeOnOpen)) {
            await RunFrameProbeAsync(FrameProbeOnOpen);
        }

        if (CollapseLeftOnOpen) {
            ToggleLeftPanel();
        }

        // 截图专用：先把视口定稿，再定基准 → 放游标 / 参考线，最后整个窗口渲染
        if (!string.IsNullOrEmpty(OriginModeOnOpen)) {
            _viewModel.OriginMode = string.Equals(OriginModeOnOpen, "role", StringComparison.OrdinalIgnoreCase)
                ? Controls.RulerOriginMode.Role
                : Controls.RulerOriginMode.Image;
        }

        if (!string.IsNullOrEmpty(PointerOnOpen) && TryParsePair(PointerOnOpen, out var pointerX, out var pointerY)) {
            FrameCanvas.SimulatePointer(new Point(pointerX, pointerY));
        }

        if (!string.IsNullOrEmpty(GuidesOnOpen)) {
            ApplyGuides(GuidesOnOpen);
        }

        if (!string.IsNullOrEmpty(WindowShotOnOpen)) {
            await Task.Delay(350);

            // 物理鼠标可能正停在窗口里（窗口一出现就有一次 PointerEntered），
            // 会画出一道十字线并带出游标数字；截图要求"干净画面"时在这里再清一次。
            if (string.Equals(PointerOnOpen, "none", StringComparison.OrdinalIgnoreCase)) {
                FrameCanvas.SimulatePointer(null);
            }

            var width = Math.Max(1, (int)Bounds.Width);
            var height = Math.Max(1, (int)Bounds.Height);
            using var whole = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            whole.Render(this);
            whole.Save(WindowShotOnOpen);
        }

        // 截图专用：打开帧列表右键菜单，并把"弹出层"单独渲染成一张图。
        // 弹出层是独立的 PopupRoot（另一个 TopLevel），Render(window) 拍不到它。
        if (!string.IsNullOrEmpty(FrameMenuShotOnOpen)) {
            await Task.Delay(150);
            FrameList.ContextMenu?.Open(FrameList);
            await Task.Delay(300);

            if (FrameList.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault() is { } firstItem &&
                TopLevel.GetTopLevel(firstItem) is { } popup) {
                var popupWidth = Math.Max(1, (int)Math.Ceiling(popup.Bounds.Width));
                var popupHeight = Math.Max(1, (int)Math.Ceiling(popup.Bounds.Height));
                using var menuShot = new RenderTargetBitmap(new PixelSize(popupWidth, popupHeight), new Vector(96, 96));
                menuShot.Render(popup);
                menuShot.Save(FrameMenuShotOnOpen);
            }

            FrameList.ContextMenu?.Close();
        }

        if (!string.IsNullOrEmpty(RenderShotOnOpen)) {
            await Task.Delay(300);
            var width = Math.Max(1, (int)FrameCanvas.Bounds.Width);
            var height = Math.Max(1, (int)FrameCanvas.Bounds.Height);
            using var shot = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            shot.Render(FrameCanvas);
            shot.Save(RenderShotOnOpen);

            // 顺带统计整张画布的像素（用于判断"黑是否真的没加到背景上"）
            var total = width * height * 4;
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(total);
            try {
                shot.CopyPixels(new PixelRect(0, 0, width, height), buffer, total, width * 4);
                var bytes = new byte[total];
                System.Runtime.InteropServices.Marshal.Copy(buffer, bytes, 0, total);
                long sb = 0, sg = 0, sr = 0;
                int minB = 255, maxB = 0;
                for (var i = 0; i < total; i += 4) {
                    sb += bytes[i];
                    sg += bytes[i + 1];
                    sr += bytes[i + 2];
                    if (bytes[i] < minB) minB = bytes[i];
                    if (bytes[i] > maxB) maxB = bytes[i];
                }

                var count = width * height;
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "npkstudio-renderstats.txt"),
                    $"size={width}x{height} meanB={sb / (double)count:F1} meanG={sg / (double)count:F1} " +
                    $"meanR={sr / (double)count:F1} minB={minB} maxB={maxB}");
            } finally {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }

        if (!string.IsNullOrEmpty(ExportPngOnOpen)) {
            _viewModel.ExportCurrentPng(ExportPngOnOpen);
        }

        if (!string.IsNullOrEmpty(ReplacePngOnOpen) || !string.IsNullOrEmpty(SaveNpkOnOpen) ||
            !string.IsNullOrEmpty(ExportPngOnOpen) || !string.IsNullOrEmpty(PlayOnOpen) ||
            !string.IsNullOrEmpty(ZoomOnOpen) || !string.IsNullOrEmpty(RenderShotOnOpen) ||
            !string.IsNullOrEmpty(DumpStateOnOpen) || !string.IsNullOrEmpty(WindowShotOnOpen) ||
            !string.IsNullOrEmpty(PointerOnOpen) || !string.IsNullOrEmpty(GuidesOnOpen) ||
            !string.IsNullOrEmpty(FrameMenuShotOnOpen)) {
            Close();
        }
    }

    /// <summary>
    ///     诊断：打开指定 IMG，逐帧打印"解码尺寸 / 画布尺寸 / 偏移 / 效果 / 缩略图 / alpha 统计"，
    ///     并把每帧的**原始解码结果**与**画布实际显示的 CurrentImage**各导出一张 PNG。
    ///     <para>
    ///         用途：定位"缩略图有内容但画布空白"。缩略图走未处理的 Decode，画布走
    ///         <c>Effect == LinearDodge ? CreateLinearDodge(...)</c>，两条路径不一致时本探针能看出来。
    ///     </para>
    /// </summary>
    private async Task RunFrameProbeAsync(string imgPath) {
        var dir = Path.GetDirectoryName(imgPath)!;
        var name = Path.GetFileName(imgPath);
        var outDir = Path.Combine(Path.GetTempPath(), "npkstudio-frame-probe");
        Directory.CreateDirectory(outDir);

        var lines = new List<string>();
        lines.Add($"probe={imgPath}");
        lines.Add($"requestedEffect={(string.IsNullOrEmpty(EffectOnOpen) ? "<memory>" : EffectOnOpen)}");

        await _viewModel.OpenAsync(dir);
        var entry = _viewModel.Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) {
            lines.Add($"entryNotFound entries={_viewModel.Entries.Count}");
            File.WriteAllLines(Path.Combine(outDir, "probe.txt"), lines);
            return;
        }

        _viewModel.SelectedEntry = entry;
        if (_viewModel.PendingFrameLoad is not null) {
            await _viewModel.PendingFrameLoad;
        }

        lines.Add($"frames={_viewModel.Frames.Count} selected={_viewModel.Frames.IndexOf(_viewModel.SelectedFrame!)}");

        var count = Math.Min(5, _viewModel.Frames.Count);
        for (var i = 0; i < count; i++) {
            var frameVm = _viewModel.Frames[i];
            _viewModel.SelectedFrame = frameVm;

            // --effect 指定的效果要"压过"按 IMG 记忆，否则测不出效果差异
            if (!string.IsNullOrEmpty(EffectOnOpen)) {
                _viewModel.Effect = string.Equals(EffectOnOpen, "lineardodge", StringComparison.OrdinalIgnoreCase)
                    ? Controls.CanvasEffect.LinearDodge
                    : Controls.CanvasEffect.None;
            }

            var frame = frameVm.Frame;
            RgbaImage? raw = null;
            try {
                raw = _viewModel.CurrentImage;   // 已含效果
            } catch {
                raw = null;
            }

            var current = _viewModel.CurrentImage;
            var canvas = _viewModel.CanvasSize;
            var offset = _viewModel.FrameOffset;
            var thumb = frameVm.Thumbnail;

            lines.Add(string.Join(";",
                $"frame={i}",
                $"size={frame.Width}x{frame.Height}",
                $"xy=({frame.X},{frame.Y})",
                $"type=0x{(int)frame.Type:X2}",
                $"compress=0x{(int)frame.Compress:X2}",
                $"decoded={(current is null ? "NULL" : "OK")}",
                $"pixels={(current is null ? "-" : $"{current.Width}x{current.Height}")}",
                $"canvas={canvas.Width}x{canvas.Height}",
                $"offset=({offset.X},{offset.Y})",
                $"effect={_viewModel.Effect}",
                $"thumb={(thumb is null ? "NULL" : $"{thumb.Width}x{thumb.Height}")}",
                $"curA0={CountZeroAlpha(current)}",
                $"curMaxA={MaxAlpha(current)}",
                $"curMaxCh={MaxChannel(current)}",
                $"status={_viewModel.Status}"));

            if (current is not null) {
                ImageIo.SavePng(current, Path.Combine(outDir, $"{name}_f{i}_canvas.png"));
            }

            // 原始解码（绕过效果）单独导一张，用于区分"解码问题"与"效果问题"
            try {
                var sessionImg = _viewModel.Frames[i].Frame;
                var decoded = entry.Npk is not null && _viewModel.Archive is not null
                    ? _viewModel.Archive.LoadImg(entry.Npk).Decode(sessionImg)
                    : null;
                if (decoded is not null) {
                    ImageIo.SavePng(decoded, Path.Combine(outDir, $"{name}_f{i}_raw.png"));
                    lines.Add($"  raw: {decoded.Width}x{decoded.Height} A0={CountZeroAlpha(decoded)} maxA={MaxAlpha(decoded)} maxCh={MaxChannel(decoded)}");
                }
            } catch (Exception ex) {
                lines.Add($"  raw: EX {ex.GetType().Name} {ex.Message}");
            }
        }

        File.WriteAllLines(Path.Combine(outDir, "probe.txt"), lines);
    }

    private static int CountZeroAlpha(RgbaImage? image) {
        if (image is null) return -1;
        var n = 0;
        for (var i = 3; i < image.Pixels.Length; i += 4) {
            if (image.Pixels[i] == 0) n++;
        }

        return n;
    }

    private static int MaxAlpha(RgbaImage? image) {
        if (image is null) return -1;
        var max = 0;
        for (var i = 3; i < image.Pixels.Length; i += 4) {
            if (image.Pixels[i] > max) max = image.Pixels[i];
        }

        return max;
    }

    private static int MaxChannel(RgbaImage? image) {
        if (image is null) return -1;
        var max = 0;
        for (var i = 0; i < image.Pixels.Length; i += 4) {
            for (var c = 0; c < 3; c++) {
                if (image.Pixels[i + c] > max) max = image.Pixels[i + c];
            }
        }

        return max;
    }

    /// <summary>
    ///     自动化验收：缩放探针。<c>--zoom 400</c> 设一次；<c>--zoom 400+20</c> 表示连续 20 次、每次 100 ms 的缩放扫描。
    ///     <para>结束时把"耗时 / 最终缩放"写到 <c>%TEMP%\npkstudio-zoom.txt</c>。</para>
    /// </summary>
    private async Task RunZoomProbeAsync(string spec) {
        var parts = spec.Split('+');
        if (!double.TryParse(parts[0], out var percent)) return;

        var sweeps = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (sweeps <= 1) {
            FrameCanvas.SetZoomLevel(percent / 100.0);
            await Task.Delay(400); // 等防抖窗口（120 ms）过去
        } else {
            for (var i = 0; i < sweeps; i++) {
                FrameCanvas.SetZoomLevel((percent / 100.0) + (i * 0.25));
                await Task.Delay(100);
            }

            await Task.Delay(400);
        }

        sw.Stop();

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "npkstudio-zoom.txt"),
            $"zoom={FrameCanvas.Zoom:F2};elapsedMs={sw.ElapsedMilliseconds};done=1");
    }

    /// <summary>
    ///     自动化验收：播放指定毫秒数，把"结束时停在第几帧 / 是否仍在播放 / 是否循环"写到临时文件。
    ///     <para>仅用于无人值守验证（播放依赖 <c>DispatcherTimer</c>，无法用单元测试覆盖）。</para>
    /// </summary>
    private async Task RunPlaybackProbeAsync(string milliseconds) {
        if (!int.TryParse(milliseconds, out var ms)) return;

        if (PlaybackNoLoop) {
            _viewModel.LoopPlayback = false;
        }

        _viewModel.AdvanceCount = 0;

        _viewModel.TogglePlay();
        var startedPlaying = _viewModel.IsPlaying;

        if (int.TryParse(RateMidOnOpen, out var midIndex)) {
            await Task.Delay(ms / 2);
            _viewModel.RateIndex = midIndex;
            await Task.Delay(ms / 2);
        } else {
            await Task.Delay(ms);
        }

        var counter = _viewModel.FrameCounter;
        var stillPlaying = _viewModel.IsPlaying;
        var index = _viewModel.SelectedFrame?.Frame.Index ?? -1;
        var highlighted = _viewModel.SelectedFrame?.IsCurrent ?? false;

        _viewModel.Stop();

        var advanced = _viewModel.AdvanceCount;
        var elapsedSeconds = ms / 1000.0;
        var actualFps = elapsedSeconds > 0 ? advanced / elapsedSeconds : 0;

        var report = $"counter={counter};started={startedPlaying};playing={stillPlaying};" +
                     $"index={index};highlighted={highlighted};frames={_viewModel.Frames.Count};" +
                     $"loop={_viewModel.LoopPlayback};rateIndex={_viewModel.RateIndex};" +
                     $"fps={_viewModel.EffectiveFps};advanced={advanced};" +
                     $"measuredFps={actualFps:F2};finalRate={_viewModel.RateIndex};" +
                     $"stoppedAfter={_viewModel.IsPlaying}";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "npkstudio-play.txt"), report);
    }

    /// <summary>非空时表示以"打开并导出"模式运行（仅用于自动化验证）。</summary>
    public string? ExportPngOnOpen { get; init; }

    /// <summary>非空时表示打开后立即用该图替换第 0 帧（仅用于自动化验证）。</summary>
    public string? ReplacePngOnOpen { get; init; }

    /// <summary>非空时表示打开后立即另存到该路径（仅用于自动化验证）。</summary>
    public string? SaveNpkOnOpen { get; init; }

    /// <summary>非空时指定画布背景模式：checker / dark / light / green（仅用于自动化验证）。</summary>
    public string? BackgroundOnOpen { get; init; }

    /// <summary>非空时指定标尺开关：on（默认）/ off（仅用于自动化截图）。</summary>
    public string? RulersOnOpen { get; init; }

    /// <summary>非空时表示打开后播放该毫秒数，然后写出播放报告（仅用于自动化验证）。</summary>
    public string? PlayOnOpen { get; init; }

    /// <summary>自动化播放时关闭循环。</summary>
    public bool PlaybackNoLoop { get; init; }

    /// <summary>自动化播放时指定的帧率（字符串形式，便于从命令行传入）。</summary>
    public string? PlaybackFps { get; init; }

    /// <summary>自动化播放时指定的倍率下标（0~8）。</summary>
    public string? RateOnOpen { get; init; }

    /// <summary>自动化验收：播放中途切到的倍率下标（验证立即生效且不崩）。</summary>
    public string? RateMidOnOpen { get; init; }

    /// <summary>诊断：逐帧输出解码/画布/效果/缩略图状态，并导出每帧 PNG。</summary>
    public string? FrameProbeOnOpen { get; init; }

    /// <summary>自动化验收：把整个窗口渲染成 PNG（验证界面布局）。</summary>
    public string? WindowShotOnOpen { get; init; }

    /// <summary>自动化验收：截图前把左栏折叠成 40px 竖条（验证折叠态布局）。</summary>
    public bool CollapseLeftOnOpen { get; init; }

    /// <summary>自动化验收：把当前 VM 状态写到指定文件（验证按 IMG 记忆）。</summary>
    public string? DumpStateOnOpen { get; init; }

    /// <summary>自动化验收：把画布渲染成 PNG（含 Skia Plus 混合效果）。</summary>
    public string? RenderShotOnOpen { get; init; }

    /// <summary>自动化验收：打开后应用的效果（none / lineardodge）。</summary>
    public string? EffectOnOpen { get; init; }

    /// <summary>自动化验收：打开后跳到的帧（0 基）。</summary>
    public string? FrameOnOpen { get; init; }

    /// <summary>截图 / 验收专用：打开后按 0 基序号切换条目（用来在真实 NPK 里挑一个帧多的 IMG）。</summary>
    public string? EntryOnOpen { get; init; }

    /// <summary>
    ///     截图专用：打开后制造三种帧级变更，格式 <c>replace,insertAfter,move</c>（0 基）。
    ///     <para>
    ///         例：<c>1,3,5</c> = 用第 2 帧的像素替换第 1 帧、在第 3 帧之后插入一帧、把第 5 帧上移一位。
    ///         走的是与菜单 / 工具栏完全相同的 VM 入口，因此标记也是按基线算出来的，不是摆拍。
    ///     </para>
    /// </summary>
    public string? ChangeMarksOnOpen { get; init; }

    /// <summary>自动化验收：打开后设置缩放，如 <c>400</c> 或 <c>400+20</c>（连续扫描 20 次）。</summary>
    public string? ZoomOnOpen { get; init; }

    /// <summary>
    ///     自动化验收：把鼠标游标模拟到画布内某个屏幕坐标（<c>x,y</c>），仅用于截图；
    ///     <c>none</c> 表示清空游标（截"干净画面"时用，避免物理鼠标停在窗口里画出一道十字线）。
    /// </summary>
    public string? PointerOnOpen { get; init; }

    /// <summary>自动化验收：按屏幕坐标放参考线，格式 <c>h:300,v:520</c>；<c>hr:</c>/<c>vr:</c> 直接按角色坐标放。</summary>
    public string? GuidesOnOpen { get; init; }

    /// <summary>自动化验收：坐标基准（<c>image</c> / <c>role</c>），仅用于截图对比。</summary>
    public string? OriginModeOnOpen { get; init; }

    /// <summary>自动化验收：关闭默认开启的像素对齐（与 <c>--no-pixel-align</c> 对应），用于连续缩放对比截图。</summary>
    public bool NoPixelAlignOnOpen { get; init; }

    /// <summary>
    ///     截图 / 验收专用：打开后按 0 基序号隐藏若干帧（逗号分隔，如 <c>1,2</c>）。
    ///     <para>走的是与右键菜单同一条 <see cref="FrameViewModel.IsVisible" /> 路径，纯视图状态。</para>
    /// </summary>
    public string? HideFramesOnOpen { get; init; }

    /// <summary>
    ///     截图专用：打开后把帧列表摆成"正在拖拽"的样子，格式 <c>from:insert</c>（0 基），
    ///     <b>不会真的移动帧</b>。仅用于拍插入指示线。
    /// </summary>
    public string? DragPreviewOnOpen { get; init; }

    /// <summary>
    ///     截图专用：打开后展开帧列表右键菜单，并把<b>弹出层本身</b>渲染到这个路径。
    ///     <para>弹出层是独立的 <c>PopupRoot</c>，主窗口的 <c>RenderTargetBitmap</c> 拍不到它。</para>
    /// </summary>
    public string? FrameMenuShotOnOpen { get; init; }

    /// <summary>解析 <c>x,y</c>（InvariantCulture）。</summary>
    private static bool TryParsePair(string text, out double x, out double y) {
        x = 0;
        y = 0;
        var comma = text.IndexOf(',');
        if (comma <= 0) return false;
        return double.TryParse(text.AsSpan(0, comma), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out x) &&
               double.TryParse(text.AsSpan(comma + 1), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out y);
    }

    /// <summary>
    ///     截图辅助：固定参考线。格式 <c>h:y,v:x</c>（屏幕坐标），<c>hr:y,vr:x</c> 直接按角色坐标。
    ///     <para>必须在视口确定之后调用，否则按屏幕换算出来的角色坐标会偏。</para>
    /// </summary>
    private void ApplyGuides(string spec) {
        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var separator = token.IndexOf(':');
            if (separator <= 0 || !double.TryParse(token.AsSpan(separator + 1),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out var value)) {
                continue;
            }

            var axis = token[..separator];
            if (axis.Equals("h", StringComparison.OrdinalIgnoreCase)) {
                FrameCanvas.AddHorizontalGuide(FrameCanvas.ScreenToFrame(new Point(0, value)).Y);
            } else if (axis.Equals("v", StringComparison.OrdinalIgnoreCase)) {
                FrameCanvas.AddVerticalGuide(FrameCanvas.ScreenToFrame(new Point(value, 0)).X);
            } else if (axis.Equals("hr", StringComparison.OrdinalIgnoreCase)) {
                // 角色坐标直接用：截图要的是"正好压在图像边缘/中线上"，不能受视口换算误差影响
                FrameCanvas.AddHorizontalGuide(value);
            } else if (axis.Equals("vr", StringComparison.OrdinalIgnoreCase)) {
                FrameCanvas.AddVerticalGuide(value);
            }
        }
    }

    /// <summary>
    ///     截图辅助：制造三种帧级变更，格式 <c>replace,insertAfter,move</c>（0 基）。
    ///     <para>
    ///         替换素材用"相邻帧导出的 PNG"作输入，保证替换前后像素肉眼可辨；
    ///         三步都走 <see cref="MainWindowViewModel" /> 的公开入口（与菜单 / 工具栏同一条路径），
    ///         所以标记是按基线比出来的，不是硬塞进 UI 的。
    ///     </para>
    /// </summary>
    /// <param name="spec">三段整数：替换下标、插入位置（"在这一帧之后"）、上移的帧下标。</param>
    private async Task ApplyChangeMarksAsync(string spec) {
        var parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var replaceIndex) ||
            !int.TryParse(parts[1], out var insertAfter) ||
            !int.TryParse(parts[2], out var moveIndex)) {
            return;
        }

        // 先拍一份卡片引用：后面的插入 / 移动会重排集合，按下标取会取错对象
        var targets = _viewModel.Frames.ToArray();
        if (targets.Length < 2) return;

        // 演示结束后把选中还给 --frame 指定的那一帧：否则最后一步"插入"会把选中带走、列表跟着滚动，
        // 截图就拍不到想展示的那几张卡片了
        var selected = _viewModel.SelectedFrame;

        if (replaceIndex >= 0 && replaceIndex < targets.Length) {
            var source = (replaceIndex + 1) % targets.Length;
            var directory = Path.Combine(Path.GetTempPath(), "npkstudio-change-marks");
            var baseName = Path.GetFileNameWithoutExtension(_viewModel.SelectedEntry?.Name ?? "frame");
            _viewModel.ExportPngBatch(directory, [targets[source]]);

            var png = Path.Combine(directory, $"{baseName}_{targets[source].Frame.Index:D3}.png");
            if (File.Exists(png)) {
                _viewModel.SelectedFrame = targets[replaceIndex];
                await _viewModel.ReplaceSelectedFrameAsync(png);
            }
        }

        if (moveIndex > 0 && moveIndex < targets.Length) {
            _viewModel.SelectedFrame = targets[moveIndex];
            _viewModel.MoveSelectedFrame(-1);
        }

        if (insertAfter >= 0 && insertAfter < targets.Length) {
            _viewModel.SelectedFrame = targets[insertAfter];
            _viewModel.InsertFrameAfterSelected();
        }

        if (selected is not null) {
            _viewModel.SelectedFrame = selected;
        }

        await Task.Delay(200);
    }

    protected override void OnClosing(WindowClosingEventArgs e) {
        // 关窗口前把"按 IMG 记忆"的配置立刻落盘（防抖定时器可能来不及触发）
        _viewModel.FlushSettings();

        if (ShouldAskBeforeClose()) {
            e.Cancel = true;
            _ = AskBeforeCloseAsync();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>窗口真正关掉后停掉试听进程（ffplay）并清理临时文件。</summary>
    protected override void OnClosed(EventArgs e) {
        Loc.PropertyChanged -= OnLanguageChanged;
        _viewModel.ShutdownAudio();
        base.OnClosed(e);
    }

    /// <summary>状态栏与画布右下角气泡的缩放文字（同一个格式，"0"：四舍五入，两处不能各算各的）。</summary>
    private void UpdateZoomText() {
        var zoom = Loc.F("status.zoom", (FrameCanvas.Zoom * 100).ToString("0"));
        ZoomLabel.Text = zoom;
        _viewModel.ZoomText = zoom;
    }

    /// <summary>状态栏鼠标坐标：按当前"坐标基准"换算（Image = 画布左上角为 0,0），与标尺、参考线标签一致。</summary>
    private void UpdatePointerText() {
        _viewModel.MousePositionText = FrameCanvas.PointerDisplayPosition is { } pointer
            ? Loc.F("status.pointer", (int)Math.Round(pointer.X), (int)Math.Round(pointer.Y))
            : string.Empty;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        UpdateZoomText();
        UpdatePointerText();
    }

    /// <summary>
    ///     是否需要弹「保存 / 不保存 / 取消」。
    ///     <para>
    ///         <b>只在真正的桌面生命周期里弹</b>：无头测试宿主没有 <c>IClassicDesktopStyleApplicationLifetime</c>，
    ///         自动化参数（<c>--export-png</c> 等）则是无人值守流程——两种情况都必须直接关，
    ///         否则会卡在一个没人点的对话框上。
    ///     </para>
    /// </summary>
    private bool ShouldAskBeforeClose() =>
        _viewModel.IsDirty
        && !_suppressDirtyPrompt
        && !IsAutomationMode
        && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    /// <summary>弹确认框：保存成功或不保存 → 关窗；取消 / 保存失败 → 留在原地。</summary>
    private async Task AskBeforeCloseAsync() {
        if (_closePromptOpen) return;

        _closePromptOpen = true;
        try {
            var choice = await SaveChangesDialog.AskAsync(this, _viewModel.FileName);
            switch (choice) {
                case SaveChangesChoice.Save:
                    if (!await SaveForCloseAsync()) return;
                    break;
                case SaveChangesChoice.Discard:
                    break;
                default:
                    return;
            }

            _suppressDirtyPrompt = true;
            Close();
        } catch (Exception ex) {
            // 关窗流程是 fire-and-forget：异常没人接会变成 UnobservedTaskException。
            _viewModel.Status = Loc.F("status.saveFailed", ex.Message);
        } finally {
            _closePromptOpen = false;
        }
    }

    /// <summary>
    ///     关窗前保存：文件夹模式直接写回原 <c>.img</c>；NPK 模式必须让用户选一个输出文件。
    /// </summary>
    /// <returns>保存是否成功（失败时不要关窗，改动还在内存里）。</returns>
    private async Task<bool> SaveForCloseAsync() {
        if (_viewModel.IsFolderMode) {
            await _viewModel.SaveAsync();
            return !_viewModel.IsDirty;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = Loc["dialog.saveAs.title"],
            DefaultExtension = "npk",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_viewModel.FileName),
            FileTypeChoices = [new FilePickerFileType(Loc["dialog.saveAs.filter"]) { Patterns = ["*.npk", "*.NPK"] }]
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) {
            return false;
        }

        await _viewModel.SaveAsync(path);
        return !_viewModel.IsDirty;
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        // 子控件（ComboBox / ToggleButton）已经处理过的键不再拦截，避免空格同时触发下拉与播放
        if (e.Handled) {
            base.OnKeyDown(e);
            return;
        }

        // 焦点在输入框时不抢键：否则搜索 "walk and run" 打空格会触发播放
        if (e.Source is TextBox) {
            base.OnKeyDown(e);
            return;
        }

        // 主修饰键见 PlatformShortcuts：macOS 只认 ⌘，其他平台认 Ctrl；修饰键精确匹配。
        // 下面注释里的 ⌘ 在 Windows / Linux 上就是 Ctrl。
        var mods = e.KeyModifiers;
        switch (e.Key) {
            case Key.R when PlatformShortcuts.Is(mods):
                // ⌘R：显示 / 隐藏标尺（Photoshop 习惯）
                _viewModel.IsRulersVisible = !_viewModel.IsRulersVisible;
                e.Handled = true;
                return;
            case Key.OemSemicolon when PlatformShortcuts.Is(mods):
                // ⌘;：显示 / 隐藏参考线（数据保留）
                _viewModel.IsGuidesVisible = !_viewModel.IsGuidesVisible;
                e.Handled = true;
                return;
            case Key.H when PlatformShortcuts.Is(mods, shift: true):
                // ⇧⌘H：在视图中心加一条水平参考线（与右键菜单同一入口）
                FrameCanvas.AddHorizontalGuideAtViewCenter();
                e.Handled = true;
                return;
            case Key.H when mods == KeyModifiers.None:
                // H：切换选中帧的可见性（调试用，纯视图状态，不改文件、不置脏）
                _viewModel.ToggleSelectedFramesVisibility();
                e.Handled = true;
                return;
            case Key.V when PlatformShortcuts.Is(mods, shift: true):
                // ⇧⌘V：在视图中心加一条垂直参考线
                FrameCanvas.AddVerticalGuideAtViewCenter();
                e.Handled = true;
                return;
            case Key.D1 when PlatformShortcuts.Is(mods, shift: true):
                ToggleLeftPanel();
                e.Handled = true;
                return;
            case Key.D2 when PlatformShortcuts.Is(mods, shift: true):
                ToggleRightPanel();
                e.Handled = true;
                return;
            case Key.Space:
                // 音效条目：空格试听 / 停止（和"访达"里按空格预览一样）；其余照旧播放帧动画
                if (_viewModel.IsAudioEntry) {
                    _viewModel.ToggleAudio();
                } else {
                    _viewModel.TogglePlay();
                }

                e.Handled = true;
                return;
            case Key.Left:
                _viewModel.StepFrame(-1);
                e.Handled = true;
                return;
            case Key.Right:
                _viewModel.StepFrame(1);
                e.Handled = true;
                return;
            case var key when PlatformShortcuts.IsDeleteKey(key):
                // Delete（Mac 上是 delete 键 / fn+delete）。焦点在输入框时上面已经放行（输入框里是编辑文字）
                _viewModel.DeleteSelectedFrames();
                e.Handled = true;
                return;
            case Key.Z when PlatformShortcuts.Is(mods):
                // ⌘Z 撤销
                _viewModel.UndoEdit();
                e.Handled = true;
                return;
            case Key.Z when PlatformShortcuts.Is(mods, shift: true):
                // ⇧⌘Z 重做
                _viewModel.RedoEdit();
                e.Handled = true;
                return;
            case Key.Y when PlatformShortcuts.AllowsRedoWithY && PlatformShortcuts.Is(mods):
                // Ctrl+Y 重做：只在非 Mac 平台保留（Windows 习惯）
                _viewModel.RedoEdit();
                e.Handled = true;
                return;
            case Key.O when PlatformShortcuts.Is(mods):
                // ⌘O 打开
                _ = PickFileAsync();
                e.Handled = true;
                return;
            case Key.S when PlatformShortcuts.Is(mods):
                // ⌘S 保存：与工具栏"另存为 NPK / 写回 .img"同一入口（不可保存时入口自己会返回）
                OnSaveAs(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.E when PlatformShortcuts.Is(mods):
                // ⌘E 导出 PNG：与工具栏按钮同一入口（没有帧时入口自己会返回）
                OnExportPng(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            case Key.OemComma when PlatformShortcuts.Is(mods):
                // ⌘, 设置（macOS 应用菜单也带这个快捷键；重复触发时 OpenSettings 只会把已开的窗口提前）
                OpenSettings();
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    private static double RateMultiplierAt(int index) => index switch {
        0 => 0.25,
        1 => 0.5,
        2 => 0.75,
        3 => 1.0,
        4 => 1.25,
        5 => 1.5,
        6 => 2.0,
        7 => 3.0,
        _ => 4.0
    };

    private void OnTogglePlay(object? sender, RoutedEventArgs e) => _viewModel.TogglePlay();



    private void OnPrevFrame(object? sender, RoutedEventArgs e) => _viewModel.StepFrame(-1);

    private void OnNextFrame(object? sender, RoutedEventArgs e) => _viewModel.StepFrame(1);

    private void OnFirstFrame(object? sender, RoutedEventArgs e) {
        if (_viewModel.Frames.Count == 0) return;
        _viewModel.SelectedFrame = _viewModel.Frames[0];
    }

    // 帧检查器的结构操作（插入 / 删除 / 上移 / 下移）统一转发给 VM，快捷键 Delete 见 OnKeyDown
    private void OnInsertFrame(object? sender, RoutedEventArgs e) => _viewModel.InsertFrameAfterSelected();

    private void OnDeleteFrame(object? sender, RoutedEventArgs e) => _viewModel.DeleteSelectedFrames();

    private void OnMoveFrameUp(object? sender, RoutedEventArgs e) => _viewModel.MoveSelectedFrame(-1);

    private void OnMoveFrameDown(object? sender, RoutedEventArgs e) => _viewModel.MoveSelectedFrame(1);

    private void OnUndo(object? sender, RoutedEventArgs e) => _viewModel.UndoEdit();

    private void OnRedo(object? sender, RoutedEventArgs e) => _viewModel.RedoEdit();

    private void OnTrimFrames(object? sender, RoutedEventArgs e) => _viewModel.TrimSelectedFrames();

    private void OnLastFrame(object? sender, RoutedEventArgs e) {
        if (_viewModel.Frames.Count == 0) return;
        _viewModel.SelectedFrame = _viewModel.Frames[^1];
    }

    private void ApplyTitleBarTheme() {
        if (!OperatingSystem.IsWindows()) return;

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        var dark = ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0) {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        }
    }

    private void OnToggleTheme(object? sender, RoutedEventArgs e) {
        var app = Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant =
            app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        ApplyTitleBarTheme();
    }

    // ── 设置 / 关于 ───────────────────────────────────────────────────────────

    private SettingsWindow? _settingsWindow;

    /// <summary>
    ///     打开设置窗口（macOS 应用菜单「设置… ⌘,」、工具栏按钮、⌘, / Ctrl+, 都走这里）。
    ///     <para>已经开着就提到前面，不开第二个（菜单快捷键和窗口按键可能同时触发）。</para>
    /// </summary>
    internal void OpenSettings() {
        if (_settingsWindow is { } open) {
            open.Activate();
            return;
        }

        var window = new SettingsWindow(new SettingsViewModel(_viewModel.SavedLanguage, _viewModel.ApplyLanguagePreference));
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Show(this);
    }

    /// <summary>打开「关于 NPKStudio」。</summary>
    internal void OpenAbout() => _ = new AboutWindow().ShowDialog(this);

    private void OnOpenSettings(object? sender, RoutedEventArgs e) => OpenSettings();

    private async void OnOpenFile(object? sender, RoutedEventArgs e) => await PickFileAsync();

    /// <summary>
    ///     打开"NPK 源目录"。Avalonia 的文件选择器不能同时选文件与目录，
    ///     所以文件夹是独立入口（拖放则两种都支持，见 <see cref="OnDrop" />）。
    /// </summary>
    private async void OnOpenFolder(object? sender, RoutedEventArgs e) {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = Loc["dialog.openFolder.title"],
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) {
            await _viewModel.OpenAsync(path);
        }
    }

    // 背景档位统一走 VM：叠加预览要把背景色烘进预合成图，控件不能是唯一真相源
    private void OnBackgroundChecker(object? sender, RoutedEventArgs e) =>
        _viewModel.Backdrop = Controls.CanvasBackground.Checker;

    private void OnBackgroundCheckerLight(object? sender, RoutedEventArgs e) =>
        _viewModel.Backdrop = Controls.CanvasBackground.CheckerLight;

    private void OnBackgroundCheckerDark(object? sender, RoutedEventArgs e) =>
        _viewModel.Backdrop = Controls.CanvasBackground.CheckerDark;

    private void OnBackgroundBlack(object? sender, RoutedEventArgs e) =>
        _viewModel.Backdrop = Controls.CanvasBackground.Black;

    private void OnBackgroundDark(object? sender, RoutedEventArgs e) =>
        _viewModel.Backdrop = Controls.CanvasBackground.Dark;

    private void OnBackgroundLight(object? sender, RoutedEventArgs e) =>
        _viewModel.Backdrop = Controls.CanvasBackground.Light;

    private void OnBackgroundGreen(object? sender, RoutedEventArgs e) =>
        _viewModel.Backdrop = Controls.CanvasBackground.Green;

    // ==================== 三栏折叠 / 分隔条复位 ====================

    private void OnToggleLeftPanel(object? sender, RoutedEventArgs e) => ToggleLeftPanel();

    private void OnToggleRightPanel(object? sender, RoutedEventArgs e) => ToggleRightPanel();

    /// <summary>左栏折叠 / 展开（按钮与 ⇧⌘1 共用，Windows 为 Ctrl+Shift+1）。</summary>
    private void ToggleLeftPanel() {
        var column = ContentGrid.ColumnDefinitions[0];
        if (_viewModel.IsLeftCollapsed) {
            SetPanelWidth(column, LeftPanel, _leftExpandedWidth);
            _viewModel.IsLeftCollapsed = false;
            return;
        }

        // 记住折叠前的实际宽度（可能被分隔条拖过），展开时按它还原
        _leftExpandedWidth = Math.Max(column.Width.Value, CollapsedPanelWidth);
        SetPanelWidth(column, LeftPanel, CollapsedPanelWidth);
        _viewModel.IsLeftCollapsed = true;
    }

    /// <summary>右栏折叠 / 展开（按钮与 ⇧⌘2 共用，Windows 为 Ctrl+Shift+2）。</summary>
    private void ToggleRightPanel() {
        var column = ContentGrid.ColumnDefinitions[4];
        if (_viewModel.IsRightCollapsed) {
            SetPanelWidth(column, RightPanel, _rightExpandedWidth);
            _viewModel.IsRightCollapsed = false;
            return;
        }

        _rightExpandedWidth = Math.Max(column.Width.Value, CollapsedPanelWidth);
        SetPanelWidth(column, RightPanel, CollapsedPanelWidth);
        _viewModel.IsRightCollapsed = true;
    }

    /// <summary>双击分隔条：左栏回到 280。</summary>
    private void ResetLeftPanelWidth() {
        _leftExpandedWidth = DefaultLeftPanelWidth;
        SetPanelWidth(ContentGrid.ColumnDefinitions[0], LeftPanel, DefaultLeftPanelWidth);
        _viewModel.IsLeftCollapsed = false;
    }

    /// <summary>双击分隔条：右栏回到 320。</summary>
    private void ResetRightPanelWidth() {
        _rightExpandedWidth = DefaultRightPanelWidth;
        SetPanelWidth(ContentGrid.ColumnDefinitions[4], RightPanel, DefaultRightPanelWidth);
        _viewModel.IsRightCollapsed = false;
    }

    /// <summary>
    ///     分隔条按下：先摘掉宽度过渡（拖拽必须跟手），再识别双击复位。
    ///     <para>走隧道阶段，双击的第二下不会先被 <see cref="GridSplitter" /> 的拖拽逻辑吃掉。</para>
    /// </summary>
    private void OnLeftSplitterPressed(object? sender, PointerPressedEventArgs e) {
        // 折叠态下按分隔条 = 要调整宽度：先把面板展开，否则 40px 竖条会被拖成一条宽"空条"
        if (_viewModel.IsLeftCollapsed) {
            ToggleLeftPanel();
        }

        LeftPanel.Transitions = null;
        if (e.ClickCount != 2) return;

        ResetLeftPanelWidth();
        e.Handled = true;
    }

    private void OnRightSplitterPressed(object? sender, PointerPressedEventArgs e) {
        if (_viewModel.IsRightCollapsed) {
            ToggleRightPanel();
        }

        RightPanel.Transitions = null;
        if (e.ClickCount != 2) return;

        ResetRightPanelWidth();
        e.Handled = true;
    }

    private void OnFitToView(object? sender, RoutedEventArgs e) => FrameCanvas.FitToView();

    private void OnZoom100(object? sender, RoutedEventArgs e) => FrameCanvas.ResetZoom();

    /// <summary>右键菜单：清除全部固定参考线。</summary>
    private void OnClearGuides(object? sender, RoutedEventArgs e) => FrameCanvas.ClearGuides();

    /// <summary>右键帧列表：隐藏选区（多选时全部）。</summary>
    private void OnHideSelectedFrames(object? sender, RoutedEventArgs e) => _viewModel.HideSelectedFrames();

    /// <summary>右键帧列表：把选区恢复为可见。</summary>
    private void OnShowSelectedFrames(object? sender, RoutedEventArgs e) => _viewModel.ShowSelectedFrames();

    /// <summary>右键帧列表：全部显示（没有隐藏帧时菜单项置灰）。</summary>
    private void OnShowAllFrames(object? sender, RoutedEventArgs e) => _viewModel.ShowAllFrames();

    /// <summary>右键菜单 / ⇧⌘H（Windows 为 Ctrl+Shift+H）：在视图中心加一条水平参考线。</summary>
    private void OnAddHorizontalGuide(object? sender, RoutedEventArgs e) =>
        FrameCanvas.AddHorizontalGuideAtViewCenter();

    /// <summary>右键菜单 / ⇧⌘V（Windows 为 Ctrl+Shift+V）：在视图中心加一条垂直参考线。</summary>
    private void OnAddVerticalGuide(object? sender, RoutedEventArgs e) =>
        FrameCanvas.AddVerticalGuideAtViewCenter();

    /// <summary>
    ///     右键菜单「坐标基准 → 图像坐标」。
    ///     <para>
    ///         菜单项是 CheckBox 语义，点已勾选项会被自己取消掉；这里在 Click 里把它按回选中，
    ///         保证「图像 / 角色」永远是二选一。
    ///     </para>
    /// </summary>
    private void OnImageOriginPicked(object? sender, RoutedEventArgs e) {
        _viewModel.IsImageOrigin = true;
        if (sender is MenuItem item) item.IsChecked = true;
    }

    /// <summary>右键菜单「坐标基准 → 角色坐标」（理由同上）。</summary>
    private void OnRoleOriginPicked(object? sender, RoutedEventArgs e) {
        _viewModel.IsRoleOrigin = true;
        if (sender is MenuItem item) item.IsChecked = true;
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e) {
        if (!_viewModel.CanSave) return;

        // 文件夹模式：另存 = 写回原 .img（原子替换），不弹文件选择器
        if (_viewModel.IsFolderMode) {
            await _viewModel.SaveAsync();
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = Loc["dialog.saveAs.title"],
            DefaultExtension = "npk",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_viewModel.FileName),
            FileTypeChoices = [new FilePickerFileType(Loc["dialog.saveAs.filter"]) { Patterns = ["*.npk", "*.NPK"] }]
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) {
            await _viewModel.SaveAsync(path);
        }
    }

    /// <summary>
    ///     文件夹模式专用：把当前源目录（或单个 img）打包成 NPK，<b>不需要清单</b>。
    ///     <para>
    ///         包内目录由"用户最终选定的输出文件名"推断：把名字起成正确的 NPK 名
    ///         （如 <c>sprite_character_priest_effect</c>），包内路径就是对的。
    ///         需要精确路径时走「资源清单」→「用资源清单导出…」。
    ///     </para>
    /// </summary>
    /// <summary>
    ///     「导出 NPK」：包内路径来自<b>已记住的资源清单</b>；第一次用（还没记住清单）时问一次，之后不再问。
    ///     <para>
    ///         <c>.img</c> 自身不记录在 NPK 里的路径，所以没有清单就只能按输出文件名推断，
    ///         而实测推断只有 48.6% 正确——因此宁可多问这一次。
    ///     </para>
    /// </summary>
    private async void OnExportNpk(object? sender, RoutedEventArgs e) {
        var folder = _viewModel.Folder;
        if (folder is null) return;

        var manifestPath = _viewModel.ManifestPath;
        if (string.IsNullOrEmpty(manifestPath)) {
            manifestPath = await PickManifestAsync();
            if (string.IsNullOrEmpty(manifestPath)) return;

            _viewModel.Status = _viewModel.SetManifest(manifestPath);
        }

        var outputPath = await PickNpkOutputAsync(folder.Name);
        if (string.IsNullOrEmpty(outputPath)) return;

        await _viewModel.ExportFolderToNpkAsync(folder, manifestPath, outputPath);
    }

    /// <summary>「资源清单 → 设置资源清单…」：记住一个清单，以后导出自动使用。</summary>
    private async void OnChooseManifest(object? sender, RoutedEventArgs e) {
        var path = await PickManifestAsync();
        if (string.IsNullOrEmpty(path)) return;

        _viewModel.Status = _viewModel.SetManifest(path);
    }

    /// <summary>「资源清单 → 清除已记住的资源清单」。</summary>
    private void OnClearManifest(object? sender, RoutedEventArgs e) =>
        _viewModel.Status = _viewModel.SetManifest(null);

    /// <summary>弹"选择资源清单"对话框；取消时返回 <c>null</c>。</summary>
    private async Task<string?> PickManifestAsync() {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = Loc["dialog.manifest.title"],
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc["dialog.manifest.filter"]) { Patterns = ["*.txt"] }]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>
    ///     「资源清单 → 用资源清单导出…」：先选清单（精确路径），再选输出文件。
    /// </summary>
    private async void OnExportNpkWithManifest(object? sender, RoutedEventArgs e) {
        var folder = _viewModel.Folder;
        if (folder is null) return;

        var manifestPath = await PickManifestAsync();
        if (string.IsNullOrEmpty(manifestPath)) return;

        // 顺手记住：用户既然挑了它，通常就是以后要一直用的那份。
        _viewModel.Status = _viewModel.SetManifest(manifestPath);

        var outputPath = await PickNpkOutputAsync(folder.Name);
        if (string.IsNullOrEmpty(outputPath)) return;

        await _viewModel.ExportFolderToNpkAsync(folder, manifestPath, outputPath);
    }

    /// <summary>
    ///     「资源清单 → 生成资源清单…」：扫描一个装满 NPK 的目录（典型是客户端 <c>ImagePacks2</c>），
    ///     把包内条目路径写成清单txt——这就是"清单从哪来"的答案：<b>不需要 PVF</b>。
    /// </summary>
    private async void OnGenerateManifest(object? sender, RoutedEventArgs e) {
        var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = Loc["dialog.manifestScan.title"],
            AllowMultiple = false
        });

        var npkDirectory = dirs.Count > 0 ? dirs[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(npkDirectory)) return;

        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = Loc["dialog.manifestSave.title"],
            DefaultExtension = "txt",
            SuggestedFileName = "missing_img_details",
            FileTypeChoices = [new FilePickerFileType(Loc["dialog.manifest.filter"]) { Patterns = ["*.txt"] }]
        });

        var outputPath = target?.TryGetLocalPath();
        if (string.IsNullOrEmpty(outputPath)) return;

        await _viewModel.GenerateManifestAsync(npkDirectory, outputPath);
    }

    /// <summary>弹"另存 NPK"对话框；取消时返回 <c>null</c>。</summary>
    private async Task<string?> PickNpkOutputAsync(string suggestedName) {
        var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = Loc["dialog.exportNpk.title"],
            DefaultExtension = "npk",
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(Loc["dialog.exportNpk.filter"]) { Patterns = ["*.npk", "*.NPK"] }]
        });

        return target?.TryGetLocalPath();
    }

    private async void OnReplaceFrame(object? sender, RoutedEventArgs e) {
        if (!_viewModel.HasFrames) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = Loc["dialog.replace.title"],
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc["dialog.replace.filter"]) { Patterns = ["*.png", "*.bmp", "*.jpg", "*.jpeg"] }]
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path)) {
            await _viewModel.ReplaceSelectedFrameAsync(path);
        }
    }

    // ── 音效条目 ───────────────────────────────────────────────────────────────

    private void OnToggleAudio(object? sender, RoutedEventArgs e) => _viewModel.ToggleAudio();

    /// <summary>把当前音效另存为一个 .ogg 文件（原样字节；替换过还没保存的就是替换后的内容）。</summary>
    private async void OnExportAudio(object? sender, RoutedEventArgs e) {
        if (!_viewModel.IsAudioEntry) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = Loc["dialog.exportAudio.title"],
            DefaultExtension = "ogg",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_viewModel.SelectedEntry?.Name ?? "sound"),
            FileTypeChoices = [new FilePickerFileType(Loc["dialog.audio.filterOgg"]) { Patterns = ["*.ogg"] }]
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) {
            await _viewModel.ExportCurrentAudioToFileAsync(path);
        }
    }

    /// <summary>把包里所有音效按包内路径导出到一个文件夹（社区整包语音拆包用）。</summary>
    private async void OnExportAllAudio(object? sender, RoutedEventArgs e) {
        if (!_viewModel.HasArchive) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = Loc["dialog.exportAudioAll.title"],
            AllowMultiple = false
        });

        var directory = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(directory)) {
            await _viewModel.ExportAudioAsync(directory, currentOnly: false);
        }
    }

    /// <summary>选一个音频文件替换当前音效（按原采样率 / 声道转成 Ogg Vorbis；保存后写进 NPK）。</summary>
    private async void OnReplaceAudio(object? sender, RoutedEventArgs e) {
        if (!_viewModel.CanReplaceAudio) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = Loc["dialog.replaceAudio.title"],
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType(Loc["dialog.audio.filterAudio"]) {
                    Patterns = ["*.ogg", "*.wav", "*.mp3", "*.flac", "*.m4a", "*.aac", "*.aif", "*.aiff", "*.opus"]
                },
                FilePickerFileTypes.All
            ]
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path)) {
            await _viewModel.ReplaceCurrentAudioAsync(path);
        }
    }

    private async void OnExportPng(object? sender, RoutedEventArgs e) {
        if (!_viewModel.HasFrames) return;

        // 多选 → 选一个目录批量导出（每帧一个文件）；单选 → 保持原来的"另存单文件"
        if (_viewModel.SelectedFrames.Count > 1) {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
                Title = Loc["dialog.exportPngBatch.title"],
                AllowMultiple = false
            });

            var directory = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(directory)) {
                _viewModel.ExportPngBatch(directory, _viewModel.SelectedFrames.ToList());
            }

            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = Loc["dialog.exportPng.title"],
            DefaultExtension = "png",
            SuggestedFileName = _viewModel.SelectedFrame?.IndexText.TrimStart('#') ?? "frame",
            FileTypeChoices = [new FilePickerFileType(Loc["dialog.exportPng.filter"]) { Patterns = ["*.png"] }]
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) {
            _viewModel.ExportCurrentPng(path);
        }
    }

    /// <summary>
    ///     导出 GIF：多选时只导选中帧，否则导全部帧；解码与编码在后台线程，帧数多也不会卡界面。
    /// </summary>
    private async void OnExportGif(object? sender, RoutedEventArgs e) {
        if (!_viewModel.HasFrames) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = Loc["dialog.exportGif.title"],
            DefaultExtension = "gif",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_viewModel.FileName),
            FileTypeChoices = [new FilePickerFileType(Loc["dialog.exportGif.filter"]) { Patterns = ["*.gif"] }]
        });

        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) {
            await _viewModel.ExportGifAsync(path);
        }
    }

    private async Task PickFileAsync() {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = Loc["dialog.open.title"],
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType(Loc["dialog.open.filter"]) {
                    Patterns = ["*.npk", "*.NPK", "*.img", "*.IMG"]
                }
            ]
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) {
            await _viewModel.OpenAsync(path);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e) {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e) {
        var files = e.DataTransfer.TryGetFiles();
        var path = files?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) {
            await _viewModel.OpenAsync(path);
        }
    }
}
