using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;

using NPKStudio.Desktop.Controls;
using NPKStudio.Desktop.Input;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.ViewModels;
using NPKStudio.Desktop.Views;

using Xunit;

namespace NPKStudio.Desktop.Tests;

/// <summary>
///     画布标尺 / 游标 / 参考线的界面层断言。
///     <para>
///         <b>为什么必须有这一层</b>：可见性开关是快捷键 → ViewModel → XAML 绑定 → 控件属性的三段链路，
///         纯 ViewModel 测试看不到绑定是否真的落到 <see cref="ImageCanvas" />；刻度步进又依赖控件的
///         视口变换（<c>Zoom</c>），只能拿真控件断言。
///     </para>
/// </summary>
public sealed class CanvasRulerUiTests
{
    private static void Press(MainWindow window, Key key, KeyModifiers modifiers) =>
        window.RaiseEvent(new KeyEventArgs {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers
        });

    private static ImageCanvas Canvas(MainWindow window) {
        var canvas = window.FindControl<ImageCanvas>("FrameCanvas");
        Assert.NotNull(canvas);
        return canvas!;
    }

    /// <summary>⌘R（Windows 为 Ctrl+R）切换标尺显示；VM 与控件两边必须同步（右键菜单绑定的是同一属性）。</summary>
    [AvaloniaFact]
    public void CtrlR_TogglesRulers_OnViewModelAndCanvas() {
        var window = new MainWindow();
        window.Show();

        var vm = (MainWindowViewModel)window.DataContext!;
        var canvas = Canvas(window);

        Assert.True(vm.IsRulersVisible);
        Assert.True(canvas.IsRulersVisible);

        // 右键菜单的勾选项绑定的是同一批 VM 属性：ContextMenu 不在可视树里，DataContext 必须能继承到
        Assert.NotNull(canvas.ContextMenu);
        Assert.Same(vm, canvas.ContextMenu!.DataContext);

        var toggles = canvas.ContextMenu.Items.OfType<MenuItem>()
            .Where(item => item.ToggleType == MenuItemToggleType.CheckBox)
            .ToList();
        // 标尺 / 参考线 / 参考线坐标 / 像素对齐
        Assert.Equal(4, toggles.Count);
        Assert.True(toggles[0].IsChecked);
        Assert.True(toggles[1].IsChecked);
        Assert.True(toggles[2].IsChecked);
        Assert.True(toggles[3].IsChecked);

        Press(window, Key.R, PlatformShortcuts.Command);
        Assert.False(vm.IsRulersVisible);
        Assert.False(canvas.IsRulersVisible);
        Assert.False(toggles[0].IsChecked);

        // 反向：菜单勾选（TwoWay）也要推回 VM / 控件
        toggles[1].IsChecked = false;
        Assert.False(vm.IsGuidesVisible);
        Assert.False(canvas.IsGuidesVisible);

        // 「参考线坐标」只管标签，不连带隐藏参考线本身
        toggles[2].IsChecked = false;
        Assert.False(vm.IsGuideLabelsVisible);
        Assert.False(canvas.ShowGuideLabels);

        Press(window, Key.R, PlatformShortcuts.Command);
        Assert.True(vm.IsRulersVisible);
        Assert.True(canvas.IsRulersVisible);

        window.Close();
    }

    /// <summary>⌘;（Windows 为 Ctrl+;）切换参考线显示，隐藏后数据仍保留。</summary>
    [AvaloniaFact]
    public void CtrlSemicolon_TogglesGuides_AndKeepsData() {
        var window = new MainWindow();
        window.Show();

        var vm = (MainWindowViewModel)window.DataContext!;
        var canvas = Canvas(window);

        canvas.AddHorizontalGuide(12);
        Assert.Equal(1, canvas.GuideCount);
        Assert.True(canvas.IsGuidesVisible);

        Press(window, Key.OemSemicolon, PlatformShortcuts.Command);
        Assert.False(vm.IsGuidesVisible);
        Assert.False(canvas.IsGuidesVisible);
        Assert.Equal(1, canvas.GuideCount);

        Press(window, Key.OemSemicolon, PlatformShortcuts.Command);
        Assert.True(vm.IsGuidesVisible);
        Assert.True(canvas.IsGuidesVisible);
        Assert.Equal(1, canvas.GuideCount);

        window.Close();
    }

    /// <summary>一条水平 + 一条垂直 → 2；清除后归零。</summary>
    [AvaloniaFact]
    public void Guides_CanBeAddedAndCleared() {
        var window = new MainWindow();
        window.Show();

        var canvas = Canvas(window);
        Assert.Equal(0, canvas.GuideCount);

        canvas.AddHorizontalGuide(64);
        canvas.AddVerticalGuide(96);

        Assert.Single(canvas.HorizontalGuides);
        Assert.Single(canvas.VerticalGuides);
        Assert.Equal(2, canvas.GuideCount);
        Assert.Equal(64, canvas.HorizontalGuides[0]);
        Assert.Equal(96, canvas.VerticalGuides[0]);

        canvas.ClearGuides();
        Assert.Equal(0, canvas.GuideCount);
        Assert.Empty(canvas.HorizontalGuides);
        Assert.Empty(canvas.VerticalGuides);

        window.Close();
    }

