using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.Platform;

using Xunit;

namespace NPKStudio.Desktop.Tests;

/// <summary>
///     macOS 应用菜单（菜单栏最左边的 NPKStudio 菜单）。
///     <para>原生菜单本身要真机看；这里守住三件事：顺序和快捷键、文字都来自词表、在 <c>Initialize</c> 里就设好。</para>
/// </summary>
public sealed class MacAppMenuTests
{
    private static readonly string[] StandardKeys =
        ["menu.about", "menu.settings", "menu.services", "menu.hide", "menu.hideOthers", "menu.showAll", "menu.quit"];

    /// <summary>苹果的标准顺序：关于 / 设置 / 服务 / 隐藏 / 隐藏其他 / 全部显示 / 退出，全部取自词表（没有写死的英文）。</summary>
    [AvaloniaFact]
    public void Build_UsesAppleOrderAndTranslatedLabels() {
        var loc = TranslationCatalog.Instance;
        // 分隔线 NativeMenuItemSeparator 也是 NativeMenuItem（Header 为 "-"），比顺序时去掉
        var items = MacAppMenu.Build(() => { }, () => { }).Items.OfType<NativeMenuItem>()
            .Where(item => item is not NativeMenuItemSeparator).ToList();

        var expectedKeys = StandardKeys.Where(key => key != "menu.services" || MacAppMenu.ServicesMarker is not null);
        Assert.Equal(expectedKeys.Select(key => loc[key]), items.Select(item => item.Header));
        Assert.All(StandardKeys, key => Assert.NotEqual(key, loc[key])); // 词表里都有
        Assert.DoesNotContain(items, item => (item.Header ?? string.Empty).Contains("Avalonia"));
    }

    /// <summary>快捷键和系统一致：设置 ⌘,、隐藏 ⌘H、隐藏其他 ⌥⌘H、退出 ⌘Q。</summary>
    [AvaloniaFact]
    public void Build_UsesStandardShortcuts() {
        var loc = TranslationCatalog.Instance;
        var items = MacAppMenu.Build(() => { }, () => { }).Items.OfType<NativeMenuItem>().ToList();
        KeyGesture? GestureOf(string key) => items.Single(item => item.Header == loc[key]).Gesture;

        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta), GestureOf("menu.settings"));
        Assert.Equal(new KeyGesture(Key.H, KeyModifiers.Meta), GestureOf("menu.hide"));
        Assert.Equal(new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt), GestureOf("menu.hideOthers"));
        Assert.Equal(new KeyGesture(Key.Q, KeyModifiers.Meta), GestureOf("menu.quit"));
    }

    /// <summary>
    ///     "服务"子菜单靠 Avalonia.Native 的内部标记识别（反射取）。在 Mac 上必须取得到：
    ///     升级 Avalonia 后这里失败，说明内部名字变了，要重新逆向确认。
    /// </summary>
    [AvaloniaFact]
    public void ServicesSubmenu_IsMarkedOnMac() {
        if (!OperatingSystem.IsMacOS()) return;

        var marker = MacAppMenu.ServicesMarker;
        Assert.NotNull(marker);

        var services = MacAppMenu.Build(() => { }, () => { }).Items.OfType<NativeMenuItem>()
            .Single(item => item.Header == TranslationCatalog.Instance["menu.services"]);
        Assert.NotNull(services.Menu);
        Assert.True(services.Menu!.GetValue(marker!) is true);
    }

    /// <summary>
    ///     菜单必须在 <see cref="App.Initialize" /> 里就挂到 Application 上：Avalonia.Native 紧接着就读，
    ///     放到 OnFrameworkInitializationCompleted 里会被默认的 "About Avalonia" 菜单顶掉。
    /// </summary>
    [AvaloniaFact]
    public void AppInitialize_SetsAppMenuOnMac() {
        if (!OperatingSystem.IsMacOS()) return;

        var app = new App();
        app.Initialize();

        var menu = NativeMenu.GetMenu(app);
        Assert.NotNull(menu);
        Assert.Equal(TranslationCatalog.Instance["menu.about"], menu!.Items.OfType<NativeMenuItem>().First().Header);
    }

    /// <summary>只有 ⌘W 算"关闭窗口"：单按 W、⇧⌘W、⌥⌘W 都不算。</summary>
    [Fact]
    public void CloseWindowShortcut_IsCommandWOnly() {
        Assert.True(MacWindowShortcuts.IsCloseWindow(Key.W, KeyModifiers.Meta));
        Assert.False(MacWindowShortcuts.IsCloseWindow(Key.W, KeyModifiers.None));
        Assert.False(MacWindowShortcuts.IsCloseWindow(Key.W, KeyModifiers.Meta | KeyModifiers.Shift));
        Assert.False(MacWindowShortcuts.IsCloseWindow(Key.W, KeyModifiers.Meta | KeyModifiers.Alt));
        Assert.False(MacWindowShortcuts.IsCloseWindow(Key.Q, KeyModifiers.Meta));
    }

    /// <summary>Mac 上在任意窗口按 ⌘W 都会关掉这个窗口（Avalonia 不建"文件 → 关闭窗口"菜单，原来按了没反应）。</summary>
    [AvaloniaFact]
    public void CommandW_ClosesWindowOnMac() {
        if (!OperatingSystem.IsMacOS()) return;

        var window = new Window();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();

        window.KeyPress(Key.W, RawInputModifiers.None, PhysicalKey.W, "w");
        Assert.False(closed);

        window.KeyPress(Key.W, RawInputModifiers.Meta, PhysicalKey.W, "w");
        Assert.True(closed);
    }
}
