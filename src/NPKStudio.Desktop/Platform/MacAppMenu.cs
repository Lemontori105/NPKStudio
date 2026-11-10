using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using NPKStudio.Core.Diagnostics;
using NPKStudio.Desktop.Localization;

namespace NPKStudio.Desktop.Platform;

/// <summary>
///     macOS 应用菜单（菜单栏最左边、以程序名命名的那个菜单），按苹果的标准顺序：
///     关于 / 设置… ⌘, / 服务 / 隐藏 ⌘H / 隐藏其他 ⌥⌘H / 全部显示 / 退出 ⌘Q，文字跟随界面语言。
///     <para>
///         <b>设置时机</b>：Avalonia.Native 在 <c>AppBuilder.AfterSetup</c> 回调里建应用菜单，只读一次
///         <see cref="Application" /> 上的 <see cref="NativeMenu" />，那时还没有就放默认的 "About Avalonia"，之后再设不会生效。
///         <c>AfterSetup</c> 在 <see cref="Application.Initialize" /> 之后、
///         <see cref="Application.OnFrameworkInitializationCompleted" /> 之前执行
///         （反编译查 Avalonia.Controls 11.3.22 <c>AppBuilder.SetupUnsafe</c> 的调用顺序），所以要在 <c>Initialize</c> 里设。
///     </para>
///     <para>
///         <b>标准项</b>：Avalonia 默认追加的 Services / Hide / Hide Others / Show All / Quit 是写死的英文
///         （<c>AvaloniaNativeMenuExporter.PopulateStandardOSXMenuItems</c>），所以 Program 里用
///         <c>MacOSPlatformOptions.DisableDefaultApplicationMenuItems</c> 关掉，这里照它的做法重建：
///         服务子菜单打上 Avalonia 内部的 <c>MacOSNativeMenuCommands.IsServicesSubmenuProperty</c> 标记
///         （原生侧据此把它当成系统"服务"菜单）；隐藏 / 隐藏其他 / 全部显示直接给 NSApp 发
///         <c>hide:</c> / <c>hideOtherApplications:</c> / <c>unhideAllApplications:</c>；
///         退出和 Avalonia 一样走 <see cref="IClassicDesktopStyleApplicationLifetime.TryShutdown" />，
///         主窗口的未保存确认照常生效。
///     </para>
/// </summary>
internal static class MacAppMenu {
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>
    ///     建应用菜单。菜单项文字在语言切换时自动更新（原生菜单项订阅了 Header 的变化）。
    /// </summary>
    public static NativeMenu Build(Action openAbout, Action openSettings) {
        var loc = TranslationCatalog.Instance;
        var labels = new List<(NativeMenuItem Item, string Key)>();
        var menu = new NativeMenu();

        NativeMenuItem Add(string key, Action onClick, KeyGesture? gesture = null) {
            var item = new NativeMenuItem { Gesture = gesture };
            item.Click += (_, _) => onClick();
            labels.Add((item, key));
            menu.Add(item);
            return item;
        }

        Add("menu.about", openAbout);
        menu.Add(new NativeMenuItemSeparator());
        Add("menu.settings", openSettings, new KeyGesture(Key.OemComma, KeyModifiers.Meta));
        menu.Add(new NativeMenuItemSeparator());

        // 服务：找不到 Avalonia 的内部标记（版本变了）就不显示这一项，其余照常
        if (ServicesMarker is { } marker) {
            var servicesMenu = new NativeMenu();
            servicesMenu.SetValue(marker, true);
            var services = new NativeMenuItem { Menu = servicesMenu };
            labels.Add((services, "menu.services"));
            menu.Add(services);
            menu.Add(new NativeMenuItemSeparator());
        }

        Add("menu.hide", () => SendToApplication("hide:"), new KeyGesture(Key.H, KeyModifiers.Meta));
        Add("menu.hideOthers", () => SendToApplication("hideOtherApplications:"),
            new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt));
        Add("menu.showAll", () => SendToApplication("unhideAllApplications:"));
        menu.Add(new NativeMenuItemSeparator());
        Add("menu.quit", Quit, new KeyGesture(Key.Q, KeyModifiers.Meta));

        void Relabel() {
            foreach (var (item, key) in labels) {
                item.Header = loc[key];
            }
        }

        Relabel();
        loc.PropertyChanged += (_, _) => Relabel();
        return menu;
    }

    /// <summary>
    ///     Avalonia.Native 用来标记"系统服务子菜单"的附加属性（internal 类型上的 public 字段，只能反射取）。
    ///     取不到时为 <c>null</c>。
    /// </summary>
    internal static AvaloniaProperty? ServicesMarker { get; } = FindServicesMarker();

    private static AvaloniaProperty? FindServicesMarker() {
        try {
            var type = Type.GetType("Avalonia.Native.MacOSNativeMenuCommands, Avalonia.Native", throwOnError: false);
            var field = type?.GetField("IsServicesSubmenuProperty", BindingFlags.Public | BindingFlags.Static);
            return field?.GetValue(null) as AvaloniaProperty;
        } catch (Exception ex) {
            TraceLog.Instance.Warn("找不到 Avalonia 的服务菜单标记，应用菜单里不显示「服务」", ex);
            return null;
        }
    }

    private static void Quit() {
        switch (Application.Current?.ApplicationLifetime) {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.TryShutdown();
                break;
            case IControlledApplicationLifetime controlled:
                controlled.Shutdown();
                break;
        }
    }

    /// <summary>给 NSApp 发一个带一个 id 参数（传 nil）的消息，例如 <c>hide:</c>。必须在主线程（菜单回调本来就在）。</summary>
    private static void SendToApplication(string selectorName) {
        try {
            var applicationClass = objc_getClass("NSApplication");
            if (applicationClass == IntPtr.Zero) return;

            var application = SendReturningId(applicationClass, sel_registerName("sharedApplication"));
            if (application == IntPtr.Zero) return;

            SendWithId(application, sel_registerName(selectorName), IntPtr.Zero);
        } catch (Exception ex) {
            TraceLog.Instance.Warn($"macOS 应用菜单命令 {selectorName} 失败", ex);
        }
    }

    [DllImport(LibObjC)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendReturningId(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendWithId(IntPtr receiver, IntPtr selector, IntPtr argument);
}
