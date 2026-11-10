using System.Text.RegularExpressions;
using Avalonia.Input;

namespace NPKStudio.Desktop.Input;

/// <summary>
///     平台快捷键约定：macOS 只认 Cmd（⌘，Avalonia 里叫 <see cref="KeyModifiers.Meta" />），其他平台认 Ctrl。
///     <para>
///         macOS 上 Ctrl+O 之类不再触发，修饰键必须精确匹配；重做用 ⇧⌘Z（不认 ⌘Y），
///         Mac 键盘上的 delete 键（Avalonia 报 <see cref="Key.Back" />）也算删除键。Windows / Linux 行为不变。
///     </para>
/// </summary>
internal static partial class PlatformShortcuts {
    /// <summary>当前是否运行在 macOS 上。</summary>
    public static bool IsMac { get; } = OperatingSystem.IsMacOS();

    /// <summary>主修饰键：macOS 是 Cmd（Meta），其他平台是 Ctrl。</summary>
    public static KeyModifiers Command { get; } = IsMac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>修饰键是否精确等于"主修饰键"（<paramref name="shift" /> 为真时再加 Shift），多按任何别的修饰键都不算。</summary>
    public static bool Is(KeyModifiers actual, bool shift = false) =>
        actual == (shift ? Command | KeyModifiers.Shift : Command);

    /// <summary>滚轮缩放用：只要按住主修饰键即可（Shift 等不影响）。</summary>
    public static bool HasCommand(KeyModifiers actual) => (actual & Command) != 0;

    /// <summary>重做是否用 Y：只有非 Mac 平台保留 Ctrl+Y，Mac 上重做只有 ⇧⌘Z。</summary>
    public static bool AllowsRedoWithY => !IsMac;

    /// <summary>删除键：Mac 键盘上标着 delete 的键在 Avalonia 里是 <see cref="Key.Back" />，fn+delete 才是 <see cref="Key.Delete" />。</summary>
    public static bool IsDeleteKey(Key key) => key == Key.Delete || (IsMac && key == Key.Back);

    /// <summary>
    ///     把文案里 Windows 写法的快捷键换成 Mac 写法；不在 macOS 上时原样返回。
    ///     <para>
    ///         规则（按顺序）：<c>Ctrl+Y</c> → <c>⇧⌘Z</c>；<c>Ctrl+Shift+</c> → <c>⇧⌘</c>；<c>Ctrl+</c> → <c>⌘</c>；
    ///         <c>Alt+</c> → <c>⌥</c>；提示末尾的 <c>Delete</c> → <c>⌫</c>。
    ///     </para>
    /// </summary>
    public static string Localize(string text) => IsMac ? ToMacNotation(text) : text;

    /// <summary><see cref="Localize" /> 的转换本体，不看当前平台（测试用）。</summary>
    internal static string ToMacNotation(string text) {
        if (string.IsNullOrEmpty(text)) return text;

        var result = text.Replace("Ctrl+Y", "⇧⌘Z", StringComparison.Ordinal)
            .Replace("Ctrl+Shift+", "⇧⌘", StringComparison.Ordinal)
            .Replace("Ctrl+", "⌘", StringComparison.Ordinal)
            .Replace("Alt+", "⌥", StringComparison.Ordinal);
        // 只换"两个空格 + Delete"结尾的快捷键提示，不碰 "Delete frame" 这类正文
        return TrailingDeleteKey().Replace(result, "$1⌫");
    }

    [GeneratedRegex(@"(\s{2})Delete$")]
    private static partial Regex TrailingDeleteKey();
}
