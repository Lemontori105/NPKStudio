using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using NPKStudio.Desktop.Controls;
using NPKStudio.Desktop.Input;
using NPKStudio.Desktop.ViewModels;
using NPKStudio.Desktop.Views;

using Xunit;

namespace NPKStudio.Desktop.Tests;

/// <summary>
///     三栏布局（REQ-17）与状态栏（REQ-20）的界面层断言。
///     <para>
///         <b>为什么必须有这一层</b>：折叠改的是 <see cref="ColumnDefinition" /> 的宽度，它不在可视树里、
///         DataContext 不会继承，绑不上去；状态栏的缩放来自 <see cref="ImageCanvas.ZoomChanged" /> 事件，
///         纯 ViewModel 测试碰不到。两条路径都只有真控件能验证。
///     </para>
/// </summary>
public sealed class MainWindowLayoutUiTests
{
    private static Grid ContentGrid(MainWindow window) =>
        window.FindControl<Grid>("ContentGrid")!;

    private static void Click(MainWindow window, string buttonName)
    {
        var button = window.FindControl<Button>(buttonName);
        Assert.NotNull(button);
        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>折叠左栏 → 列宽 40；再点 → 回到默认 280。</summary>
    [AvaloniaFact]
    public void ToggleLeftPanel_CollapsesTo40_AndRestoresTo280()
    {
        var window = new MainWindow();
        window.Show();

        var column = ContentGrid(window).ColumnDefinitions[0];
        Assert.Equal(280, column.Width.Value);

        Click(window, "CollapseLeftButton");
        Assert.Equal(40, column.Width.Value);
        Assert.True(window.FindControl<Border>("LeftRail")!.IsVisible);
        Assert.False(window.FindControl<Grid>("LeftExpandedArea")!.IsVisible);

        Click(window, "ExpandLeftButton");
        Assert.Equal(280, column.Width.Value);
        Assert.False(window.FindControl<Border>("LeftRail")!.IsVisible);
        Assert.True(window.FindControl<Grid>("LeftExpandedArea")!.IsVisible);

        window.Close();
    }

    /// <summary>展开时回到"折叠前的实际宽度"，而不是永远回 280（拖过分隔条也要记住）。</summary>
    [AvaloniaFact]
    public void CollapseLeftPanel_RemembersCustomWidth()
    {
        var window = new MainWindow();
        window.Show();

        var grid = ContentGrid(window);
        grid.ColumnDefinitions[0].Width = new GridLength(200);

        Click(window, "CollapseLeftButton");
        Assert.Equal(40, grid.ColumnDefinitions[0].Width.Value);

        Click(window, "ExpandLeftButton");
        Assert.Equal(200, grid.ColumnDefinitions[0].Width.Value);

        window.Close();
    }

    /// <summary>折叠右栏 → 列宽 40；再点 → 回到默认 320。</summary>
    [AvaloniaFact]
    public void ToggleRightPanel_CollapsesTo40_AndRestoresTo320()
    {
        var window = new MainWindow();
        window.Show();

        var column = ContentGrid(window).ColumnDefinitions[4];
        Assert.Equal(320, column.Width.Value);

        Click(window, "CollapseRightButton");
        Assert.Equal(40, column.Width.Value);
        Assert.True(window.FindControl<Border>("RightRail")!.IsVisible);
        Assert.False(window.FindControl<Grid>("RightExpandedArea")!.IsVisible);

        Click(window, "ExpandRightButton");
        Assert.Equal(320, column.Width.Value);
        Assert.False(window.FindControl<Border>("RightRail")!.IsVisible);
        Assert.True(window.FindControl<Grid>("RightExpandedArea")!.IsVisible);

        window.Close();
    }

    /// <summary>双击左分隔条 → 左栏复位到 280（先把宽度改成 200，确保不是"本来就是 280"）。</summary>
    [AvaloniaFact]
    public void DoubleClickLeftSplitter_ResetsWidthTo280()
    {
        var window = new MainWindow();
        window.Show();

        var grid = ContentGrid(window);
        grid.ColumnDefinitions[0].Width = new GridLength(200);

        // 列宽改了要跑一次布局，面板宽度才会同步成 200（否则它仍盖在分隔条命中区上）
        Dispatcher.UIThread.RunJobs();

        var splitter = window.FindControl<GridSplitter>("LeftSplitter");
        Assert.NotNull(splitter);
        var point = splitter!.TranslatePoint(new Point(3, 120), window);
        Assert.NotNull(point);

        // 两次完整点击才能形成 ClickCount=2
        Hover(window, point!.Value);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);

        Assert.Equal(280, grid.ColumnDefinitions[0].Width.Value);

        window.Close();
    }

    private static void Hover(MainWindow window, Point point) =>
        window.MouseMove(point, RawInputModifiers.None);

    /// <summary>双击右分隔条 → 右栏复位到 320。</summary>
    [AvaloniaFact]
    public void DoubleClickRightSplitter_ResetsWidthTo320()
    {
        var window = new MainWindow();
        window.Show();

        var grid = ContentGrid(window);
        grid.ColumnDefinitions[4].Width = new GridLength(260);
        Dispatcher.UIThread.RunJobs();

        var splitter = window.FindControl<GridSplitter>("RightSplitter");
        Assert.NotNull(splitter);
        var point = splitter!.TranslatePoint(new Point(3, 120), window);
        Assert.NotNull(point);

        Hover(window, point!.Value);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);

        Assert.Equal(320, grid.ColumnDefinitions[4].Width.Value);

