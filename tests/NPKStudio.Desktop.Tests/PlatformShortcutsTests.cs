using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;

using NPKStudio.Desktop.Input;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.ViewModels;
using NPKStudio.Desktop.Views;

using Xunit;

namespace NPKStudio.Desktop.Tests;

/// <summary>
///     平台快捷键约定（见 <see cref="PlatformShortcuts" />）：macOS 只认 ⌘、修饰键精确匹配，
///     重做用 ⇧⌘Z，delete 键（<see cref="Key.Back" />）也能删帧，文案换成 Mac 写法；其他平台认 Ctrl。
///     <para>名字里带 <c>OnMac</c> 的用例只在 macOS 上断言，其他平台直接返回。</para>
/// </summary>
public sealed class PlatformShortcutsTests
{
    private static void Press(MainWindow window, Key key, KeyModifiers modifiers) =>
        window.RaiseEvent(new KeyEventArgs {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers
        });

    [Theory]
    [InlineData("打开资源包  Ctrl+O", "打开资源包  ⌘O")]
    [InlineData("重做  Ctrl+Y", "重做  ⇧⌘Z")]
    [InlineData("折叠文件列表  Ctrl+Shift+1", "折叠文件列表  ⇧⌘1")]
    [InlineData("把 .NPK 或 .IMG 拖进窗口，或按 Ctrl+O", "把 .NPK 或 .IMG 拖进窗口，或按 ⌘O")]
    [InlineData("平移  Alt+拖动", "平移  ⌥拖动")]
    [InlineData("Delete frame  Delete", "Delete frame  ⌫")]
    [InlineData("删除帧  Delete", "删除帧  ⌫")]
    [InlineData("Deleted {0} frame(s)", "Deleted {0} frame(s)")]
    [InlineData("", "")]
    public void ToMacNotation_ConvertsShortcutText(string input, string expected) =>
        Assert.Equal(expected, PlatformShortcuts.ToMacNotation(input));

    [Fact]
    public void Localize_ConvertsOnlyOnMac() {
        const string text = "撤销  Ctrl+Z";
        var expected = PlatformShortcuts.IsMac ? "撤销  ⌘Z" : text;
        Assert.Equal(expected, PlatformShortcuts.Localize(text));
    }

    [Fact]
    public void Command_IsCmdOnMac_CtrlElsewhere() {
        var expected = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        Assert.Equal(expected, PlatformShortcuts.Command);
    }

    [Fact]
    public void Is_RequiresExactModifiers() {
        var cmd = PlatformShortcuts.Command;
        Assert.True(PlatformShortcuts.Is(cmd));
        Assert.False(PlatformShortcuts.Is(cmd | KeyModifiers.Shift));
        Assert.True(PlatformShortcuts.Is(cmd | KeyModifiers.Shift, shift: true));
        Assert.False(PlatformShortcuts.Is(cmd, shift: true));
        Assert.False(PlatformShortcuts.Is(cmd | KeyModifiers.Alt));
        Assert.False(PlatformShortcuts.Is(KeyModifiers.None));
    }

    /// <summary>词表加载后，Mac 上所有带快捷键的提示都不再出现 Ctrl（两种语言都成立，不受其它用例切语言影响）。</summary>
    [Fact]
    public void Catalog_ShowsMacShortcuts_OnMac() {
        if (!PlatformShortcuts.IsMac) return;

        var loc = TranslationCatalog.Instance;
        string[] keys = [
            "tip.open", "tip.save", "tip.undo", "tip.redo", "tip.png", "tip.writeBack", "empty.dropHint",
            "panel.collapseLeft", "panel.expandLeft", "panel.collapseRight", "panel.expandRight",
            "canvas.guides.addH", "canvas.guides.addV", "inspector.delete"
        ];
        foreach (var key in keys) {
            Assert.DoesNotContain("Ctrl", loc[key], StringComparison.Ordinal);
        }

        Assert.EndsWith("⌘O", loc["tip.open"], StringComparison.Ordinal);
        Assert.EndsWith("⇧⌘Z", loc["tip.redo"], StringComparison.Ordinal);
        Assert.EndsWith("⌫", loc["inspector.delete"], StringComparison.Ordinal);
    }

    /// <summary>Mac 上 control+R 不再切换标尺，只有 ⌘R 生效。</summary>
    [AvaloniaFact]
    public void ControlR_IsIgnored_OnMac() {
        if (!PlatformShortcuts.IsMac) return;

        var window = new MainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        Assert.True(vm.IsRulersVisible);

        Press(window, Key.R, KeyModifiers.Control);
        Assert.True(vm.IsRulersVisible);

        Press(window, Key.R, KeyModifiers.Meta);
        Assert.False(vm.IsRulersVisible);

        window.Close();
    }

    /// <summary>
    ///     撤销 ⌘Z、重做 ⇧⌘Z；Mac 上 ⌘Y 和 control+Z 都不起作用，其他平台 Ctrl+Y 仍是重做。
    /// </summary>
    [AvaloniaFact]
    public async Task UndoRedo_FollowPlatformConvention() {
        using var folder = TestFolder.Create();
        folder.WriteImg("walk.img", frameCount: 3, width: 8, height: 8);

        var window = new MainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        await vm.OpenFolderAsync(folder.Path);
        vm.SelectedFrame = vm.Frames[1];
        vm.DeleteSelectedFrames();
        Assert.Equal(2, vm.Frames.Count);

        if (PlatformShortcuts.IsMac) {
            Press(window, Key.Z, KeyModifiers.Control);
            Assert.Equal(2, vm.Frames.Count);
        }

        Press(window, Key.Z, PlatformShortcuts.Command);
        Assert.Equal(3, vm.Frames.Count);

        Press(window, Key.Y, PlatformShortcuts.Command);
        if (PlatformShortcuts.IsMac) {
            Assert.Equal(3, vm.Frames.Count);
        } else {
            Assert.Equal(2, vm.Frames.Count);
            Press(window, Key.Z, PlatformShortcuts.Command);
            Assert.Equal(3, vm.Frames.Count);
        }

        Press(window, Key.Z, PlatformShortcuts.Command | KeyModifiers.Shift);
        Assert.Equal(2, vm.Frames.Count);

        window.Close();
    }

    /// <summary>Mac 键盘的 delete 键（Avalonia 报 <see cref="Key.Back" />）删帧；在输入框里照样放行当删字符。</summary>
    [AvaloniaFact]
    public async Task BackKey_DeletesFrame_ButIsIgnoredFromTextBox_OnMac() {
        if (!PlatformShortcuts.IsMac) return;

        using var folder = TestFolder.Create();
        folder.WriteImg("walk.img", frameCount: 3, width: 8, height: 8);

        var window = new MainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        await vm.OpenFolderAsync(folder.Path);
        vm.SelectedFrame = vm.Frames[1];

        var search = window.FindControl<TextBox>("SearchBox");
        Assert.NotNull(search);
        search!.RaiseEvent(new KeyEventArgs {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Back
        });
        Assert.Equal(3, vm.Frames.Count);

        window.RaiseEvent(new KeyEventArgs {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Back
        });
        Assert.Equal(2, vm.Frames.Count);

        window.Close();
    }
}
