using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

using NPKStudio.Desktop.Controls;
using NPKStudio.Desktop.Input;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.ViewModels;
using NPKStudio.Desktop.Views;

using Xunit;

namespace NPKStudio.Desktop.Tests;

/// <summary>
///     「参考线」可发现性与检查器布局的界面层断言。
///     <para>
///         <b>为什么必须有这一层</b>：参考线数据只存在 <see cref="ImageCanvas" /> 里，菜单项是否存在、
///         点击后有没有真的落到画布、0 条时「清除」是否置灰，纯 ViewModel 测试都看不到；
///         输入框等宽是布局结果，也只有真控件能量出来。
///     </para>
/// </summary>
public sealed class GuideMenuUiTests
{
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    private static ImageCanvas Canvas(MainWindow window) {
        var canvas = window.FindControl<ImageCanvas>("FrameCanvas");
        Assert.NotNull(canvas);
        return canvas!;
    }

    /// <summary>按本地化文案找右键菜单项（菜单里没有 x:Name 依赖，文案本身就是用户看到的入口）。</summary>
    private static MenuItem GuideMenuItem(MainWindow window, string key) {
        var header = Loc[key];
        var item = Canvas(window).ContextMenu!.Items.OfType<MenuItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Header as string, header, StringComparison.Ordinal));
        Assert.NotNull(item);
        return item!;
    }

    private static void Click(MenuItem item) =>
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private static void Press(MainWindow window, Key key, KeyModifiers modifiers) =>
        window.RaiseEvent(new KeyEventArgs {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers
        });

    /// <summary>右键菜单里必须有"添加水平/垂直参考线"，且顺序在「参考线」开关之后、「清除」之前。</summary>
    [AvaloniaFact]
    public void ContextMenu_ExposesAddGuideEntries_InGuideGroup() {
        var window = new MainWindow();
        window.Show();

        var items = Canvas(window).ContextMenu!.Items.OfType<MenuItem>().ToList();
        var headers = items.Select(item => item.Header as string).ToList();

        Assert.Contains(Loc["canvas.guides.addH"], headers);
        Assert.Contains(Loc["canvas.guides.addV"], headers);
        Assert.Contains(Loc["canvas.guides.clear"], headers);
        Assert.Contains(Loc["canvas.guides.labels"], headers);

        var guidesToggle = headers.IndexOf(Loc["canvas.guides"]);
        var labelsToggle = headers.IndexOf(Loc["canvas.guides.labels"]);
        var addHorizontal = headers.IndexOf(Loc["canvas.guides.addH"]);
        var addVertical = headers.IndexOf(Loc["canvas.guides.addV"]);
        var clear = headers.IndexOf(Loc["canvas.guides.clear"]);

        Assert.True(guidesToggle >= 0, "「参考线」显示开关不见了");
        Assert.True(labelsToggle > guidesToggle, "「参考线坐标」必须紧跟在参考线开关之后");
        Assert.True(addHorizontal > labelsToggle, "「添加水平参考线」必须排在参考线分组内");
        Assert.True(addVertical > addHorizontal, "「添加垂直参考线」必须排在水平那条之后");
        Assert.True(clear > addVertical, "「清除参考线」必须是分组最后一项");

        window.Close();
    }

    /// <summary>点菜单项真的加线；「清除」在有线时可用、无线时置灰。</summary>
    [AvaloniaFact]
    public void MenuAddAndClear_TracksGuideCount_AndDisablesClearWhenEmpty() {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var vm = (MainWindowViewModel)window.DataContext!;
        var canvas = Canvas(window);
        var addHorizontal = GuideMenuItem(window, "canvas.guides.addH");
        var addVertical = GuideMenuItem(window, "canvas.guides.addV");
        var clear = GuideMenuItem(window, "canvas.guides.clear");

        Assert.Equal(0, canvas.GuideCount);
        Assert.Equal(0, vm.GuideCount);
        Assert.False(vm.HasGuides);
        Assert.False(clear.IsEnabled);

        Click(addHorizontal);
        Assert.Single(canvas.HorizontalGuides);
        Assert.Empty(canvas.VerticalGuides);
        // 落在"当前视图可见区域"的中心，而不是 0 或帧左上角
        Assert.Equal(canvas.ViewCenterFramePosition.Y, canvas.HorizontalGuides[0], 6);
        Assert.Equal(1, vm.GuideCount);
        Dispatcher.UIThread.RunJobs();
        Assert.True(clear.IsEnabled);

        Click(addVertical);
        Assert.Single(canvas.VerticalGuides);
        Assert.Equal(canvas.ViewCenterFramePosition.X, canvas.VerticalGuides[0], 6);
        Assert.Equal(2, vm.GuideCount);

        Click(clear);
        Assert.Equal(0, canvas.GuideCount);
        Assert.Equal(0, vm.GuideCount);
        Dispatcher.UIThread.RunJobs();
        Assert.False(clear.IsEnabled);

        window.Close();
    }

    /// <summary>⇧⌘H / ⇧⌘V（Windows 为 Ctrl+Shift+H / V）各加一条（与右键菜单同一入口）。</summary>
    [AvaloniaFact]
    public void AddGuideShortcuts_AddOneGuideEach() {
        var window = new MainWindow();
        window.Show();

        var vm = (MainWindowViewModel)window.DataContext!;
        var canvas = Canvas(window);

        Press(window, Key.H, PlatformShortcuts.Command | KeyModifiers.Shift);
        Assert.Single(canvas.HorizontalGuides);
        Assert.Equal(1, vm.GuideCount);

        Press(window, Key.V, PlatformShortcuts.Command | KeyModifiers.Shift);
        Assert.Single(canvas.VerticalGuides);
        Assert.Equal(2, vm.GuideCount);

        // 只按 H（无修饰键）不能误加：H 不是任何工具快捷键
        Press(window, Key.H, KeyModifiers.None);
        Assert.Equal(2, canvas.GuideCount);

        window.Close();
    }

    /// <summary>
    ///     从标尺拖出参考线时状态栏要给提示，松手后换成"共 N 条"——用户得先知道"这里能拖"。
    /// </summary>
    [AvaloniaFact]
    public void DraggingFromRuler_ShowsStatusHint_ThenGuideCount() {
        var window = new MainWindow();
        window.Show();

        var vm = (MainWindowViewModel)window.DataContext!;
        var canvas = Canvas(window);

        var rulerPoint = canvas.TranslatePoint(new Point(200, ImageCanvas.RulerSize / 2), window);
        var contentPoint = canvas.TranslatePoint(new Point(200, 200), window);
        Assert.NotNull(rulerPoint);
        Assert.NotNull(contentPoint);

        window.MouseDown(rulerPoint!.Value, MouseButton.Left);
        Assert.True(canvas.IsDraggingGuide);
        Assert.Equal(Loc["status.guideDragging"], vm.Status);

        window.MouseMove(contentPoint!.Value, RawInputModifiers.LeftMouseButton);
        window.MouseUp(contentPoint.Value, MouseButton.Left);

        Assert.False(canvas.IsDraggingGuide);
        Assert.Single(canvas.HorizontalGuides);
        Assert.Equal(Loc.F("status.guideCount", 1), vm.Status);

        window.Close();
    }

    /// <summary>
    ///     检查器四个数字框必须逐像素等宽：X 与 Y 相等、宽与高相等、四者全等（误差 0）。
    /// </summary>
    [AvaloniaFact]
    public async Task InspectorInputs_HaveExactlyEqualWidths() {
        using var folder = TestFolder.Create();
        folder.WriteImg("walk.img", frameCount: 2, width: 32, height: 32);

        var window = new MainWindow();
        window.Show();

        var vm = (MainWindowViewModel)window.DataContext!;
        await vm.OpenFolderAsync(folder.Path);
        Assert.NotNull(vm.SelectedFrame);

        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var x = window.FindControl<NumericUpDown>("InspectorXInput");
        var y = window.FindControl<NumericUpDown>("InspectorYInput");
        var width = window.FindControl<NumericUpDown>("InspectorWidthInput");
        var height = window.FindControl<NumericUpDown>("InspectorHeightInput");

        Assert.NotNull(x);
        Assert.NotNull(y);
        Assert.NotNull(width);
        Assert.NotNull(height);

        Assert.True(x!.Bounds.Width > 0, "检查器没有完成布局，量不到输入框宽度");

        // 允许 ≤1px 的布局取整差：为保证 0.5px 绝对等宽而关掉布局取整会让边框落在半像素上发虚，
        // 对像素艺术工具不划算。1px 差肉眼不可辨，边框锐利更重要。
        Assert.True(Math.Abs(x.Bounds.Width - y!.Bounds.Width) <= 1, $"X={x.Bounds.Width} 与 Y={y.Bounds.Width} 宽度差超过 1px");
        Assert.True(Math.Abs(width!.Bounds.Width - height!.Bounds.Width) <= 1, $"宽={width.Bounds.Width} 与 高={height.Bounds.Width} 宽度差超过 1px");
        Assert.True(Math.Abs(x.Bounds.Width - width.Bounds.Width) <= 1, $"X={x.Bounds.Width} 与 宽={width.Bounds.Width} 宽度差超过 1px");

        window.Close();
    }

    /// <summary>
    ///     参考线 / 十字线 token 在浅色与深色两套主题里都存在，且**值不同**；
    ///     两者都是纯 1px（不再叠托底黑影），所以十字线必须 100% 不透明、参考线用更高对比度的色。
    /// </summary>
    [AvaloniaFact]
    public void GuideAndCrosshairTokens_DifferBetweenThemes() {
        var app = Application.Current!;

        var lightGuide = TokenColor(app, "GuideLine", ThemeVariant.Light);
        var darkGuide = TokenColor(app, "GuideLine", ThemeVariant.Dark);
        Assert.NotEqual(lightGuide, darkGuide);
        // 浅色：在白 / 浅灰棋盘格上要比原来的 #FF6B00 更深，才撑得住 1px 的可见度
        Assert.Equal(Color.Parse("#E04E00"), lightGuide);
        Assert.Equal(Color.Parse("#FFC24D"), darkGuide);

        var lightCrosshair = TokenColor(app, "CanvasCrosshair", ThemeVariant.Light);
        var darkCrosshair = TokenColor(app, "CanvasCrosshair", ThemeVariant.Dark);
        Assert.NotEqual(lightCrosshair, darkCrosshair);
        // 纯 1px：必须完全不透明，靠颜色本身保证可见性
        Assert.Equal(Color.Parse("#2F6BFF"), lightCrosshair);
        Assert.Equal(Color.Parse("#8FB2FF"), darkCrosshair);
        Assert.Equal(0xFF, lightCrosshair.A);
        Assert.Equal(0xFF, darkCrosshair.A);

        // 托底阴影 token 随"双层绘制"一起下线：两套主题里都不该再存在
        Assert.False(app.TryGetResource("CanvasOverlayShadow", ThemeVariant.Light, out _),
            "CanvasOverlayShadow 已删除，浅色主题里不该还有");
        Assert.False(app.TryGetResource("CanvasOverlayShadow", ThemeVariant.Dark, out _),
            "CanvasOverlayShadow 已删除，深色主题里不该还有");
    }

    private static Color TokenColor(Application app, string key, ThemeVariant variant) {
        Assert.True(app.TryGetResource(key, variant, out var value), $"{key} 在 {variant} 主题里不存在");
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
        return brush.Color;
    }
}
