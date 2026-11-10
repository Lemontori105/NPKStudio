using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace NPKStudio.Desktop.Platform;

/// <summary>
///     macOS 窗口级快捷键：⌘W 关闭当前窗口。
///     <para>
///         原生 Mac 程序的 ⌘W 来自"文件 → 关闭窗口"菜单项（performClose:）；Avalonia 不建这个菜单，
///         程序里也没有处理 ⌘W 的地方，所以按了没反应（不是和别的快捷键冲突）。
///         这里给所有 <see cref="Window" /> 挂一个类级处理：没被控件处理掉的 ⌘W 就调用
///         <see cref="Window.Close()" />。主窗口照常走关闭流程（有未保存修改会先询问）。
///     </para>
/// </summary>
internal static class MacWindowShortcuts {
    private static bool s_installed;

    /// <summary>在程序启动时调用一次；非 macOS 不做任何事（Windows 上 Ctrl+W 习惯上不关主窗口）。</summary>
    public static void Install() {
        if (s_installed || !OperatingSystem.IsMacOS()) return;

        s_installed = true;
        InputElement.KeyDownEvent.AddClassHandler<Window>(OnKeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>是否是"关闭窗口"快捷键（只认 ⌘W，不带其它修饰键）。</summary>
    internal static bool IsCloseWindow(Key key, KeyModifiers modifiers) =>
        key == Key.W && modifiers == KeyModifiers.Meta;

    private static void OnKeyDown(Window window, KeyEventArgs e) {
        if (e.Handled || !IsCloseWindow(e.Key, e.KeyModifiers)) return;

        e.Handled = true;
        window.Close();
    }
}
