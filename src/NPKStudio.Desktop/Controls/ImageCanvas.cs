using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NPKStudio.Core.Imaging;
using NPKStudio.Desktop.Controls.Rendering;
using NPKStudio.Desktop.Input;

namespace NPKStudio.Desktop.Controls;

/// <summary>
///     帧画布：把 <see cref="RgbaImage" /> 画出来，并叠加标尺 / 参考线 / 十字线。
///     <para>只负责：注册依赖属性、组装渲染器、分发指针事件、对外暴露只读状态；绘制在 <c>Controls/Rendering</c>。</para>
/// </summary>
public sealed class ImageCanvas : Control {
    /// <summary>标尺厚度（屏幕像素）。顶部与左侧各一条。</summary>
    public const double RulerSize = 20;

    public static readonly StyledProperty<RgbaImage?> ImageProperty = AvaloniaProperty.Register<ImageCanvas, RgbaImage?>(nameof(Image));

    public static readonly StyledProperty<IBrush?> CheckerBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(CheckerBrush));

    /// <summary>亮棋盘格画刷（#FFFFFF / #E6E9EE）。</summary>
    public static readonly StyledProperty<IBrush?> CheckerLightBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(CheckerLightBrush));

    /// <summary>暗棋盘格画刷（#808080 / #6B6B6B）。</summary>
    public static readonly StyledProperty<IBrush?> CheckerDarkBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(CheckerDarkBrush));

    public static readonly StyledProperty<IBrush?> FrameBorderProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(FrameBorder));

    /// <summary>帧外区域的底色（与帧内背景形成对比，界定图像边界）。</summary>
    public static readonly StyledProperty<IBrush?> OuterBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(OuterBrush));

    /// <summary>帧内背景模式。</summary>
    public static readonly StyledProperty<CanvasBackground> BackgroundProperty = AvaloniaProperty.Register<ImageCanvas, CanvasBackground>(nameof(Background));

    /// <summary>是否显示标尺（默认显示）。</summary>
    public static readonly StyledProperty<bool> IsRulersVisibleProperty = AvaloniaProperty.Register<ImageCanvas, bool>(nameof(IsRulersVisible), defaultValue: true);

    /// <summary>是否显示参考线（默认显示；隐藏只影响绘制，数据仍保留）。</summary>
    public static readonly StyledProperty<bool> IsGuidesVisibleProperty = AvaloniaProperty.Register<ImageCanvas, bool>(nameof(IsGuidesVisible), defaultValue: true);

    /// <summary>是否在画布上/左边缘显示参考线坐标标签（默认显示）。</summary>
    public static readonly StyledProperty<bool> ShowGuideLabelsProperty = AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ShowGuideLabels), defaultValue: true);

    /// <summary>像素对齐（默认开启）：缩放吸附整数档位，主图按最近邻绘制。</summary>
    public static readonly StyledProperty<bool> IsPixelAlignEnabledProperty = AvaloniaProperty.Register<ImageCanvas, bool>(nameof(IsPixelAlignEnabled), defaultValue: true);

    /// <summary>标尺底色。</summary>
    public static readonly StyledProperty<IBrush?> RulerBackgroundProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(RulerBackground));

    /// <summary>标尺分隔线。</summary>
    public static readonly StyledProperty<IBrush?> RulerBorderBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(RulerBorderBrush));

    /// <summary>刻度线。</summary>
    public static readonly StyledProperty<IBrush?> RulerTickBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(RulerTickBrush));

    /// <summary>刻度数字。</summary>
    public static readonly StyledProperty<IBrush?> RulerTextBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(RulerTextBrush));

    /// <summary>鼠标十字辅助线（主题色，100% 不透明）。</summary>
    public static readonly StyledProperty<IBrush?> CrosshairBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(CrosshairBrush));

    /// <summary>参考线（主题橙）。</summary>
    public static readonly StyledProperty<IBrush?> GuideBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(GuideBrush));

    /// <summary>参考线坐标标签的底色（参考线色的 85% 不透明版）。</summary>
    public static readonly StyledProperty<IBrush?> GuideLabelBackgroundProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(GuideLabelBackground));

    /// <summary>参考线坐标标签的文字色（深色，压在高饱和标签底上）。</summary>
    public static readonly StyledProperty<IBrush?> GuideLabelTextBrushProperty = AvaloniaProperty.Register<ImageCanvas, IBrush?>(nameof(GuideLabelTextBrush));

    /// <summary>
    ///     画布坐标系尺寸（整个 IMG 所有帧的<b>联合包围盒</b>）。
    ///     <para>只有它变化（= 切换 IMG）才重新 <c>Fit</c>；播放时切换帧不会重排视口。</para>
    /// </summary>
    public static readonly StyledProperty<Size> CanvasSizeProperty = AvaloniaProperty.Register<ImageCanvas, Size>(nameof(CanvasSize));

    /// <summary>当前帧左上角在画布坐标系中的位置（画布坐标 = 角色坐标 - 包围盒左上角）。</summary>
    public static readonly StyledProperty<Point> FrameOffsetProperty = AvaloniaProperty.Register<ImageCanvas, Point>(nameof(FrameOffset));

    /// <summary>画布坐标原点对应的<b>角色坐标</b>（= 联合包围盒左上角）。</summary>
    public static readonly StyledProperty<Point> CanvasOriginProperty = AvaloniaProperty.Register<ImageCanvas, Point>(nameof(CanvasOrigin));

    /// <summary>标尺 / 游标 / 参考线标签的坐标基准（默认 <see cref="RulerOriginMode.Image" />）。</summary>
    public static readonly StyledProperty<RulerOriginMode> OriginModeProperty = AvaloniaProperty.Register<ImageCanvas, RulerOriginMode>(nameof(OriginMode), defaultValue: RulerOriginMode.Image);

    private readonly ViewportTransform _transform = new();
    private readonly PixelZoom _pixelZoom = new();
    private readonly ZoomInput _zoomInput = new();
    private readonly GuideStore _guides = new();
    private readonly CanvasBackgroundRenderer _backgroundRenderer = new();
    private readonly FrameRenderer _frameRenderer = new();
    private readonly RulerRenderer _rulerRenderer = new();
    private readonly GuideRenderer _guideRenderer;
    private readonly CrosshairRenderer _crosshairRenderer = new();
    private readonly GuideInteraction _guideInteraction = new();

    private bool _panning;
    private Point _panOrigin;

    // 鼠标位置：null = 指针不在画布上（十字线、游标、状态栏坐标全部消失）
    private Point? _pointerScreen;
    private int _reportedFrameX;
    private int _reportedFrameY;
    private bool _hasReportedPointer;

    /// <summary>本帧内已经排过一次重绘（合并高频 Move 的脏标记）。</summary>
    private bool _renderQueued;

    private Cursor? _cursorSizeAll;

    public ImageCanvas() {
        _guideRenderer = new GuideRenderer(_rulerRenderer);

        ClipToBounds = true;
        Focusable = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        // 背景模式与外底变化需要重绘；Image 走 OnPropertyChanged 里的位图重建路径
        AffectsRender<ImageCanvas>(BackgroundProperty, OuterBrushProperty, CheckerBrushProperty, FrameBorderProperty,
            FrameOffsetProperty, CheckerLightBrushProperty, CheckerDarkBrushProperty,
            IsRulersVisibleProperty, IsGuidesVisibleProperty, CanvasOriginProperty, OriginModeProperty,
            RulerBackgroundProperty, RulerBorderBrushProperty, RulerTickBrushProperty, RulerTextBrushProperty,
            CrosshairBrushProperty, GuideBrushProperty,
            ShowGuideLabelsProperty, GuideLabelBackgroundProperty, GuideLabelTextBrushProperty,
            IsPixelAlignEnabledProperty);

        // macOS 触控板双指捏合（Avalonia.Native 把 magnifyWithEvent: 转成这个路由事件）
        Gestures.AddPointerTouchPadGestureMagnifyHandler(this, OnTouchPadMagnify);
    }

    // ==================== 依赖属性包装（公开 API，签名与行为不变） ====================

    public RgbaImage? Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public IBrush? CheckerBrush { get => GetValue(CheckerBrushProperty); set => SetValue(CheckerBrushProperty, value); }
    public IBrush? CheckerLightBrush { get => GetValue(CheckerLightBrushProperty); set => SetValue(CheckerLightBrushProperty, value); }
    public IBrush? CheckerDarkBrush { get => GetValue(CheckerDarkBrushProperty); set => SetValue(CheckerDarkBrushProperty, value); }
    public IBrush? FrameBorder { get => GetValue(FrameBorderProperty); set => SetValue(FrameBorderProperty, value); }
    public IBrush? OuterBrush { get => GetValue(OuterBrushProperty); set => SetValue(OuterBrushProperty, value); }
    public CanvasBackground Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public bool IsRulersVisible { get => GetValue(IsRulersVisibleProperty); set => SetValue(IsRulersVisibleProperty, value); }
    public bool IsGuidesVisible { get => GetValue(IsGuidesVisibleProperty); set => SetValue(IsGuidesVisibleProperty, value); }
    public IBrush? RulerBackground { get => GetValue(RulerBackgroundProperty); set => SetValue(RulerBackgroundProperty, value); }
    public IBrush? RulerBorderBrush { get => GetValue(RulerBorderBrushProperty); set => SetValue(RulerBorderBrushProperty, value); }
    public IBrush? RulerTickBrush { get => GetValue(RulerTickBrushProperty); set => SetValue(RulerTickBrushProperty, value); }
    public IBrush? RulerTextBrush { get => GetValue(RulerTextBrushProperty); set => SetValue(RulerTextBrushProperty, value); }
    public IBrush? CrosshairBrush { get => GetValue(CrosshairBrushProperty); set => SetValue(CrosshairBrushProperty, value); }
    public IBrush? GuideBrush { get => GetValue(GuideBrushProperty); set => SetValue(GuideBrushProperty, value); }
    public bool ShowGuideLabels { get => GetValue(ShowGuideLabelsProperty); set => SetValue(ShowGuideLabelsProperty, value); }
    public bool IsPixelAlignEnabled { get => GetValue(IsPixelAlignEnabledProperty); set => SetValue(IsPixelAlignEnabledProperty, value); }
    public IBrush? GuideLabelBackground { get => GetValue(GuideLabelBackgroundProperty); set => SetValue(GuideLabelBackgroundProperty, value); }
    public IBrush? GuideLabelTextBrush { get => GetValue(GuideLabelTextBrushProperty); set => SetValue(GuideLabelTextBrushProperty, value); }
    public Size CanvasSize { get => GetValue(CanvasSizeProperty); set => SetValue(CanvasSizeProperty, value); }
    public Point FrameOffset { get => GetValue(FrameOffsetProperty); set => SetValue(FrameOffsetProperty, value); }
    public Point CanvasOrigin { get => GetValue(CanvasOriginProperty); set => SetValue(CanvasOriginProperty, value); }
    public RulerOriginMode OriginMode { get => GetValue(OriginModeProperty); set => SetValue(OriginModeProperty, value); }

    /// <summary>
    ///     显示坐标 0 对应的角色坐标（Image 基准 = 画布左上角；Role 基准 = 角色原点）。
    ///     <para>显示坐标 = 角色坐标 − 该偏移，方向与 <see cref="FrameXToScreen" /> 的屏幕映射无关。</para>
    /// </summary>
    internal Point DisplayOffset => OriginMode == RulerOriginMode.Image ? CanvasOrigin : default;

    /// <summary>角色坐标 → 当前基准下的显示坐标（仅用于读数，不参与屏幕定位）。</summary>
    internal Point ToDisplay(Point role) => new(role.X - DisplayOffset.X, role.Y - DisplayOffset.Y);

    /// <summary>水平参考线（角色坐标 Y）。</summary>
    public IReadOnlyList<double> HorizontalGuides => _guides.Horizontal;

    /// <summary>垂直参考线（角色坐标 X）。</summary>
    public IReadOnlyList<double> VerticalGuides => _guides.Vertical;

    /// <summary>参考线总数（水平 + 垂直）。</summary>
    public int GuideCount => _guides.Count;

    /// <summary>最近一次布局出来的标签矩形（标签关闭时为 0 条）；测试与诊断用。</summary>
    internal IReadOnlyList<GuideLabelBox> GuideLabelBoxes => _guideRenderer.LabelBoxes;

    /// <summary>最近一次布局时标尺游标标签占用的矩形（测试与诊断用）。</summary>
    internal IReadOnlyList<Rect> CursorLabelBoxes => _guideRenderer.CursorLabelBoxes;

    /// <summary>标签文字缓存未命中次数（测试用：坐标没变时这个数不该增长）。</summary>
    internal int GuideLabelTextCacheMisses => _guideRenderer.CacheMisses;

    /// <summary>
    ///     当前缩放下的刻度步进（帧坐标像素），取值来自 1/2/5/10/20/50/100/200/500。
    ///     <para>保证相邻数字的屏幕间距 ≥ 48px，缩放越大步进越小。</para>
    /// </summary>
    public double RulerStep => RulerRenderer.StepFor(_transform.Zoom);

    /// <summary>鼠标所在角色坐标；指针离开画布后为 <c>null</c>。</summary>
    public Point? PointerFramePosition => _pointerScreen is { } screen ? ToFrame(screen) : null;

    /// <summary>鼠标所在显示坐标（跟 <see cref="OriginMode" /> 换算）；指针离开画布后为 <c>null</c>。</summary>
    public Point? PointerDisplayPosition => _pointerScreen is { } screen ? ToDisplay(ToFrame(screen)) : null;

    /// <summary>鼠标坐标变化通知（进入 / 移动 / 离开）。</summary>
    public event EventHandler? PointerPositionChanged;

    /// <summary>参考线集合变化通知（新增 / 删除 / 清除），供状态栏与右键菜单启停使用。</summary>
    public event EventHandler? GuidesChanged;

    /// <summary>参考线开始 / 结束拖拽的通知（用于状态栏提示）。</summary>
    public event EventHandler? GuideDragChanged;

    /// <summary>当前是否正在拖拽一条参考线。</summary>
    public bool IsDraggingGuide => _guideInteraction.IsDragging;

    /// <summary>实际用于 Fit / 背景 / 边框的画布尺寸（<see cref="CanvasSize" /> 未设时退回当前帧尺寸）。</summary>
    private Size CanvasExtent => CanvasSize is { Width: > 0, Height: > 0 }
        ? CanvasSize
        : Image is { } image ? new Size(image.Width, image.Height) : default;

    /// <summary>当前缩放倍数。</summary>
    public double Zoom => _transform.Zoom;

    /// <summary>缩放变化通知（供状态栏/气泡显示）。</summary>
    public event EventHandler? ZoomChanged;

    // ==================== 视口 / 参考线公开操作 ====================

    /// <summary>令当前画布适配视口。</summary>
    public void FitToView() => ApplyFit(Bounds.Size);

    /// <summary>适配视口。开启像素对齐时向下取到最近的合法档位，宁可留一点边，也不产生非整数缩放。</summary>
    private void ApplyFit(Size viewport) {
        var extent = CanvasExtent;
        if (extent.Width <= 0 || extent.Height <= 0) return;
        var scale = _transform.ComputeFit(extent, viewport);
        if (scale <= 0) return;

        var target = IsPixelAlignEnabled ? _pixelZoom.SnapDown(scale) : scale;
        _zoomInput.ResetPinch();
        _zoomInput.ResetPendingTicks();
        _transform.SetZoom(target, new Point(viewport.Width / 2, viewport.Height / 2));
        _transform.Center(extent, viewport);
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>复位到 100%。</summary>
    public void ResetZoom() {
        var extent = CanvasExtent;
        if (extent.Width <= 0 || extent.Height <= 0) return;
        _zoomInput.ResetPinch();
        _zoomInput.ResetPendingTicks();
        _transform.Reset(extent, Bounds.Size);
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     设置到指定缩放倍数（以视口中心为锚点）。
    /// </summary>
    /// <param name="zoom">
    ///     目标倍数（会被夹到 <see cref="ViewportTransform.MinZoom" />~<see cref="ViewportTransform.MaxZoom" />；
    ///     开启像素对齐时吸附到最近的合法档位）。
    /// </param>
    public void SetZoomLevel(double zoom) {
        var extent = CanvasExtent;
        if (extent.Width <= 0 || extent.Height <= 0) return;

        var target = IsPixelAlignEnabled ? _pixelZoom.Snap(zoom) : zoom;
        _transform.SetZoom(target, new Point(Bounds.Width / 2, Bounds.Height / 2));
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>追加一条水平参考线（角色坐标 Y；开启像素对齐时吸附到整像素）。</summary>
    public void AddHorizontalGuide(double y) {
        _guides.Add(GuideAxis.Horizontal, IsPixelAlignEnabled ? Math.Round(y) : y);
        NotifyGuidesChanged();
    }

    /// <summary>追加一条垂直参考线（角色坐标 X；开启像素对齐时吸附到整像素）。</summary>
    public void AddVerticalGuide(double x) {
        _guides.Add(GuideAxis.Vertical, IsPixelAlignEnabled ? Math.Round(x) : x);
        NotifyGuidesChanged();
    }

    /// <summary>在<b>当前视图可见区域</b>的正中加一条水平参考线（右键菜单 / ⇧⌘H，Windows 为 Ctrl+Shift+H）。</summary>
    public void AddHorizontalGuideAtViewCenter() => AddHorizontalGuide(ViewCenterFramePosition.Y);

    /// <summary>在<b>当前视图可见区域</b>的正中加一条垂直参考线（右键菜单 / ⇧⌘V，Windows 为 Ctrl+Shift+V）。</summary>
    public void AddVerticalGuideAtViewCenter() => AddVerticalGuide(ViewCenterFramePosition.X);

    /// <summary>视图可见区域中心对应的角色坐标（不含标尺边带；视口尚未布局时退回控件中心）。</summary>
    public Point ViewCenterFramePosition {
        get {
            var content = ContentRect(new Rect(Bounds.Size));
            var center = content is { Width: > 0, Height: > 0 }
                ? content.Center
                : new Point(Bounds.Width / 2, Bounds.Height / 2);
            return ToFrame(center);
        }
    }

    /// <summary>清除全部参考线。</summary>
    public void ClearGuides() {
        if (_guides.IsEmpty) return;
        _guides.Clear();
        NotifyGuidesChanged();
    }

    private void NotifyGuidesChanged() {
        GuidesChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>屏幕坐标 → 角色坐标（自动化截图 / 诊断用）。</summary>
    internal Point ScreenToFrame(Point screen) => ToFrame(screen);

    /// <summary>屏幕坐标 → 当前基准下的显示坐标（测试用：验证"画布左上角 = 0"这条链）。</summary>
    internal Point ScreenToDisplay(Point screen) => ToDisplay(ToFrame(screen));

    /// <summary>模拟指针位置（仅用于自动化截图与无头测试，不产生真实输入事件）。</summary>
    /// <param name="position">控件内屏幕坐标；<c>null</c> 表示指针离开。</param>
    public void SimulatePointer(Point? position) {
        SetPointer(position);
        if (position is { } screen) {
            UpdateHover(screen);
            return;
        }

        _guideInteraction.ResetHover();
        Cursor = _guideInteraction.Cursor;
    }

    // ==================== 属性变化 / 挂载 ====================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == ImageProperty) {
            _frameRenderer.Upload(Image);
            ZoomChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }

        if (change.Property == RulerTextBrushProperty) {
            // 主题切换后旧 FormattedText 仍持着上一套画刷，必须整体失效
            _rulerRenderer.InvalidateTextCache();
        }

        if (change.Property == OriginModeProperty) {
            // 基准一变，同一位置的数字全变：两套文字缓存都失效；参考线本身不动（仍按角色坐标绘制定位）。
            _rulerRenderer.InvalidateTextCache();
            _guideRenderer.InvalidateTextCache();
            InvalidateVisual();
            PointerPositionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (change.Property == GuideLabelTextBrushProperty) {
            _guideRenderer.InvalidateTextCache();
        }

        if (change.Property == IsPixelAlignEnabledProperty) {
            // 运行中开启：把当前（可能是连续缩放留下的）倍数吸附到最近档位，画面立刻变"硬"。
            if (IsPixelAlignEnabled) {
                var snapped = _pixelZoom.Snap(_transform.Zoom);
                if (Math.Abs(snapped - _transform.Zoom) > 1e-9) {
                    _transform.SetZoom(snapped, new Point(Bounds.Width / 2, Bounds.Height / 2));
                    ZoomChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            InvalidateVisual();
            return;
        }

        if (change.Property == CanvasSizeProperty) {
            // 只有"切换 IMG"才会走到这里：重新适配视口（播放时切帧不会触发）
            var extent = CanvasExtent;
            if (extent.Width > 0 && extent.Height > 0 && Bounds.Width > 0) {
                ApplyFit(Bounds.Size);
                return;
            }

            ZoomChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) {
        base.OnSizeChanged(e);
        var extent = CanvasExtent;
        if (extent.Width > 0 && extent.Height > 0) {
            ApplyFit(e.NewSize);
        }
    }

    /// <summary>重新挂载到可视树时强制重建位图（离开可视树会释放，但 <c>Image</c> 没变不会触发属性变更）。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _frameRenderer.Reattach(Image);
        // 到这里 Avalonia 平台已初始化，原生 AvnView 类已加载，可以补上 macOS 智能缩放（只做一次）
        MacSmartZoom.EnsureInstalled(OnMacSmartZoom);
        InvalidateVisual();
    }

    /// <summary>离开可视树时释放原生位图。</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        _frameRenderer.Release();
    }

    // ==================== 绘制：只负责组装 ====================

    public override void Render(DrawingContext context) {
        base.Render(context);

        var ctx = CreateRenderContext();

        // 第一层：帧外区域（始终画，和帧内背景形成对比）
        _backgroundRenderer.DrawOuter(context, in ctx);

        // 第二 / 三 / 四层：帧内背景 → 帧图像 → 1px 边框
        if (ctx.Image is not null && _frameRenderer.HasBitmap &&
            ctx.CanvasExtent is { Width: > 0, Height: > 0 }) {
            _backgroundRenderer.DrawContent(context, in ctx);
            _frameRenderer.Draw(context, in ctx);
        }

        // 第五层：参考线 → 十字辅助线 → 参考线标签（顺序与重构前一致）
        _guideRenderer.UpdateLayout(in ctx, _guides);
        _guideRenderer.DrawLines(context, in ctx, _guides);
        _crosshairRenderer.Draw(context, in ctx);
        _guideRenderer.DrawLabels(context, in ctx);

        // 第六层：标尺与游标永远压在最上面（覆盖画布边缘 20px）
        _rulerRenderer.Draw(context, in ctx);

        _renderQueued = false;
    }

    /// <summary>把当前所有只读参数打包给渲染器（值类型，渲染路径零堆分配）。</summary>
    private CanvasRenderContext CreateRenderContext() {
        var viewport = new Rect(Bounds.Size);
        var extent = CanvasExtent;
        var zoom = _transform.Zoom;
        var offset = _transform.Offset;

        // 像素对齐时把整幅图的屏幕原点压到整数像素，避免最近邻采样出现"半像素"宽窄不一
        var drawOffset = IsPixelAlignEnabled ? new Point(Math.Round(offset.X), Math.Round(offset.Y)) : offset;

        return new CanvasRenderContext {
            Viewport = viewport, Content = ContentRect(viewport),
            CanvasRect = new Rect(drawOffset.X, drawOffset.Y, extent.Width * zoom, extent.Height * zoom),
            Zoom = zoom, Offset = offset, DrawOffset = drawOffset,
            CanvasOrigin = CanvasOrigin, DisplayOffset = DisplayOffset,
            CanvasExtent = extent, PointerScreen = _pointerScreen,
            FrameOffset = FrameOffset, Image = Image, Background = Background, OriginMode = OriginMode,
            OuterBrush = OuterBrush, CheckerBrush = CheckerBrush, CheckerLightBrush = CheckerLightBrush,
            CheckerDarkBrush = CheckerDarkBrush, FrameBorder = FrameBorder,
            RulerBackground = RulerBackground, RulerBorderBrush = RulerBorderBrush,
            RulerTickBrush = RulerTickBrush, RulerTextBrush = RulerTextBrush,
            CrosshairBrush = CrosshairBrush, GuideBrush = GuideBrush,
            GuideLabelBackground = GuideLabelBackground, GuideLabelTextBrush = GuideLabelTextBrush,
            IsRulersVisible = IsRulersVisible, IsGuidesVisible = IsGuidesVisible,
            ShowGuideLabels = ShowGuideLabels, IsPixelAlignEnabled = IsPixelAlignEnabled,
            DragAxis = _guideInteraction.DragAxis, DragIndex = _guideInteraction.DragIndex,
            HoverAxis = _guideInteraction.HoverAxis, HoverIndex = _guideInteraction.HoverIndex
        };
    }

    /// <summary>重算标签布局（测试与绘制共用）。</summary>
    internal void UpdateGuideLabelLayout() {
        var ctx = CreateRenderContext();
        _guideRenderer.UpdateLayout(in ctx, _guides);
    }

    // ==================== 坐标换算 ====================

    private Point ToFrame(Point screen) {
        var canvas = _transform.ScreenToImage(screen);
        return new Point(canvas.X + CanvasOrigin.X, canvas.Y + CanvasOrigin.Y);
    }

    internal double FrameXToScreen(double frameX) => (frameX - CanvasOrigin.X) * _transform.Zoom + _transform.Offset.X;
    internal double FrameYToScreen(double frameY) => (frameY - CanvasOrigin.Y) * _transform.Zoom + _transform.Offset.Y;

    /// <summary>标尺上某个显示坐标对应的读数（当前基准）。绘制与测试共用这一处换算。</summary>
    internal int RulerLabelFor(double displayValue, bool horizontal) =>
        RulerRenderer.LabelFor(displayValue, horizontal, RulerLabelShift);

    /// <summary>Role 基准下刻度数字要加回的画布原点；Image 基准为 0。</summary>
    private Point RulerLabelShift => OriginMode == RulerOriginMode.Role ? CanvasOrigin : default;

    private Rect ContentRect(Rect viewport) {
        if (!IsRulersVisible) return viewport;
        return new Rect(RulerSize, RulerSize,
            Math.Max(0, viewport.Width - RulerSize),
            Math.Max(0, viewport.Height - RulerSize));
    }

    // ==================== 指针交互 ====================

    /// <summary>记录指针位置（零点分配）：只在整数坐标变化时通知状态栏；重绘按帧合并。</summary>
    private void SetPointer(Point? position) {
        if (_pointerScreen == position) return;
        _pointerScreen = position;

        if (!_renderQueued) {
            _renderQueued = true;
            InvalidateVisual();
        }

        if (position is { } screen) {
            var frame = ToFrame(screen);
            var x = (int)Math.Round(frame.X);
            var y = (int)Math.Round(frame.Y);
            if (!_hasReportedPointer || x != _reportedFrameX || y != _reportedFrameY) {
                _reportedFrameX = x;
                _reportedFrameY = y;
                _hasReportedPointer = true;
                PointerPositionChanged?.Invoke(this, EventArgs.Empty);
            }
        } else if (_hasReportedPointer) {
            _hasReportedPointer = false;
            PointerPositionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);
        if (Image is null) return;

        // 主修饰键 + 滚轮缩放：macOS 是 ⌘，其他平台是 Ctrl（见 PlatformShortcuts）。
        // 缩放量按滚动量折算（一格 = 10%，见 ZoomInput）：触控板滑动是一串很小的 Delta，不能每个事件都算一整格。
        if (PlatformShortcuts.HasCommand(e.KeyModifiers)) {
            _zoomInput.ResetPinch();
            var ticks = e.Delta.Y;
            var anchor = e.GetPosition(this);
            if (IsPixelAlignEnabled) {
                // 连续缩放下 4.0 × 1.1 仍会吸附回 4.0，滚轮会"卡死"：攒够一格直接跳到上/下一档。
                var steps = _zoomInput.StepsForTicks(ticks);
                if (steps != 0) {
                    var next = _transform.Zoom;
                    for (var i = 0; i < Math.Abs(steps); i++) {
                        next = _pixelZoom.Next(next, Math.Sign(steps));
                    }

                    _transform.SetZoom(next, anchor);
                }
            } else {
                _transform.ZoomAt(anchor, ZoomInput.FactorForTicks(ticks));
            }

            InvalidateVisual();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        // 不按修饰键（或只按 Shift）的滚动 = 平移：触控板双指拖动、鼠标滚轮上下 / Shift+滚轮左右
        if ((e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None) {
            var pan = ZoomInput.PanDelta(e.Delta, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (pan == default) return;

            _transform.Pan(pan);
            // 画面动了，指针下的图像坐标也变了：强制刷新一次状态栏坐标
            var pointer = _pointerScreen ?? e.GetPosition(this);
            _pointerScreen = null;
            SetPointer(pointer);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     macOS 智能缩放（双指轻点两下）：当前就是"适配窗口"时，以指针为中心放大到 2 倍（像素对齐时至少升一档）；
    ///     否则回到适配窗口。和"预览""Safari"里放大 / 缩回的切换一致。
    /// </summary>
    internal void ToggleSmartZoom() {
        var extent = CanvasExtent;
        if (Image is null || extent.Width <= 0 || extent.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var raw = _transform.ComputeFit(extent, Bounds.Size);
        if (raw <= 0) return;

        var fit = Math.Clamp(IsPixelAlignEnabled ? _pixelZoom.SnapDown(raw) : raw,
            ViewportTransform.MinZoom, ViewportTransform.MaxZoom);
        if (!ZoomInput.IsNearFit(_transform.Zoom, fit)) {
            FitToView();
            return;
        }

        var target = ZoomInput.SmartZoomTarget(fit);
        if (IsPixelAlignEnabled) {
            target = Math.Max(_pixelZoom.Snap(target), _pixelZoom.Next(fit, 1));
        }

        _zoomInput.ResetPinch();
        _zoomInput.ResetPendingTicks();
        _transform.SetZoom(target, _pointerScreen ?? new Point(Bounds.Width / 2, Bounds.Height / 2));
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    // 指针当前停在哪个画布上（智能缩放事件不带目标控件，按指针位置分发）
    private static WeakReference<ImageCanvas>? s_hoveredCanvas;

    private static void OnMacSmartZoom() {
        if (s_hoveredCanvas is not null && s_hoveredCanvas.TryGetTarget(out var canvas)) {
            canvas.ToggleSmartZoom();
        }
    }

    /// <summary>
    ///     触控板双指捏合缩放（macOS）：不需要按修饰键，按捏合幅度连续缩放；像素对齐时吸附到最近档位。
    ///     锚点是指针位置，和 ⌘+滚轮一致。
    /// </summary>
    private void OnTouchPadMagnify(object? sender, PointerDeltaEventArgs e) {
        if (Image is null) return;

        // macOS 后端把 magnification 同时放进 Delta.X / Delta.Y；优先取 X，X 为 0 时退回 Y
        var magnification = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        if (magnification == 0) return;

        _zoomInput.ResetPendingTicks();
        var target = _zoomInput.PinchTarget(_transform.Zoom, magnification, Environment.TickCount64);
        if (IsPixelAlignEnabled) {
            target = _pixelZoom.Snap(target);
        }

        _transform.SetZoom(target, e.GetPosition(this));
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var position = point.Position;

        // 中键 / Alt+左键平移优先级最高：标尺区域也要能平移画布
        if (point.Properties.IsMiddleButtonPressed ||
            (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt))) {
            _panning = true;
            _panOrigin = position;
            e.Pointer.Capture(this);
            Cursor = CursorSizeAll;
            e.Handled = true;
            return;
        }

        var content = ContentRect(new Rect(Bounds.Size));
        var ctx = CreateRenderContext();

        if (e.ClickCount == 1 && point.Properties.IsLeftButtonPressed) {
            // 顶部标尺 → 新建水平参考线；左侧标尺 → 新建垂直参考线
            if (IsRulersVisible && position.Y < RulerSize && position.X >= RulerSize) {
                if (TryBeginGuideDrag(GuideAxis.Horizontal, newIndex: -1, position, e)) return;
            }

            if (IsRulersVisible && position.X < RulerSize && position.Y >= RulerSize) {
                if (TryBeginGuideDrag(GuideAxis.Vertical, newIndex: -1, position, e)) return;
            }

            // 画布内：抓住已有参考线
            if (IsGuidesVisible && content.Contains(position) &&
                _guideInteraction.TryHit(_guides, in ctx, position, out var axis, out var index) &&
                TryBeginGuideDrag(axis, index, position, e)) {
                return;
            }
        }

        // 双击空白处 = 适配窗口（与右键菜单「适配窗口」同一条路径）。
        // Alt+左键是平移的前置组合键，不能在这里抢；标尺区域与已固定的参考线上也不适配。
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed &&
            !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !IsInRulerBand(position) &&
            !(IsGuidesVisible && content.Contains(position) &&
              _guideInteraction.TryHit(_guides, in ctx, position, out _, out _))) {
            FitToView();
            e.Handled = true;
        }
    }

    /// <summary>开始拖动参考线。新建时立刻入表（便于绘制跟随），拖回标尺松开再删掉。</summary>
    private bool TryBeginGuideDrag(GuideAxis axis, int newIndex, Point position, PointerPressedEventArgs e) {
        if (axis == GuideAxis.None) return false;

        var ctx = CreateRenderContext();
        var cursor = _guideInteraction.BeginDrag(_guides, in ctx, axis, newIndex, position, out var added);

        Cursor = cursor;
        e.Pointer.Capture(this);
        e.Handled = true;

        if (added) {
            NotifyGuidesChanged();
        } else {
            InvalidateVisual();
        }

        GuideDragChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        var now = e.GetPosition(this);

        if (_panning) {
            _transform.Pan(now - _panOrigin);
            _panOrigin = now;
            SetPointer(now);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_guideInteraction.IsDragging) {
            var ctx = CreateRenderContext();
            _guideInteraction.MoveDrag(_guides, in ctx, now);
            SetPointer(now);
            e.Handled = true;
            return;
        }

        SetPointer(now);
        UpdateHover(now);
    }

    /// <summary>悬停光标切换：标尺 / 参考线上给对应方向的调整光标。只在状态变化时赋值。</summary>
    private void UpdateHover(Point position) {
        var ctx = CreateRenderContext();
        var cursor = _guideInteraction.UpdateHover(_guides, in ctx, position,
            out var visualChanged, out var cursorChanged);

        if (visualChanged) InvalidateVisual();
        if (cursorChanged) Cursor = cursor;
    }

    private Cursor CursorSizeAll => _cursorSizeAll ??= new Cursor(StandardCursorType.SizeAll);

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);

        if (_panning) {
            _panning = false;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        if (!_guideInteraction.IsDragging) return;

        var position = e.GetPosition(this);
        var remove = IsInRulerBand(position) || !new Rect(Bounds.Size).Contains(position);
        var removed = _guideInteraction.EndDrag(_guides, remove);

        e.Pointer.Capture(null);
        Cursor = _guideInteraction.Cursor;
        // 拖到画布外松开：参考线已删，指针状态也清掉，避免状态栏留着过期坐标
        SetPointer(new Rect(Bounds.Size).Contains(position) ? position : null);
        InvalidateVisual();
        if (removed) {
            GuidesChanged?.Invoke(this, EventArgs.Empty);
        }

        GuideDragChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>
    ///     指针直接进入标尺边带时也要立刻换成"可拖出参考线"的光标。
    ///     <para>只靠 <see cref="OnPointerMoved" /> 的话，从窗口外直接移到标尺上会先闪一下默认箭头。</para>
    /// </summary>
    protected override void OnPointerEntered(PointerEventArgs e) {
        base.OnPointerEntered(e);
        s_hoveredCanvas = new WeakReference<ImageCanvas>(this);
        var position = e.GetPosition(this);
        SetPointer(position);
        UpdateHover(position);
    }

    protected override void OnPointerExited(PointerEventArgs e) {
        base.OnPointerExited(e);

        if (_panning) {
            _panning = false;
            e.Pointer.Capture(null);
        }

        if (_guideInteraction.IsDragging) return; // 有指针捕获时不应走到这里；真走到也不丢参考线

        _guideInteraction.ResetHover();
        Cursor = _guideInteraction.Cursor;
        SetPointer(null);
        if (s_hoveredCanvas is not null && s_hoveredCanvas.TryGetTarget(out var hovered) && ReferenceEquals(hovered, this)) {
            s_hoveredCanvas = null;
        }
    }

    private bool IsInRulerBand(Point position) => IsRulersVisible && (position.X < RulerSize || position.Y < RulerSize);
}