        window.Close();
    }

    /// <summary>选中帧后状态栏几何文案非空且含 ×；无选中帧时清空。</summary>
    [AvaloniaFact]
    public async Task StatusBar_ShowsSelectedFrameGeometry_AndClearsWhenNothingSelected()
    {
        using var folder = TestFolder.Create();
        folder.WriteImg("walk.img", frameCount: 3, width: 8, height: 8);

        var window = new MainWindow();
        window.Show();

        var viewModel = (MainWindowViewModel)window.DataContext!;
        await viewModel.OpenFolderAsync(folder.Path);

        var label = window.FindControl<TextBlock>("SelectionGeometryLabel");
        Assert.NotNull(label);

        Assert.NotNull(viewModel.SelectedFrame);
        Assert.False(string.IsNullOrEmpty(viewModel.SelectionGeometryText));
        Assert.Contains("X:", viewModel.SelectionGeometryText);
        Assert.Contains("Y:", viewModel.SelectionGeometryText);
        Assert.Contains("×", viewModel.SelectionGeometryText);
        Assert.Contains($"{viewModel.SelectedFrame!.Frame.Width}×{viewModel.SelectedFrame.Frame.Height}",
            viewModel.SelectionGeometryText);
        Assert.Equal(viewModel.SelectionGeometryText, label!.Text);
        Assert.True(label.IsVisible);

        // 改坐标后几何文案要跟着变（负号照原样显示，DNF 的帧常带负偏移）
        viewModel.Inspector.X = -7;
        Assert.Contains("X:-7", viewModel.SelectionGeometryText);

        viewModel.SelectedFrame = null;
        Assert.Equal(string.Empty, viewModel.SelectionGeometryText);
        Assert.False(label.IsVisible);

        window.Close();
    }

    /// <summary>
    ///     <see cref="ImageCanvas.FitToView" /> 之后状态栏缩放文案必须跟着变
    ///     （缩放的真实来源是画布，不是 ViewModel 自己算的）。
    /// </summary>
    [AvaloniaFact]
    public async Task FitToView_UpdatesStatusBarZoomText()
    {
        using var folder = TestFolder.Create();
        folder.WriteImg("walk.img", frameCount: 2, width: 8, height: 8);

        var window = new MainWindow();
        window.Show();

        var viewModel = (MainWindowViewModel)window.DataContext!;
        await viewModel.OpenFolderAsync(folder.Path);

        var canvas = window.FindControl<ImageCanvas>("FrameCanvas");
        Assert.NotNull(canvas);
        Assert.True(canvas!.Bounds.Width > 0, "画布没有完成布局，FitToView 会直接返回");
        Assert.NotNull(canvas.Image);

        canvas.SetZoomLevel(4);
        Assert.Equal("400%", viewModel.ZoomText);

        canvas.FitToView();
        Assert.NotEqual("400%", viewModel.ZoomText);
        Assert.Equal($"{(canvas.Zoom * 100).ToString("0")}%", viewModel.ZoomText);

        window.Close();
    }

    /// <summary>⇧⌘1 / ⇧⌘2（Windows 为 Ctrl+Shift+1 / 2）折叠对应面板（普通按键不触发）。</summary>
    [AvaloniaFact]
    public void CollapseShortcuts_TogglePanels()
    {
        var window = new MainWindow();
        window.Show();

        var grid = ContentGrid(window);
        var left = grid.ColumnDefinitions[0];
        var right = grid.ColumnDefinitions[4];

        Press(window, Key.D1, PlatformShortcuts.Command | KeyModifiers.Shift);
        Assert.Equal(40, left.Width.Value);

        Press(window, Key.D2, PlatformShortcuts.Command | KeyModifiers.Shift);
        Assert.Equal(40, right.Width.Value);

        Press(window, Key.D1, PlatformShortcuts.Command | KeyModifiers.Shift);
        Assert.Equal(280, left.Width.Value);

        Press(window, Key.D2, PlatformShortcuts.Command | KeyModifiers.Shift);
        Assert.Equal(320, right.Width.Value);

        window.Close();
    }

    private static void Press(MainWindow window, Key key, KeyModifiers modifiers) =>
        window.RaiseEvent(new KeyEventArgs {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers
        });

    /// <summary>双击画布 → 适配窗口（与右键菜单「适配窗口」结果一致）。</summary>
    [AvaloniaFact]
    public async Task DoubleClickOnCanvas_FitsToView()
    {
        using var folder = TestFolder.Create();
        folder.WriteImg("walk.img", frameCount: 2, width: 8, height: 8);

        var window = new MainWindow();
        window.Show();

        var viewModel = (MainWindowViewModel)window.DataContext!;
        await viewModel.OpenFolderAsync(folder.Path);

        var canvas = window.FindControl<ImageCanvas>("FrameCanvas");
        Assert.NotNull(canvas);
        Assert.True(canvas!.Bounds.Width > 0, "画布没有完成布局，FitToView 会直接返回");

        canvas.SetZoomLevel(2);
        Assert.Equal(2.0, canvas.Zoom, 3);

        var point = canvas.TranslatePoint(new Point(24, 24), window);
        Assert.NotNull(point);
        window.MouseDown(point!.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);

        var afterDoubleClick = canvas.Zoom;
        Assert.NotEqual(2.0, afterDoubleClick, 3);

        // 手动 FitToView 一次，缩放必须落在同一个值上（说明双击走的就是 FitToView）
        canvas.SetZoomLevel(2);
        canvas.FitToView();
        Assert.Equal(canvas.Zoom, afterDoubleClick, 3);

        window.Close();
    }
}