    /// <summary>
    ///     真实指针拖拽：从顶部标尺拉到画布 → 固定一条水平参考线；再把它拖回标尺 → 删除。
    ///     左侧标尺同理产生垂直参考线。
    /// </summary>
    [AvaloniaFact]
    public void DraggingFromRuler_CreatesGuide_AndDraggingBackDeletesIt() {
        var window = new MainWindow();
        window.Show();

        var canvas = Canvas(window);
        var top = canvas.TranslatePoint(new Point(120, ImageCanvas.RulerSize / 2), window);
        var left = canvas.TranslatePoint(new Point(ImageCanvas.RulerSize / 2, 200), window);
        var content = canvas.TranslatePoint(new Point(120, 160), window);
        Assert.NotNull(top);
        Assert.NotNull(left);
        Assert.NotNull(content);

        // 顶部标尺 → 下拉出水平参考线
        window.MouseDown(top!.Value, MouseButton.Left);
        window.MouseMove(content!.Value, RawInputModifiers.LeftMouseButton);
        window.MouseUp(content.Value, MouseButton.Left);
        Assert.Single(canvas.HorizontalGuides);

        // 抓住刚固定的线再拖回标尺 → 松开即删除
        window.MouseDown(content.Value, MouseButton.Left);
        window.MouseMove(top.Value, RawInputModifiers.LeftMouseButton);
        window.MouseUp(top.Value, MouseButton.Left);
        Assert.Empty(canvas.HorizontalGuides);

        // 左侧标尺 → 右拉出垂直参考线
        window.MouseDown(left!.Value, MouseButton.Left);
        window.MouseMove(content.Value, RawInputModifiers.LeftMouseButton);
        window.MouseUp(content.Value, MouseButton.Left);
        Assert.Single(canvas.VerticalGuides);

        window.Close();
    }

    /// <summary>鼠标进入 / 移动 / 离开时状态栏坐标文案跟着变，离开后清空。</summary>
    [AvaloniaFact]
    public async Task PointerMovement_UpdatesStatusBarText_AndClearsOnExit() {
        using var folder = TestFolder.Create();
        folder.WriteImg("walk.img", frameCount: 2, width: 8, height: 8);

        var window = new MainWindow();
        window.Show();

        var vm = (MainWindowViewModel)window.DataContext!;
        await vm.OpenFolderAsync(folder.Path);

        var canvas = Canvas(window);
        Assert.Equal(string.Empty, vm.MousePositionText);

        canvas.SimulatePointer(new Point(240, 180));
        var first = vm.MousePositionText;
        Assert.False(string.IsNullOrEmpty(first));

        // 光标坐标必须带「光标」前缀、用逗号分隔，不能和选中帧的 "X:0 Y:0" 长成一样
        var pointer = canvas.PointerFramePosition!.Value;
        Assert.Equal(
            TranslationCatalog.Instance.F("status.pointer", (int)Math.Round(pointer.X), (int)Math.Round(pointer.Y)),
            first);
        Assert.DoesNotContain("X:", first);
        Assert.DoesNotContain("Y:", first);
        Assert.Contains(",", first);

        canvas.SimulatePointer(new Point(320, 260));
        Assert.NotEqual(first, vm.MousePositionText);

        canvas.SimulatePointer(null);
        Assert.Equal(string.Empty, vm.MousePositionText);

        window.Close();
    }

    /// <summary>
    ///     100% → 400% 后刻度步进必须变小（数字间距始终 ≥48px）。
    ///     100% 时 1/2/5/10/20 都不够 48px，取 50；400% 时 20 对应 80px，取 20。
    /// </summary>
    [AvaloniaFact]
    public async Task RulerStep_ShrinksWhenZoomingFrom100To400() {
        using var folder = TestFolder.Create();
        folder.WriteImg("walk.img", frameCount: 2, width: 8, height: 8);

        var window = new MainWindow();
        window.Show();

        var vm = (MainWindowViewModel)window.DataContext!;
        await vm.OpenFolderAsync(folder.Path);

        var canvas = Canvas(window);
        Assert.True(canvas.Bounds.Width > 0, "画布没有完成布局，SetZoomLevel 会直接返回");

        canvas.SetZoomLevel(1.0);
        Assert.Equal(1.0, canvas.Zoom);
        Assert.Equal(50.0, canvas.RulerStep);

        canvas.SetZoomLevel(4.0);
        Assert.Equal(4.0, canvas.Zoom);
        Assert.Equal(20.0, canvas.RulerStep);

        window.Close();
    }
}
