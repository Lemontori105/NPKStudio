using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NPKStudio.Desktop.Localization;

/// <summary>
///     界面语言的选择规则：启动参数 <c>--lang</c> &gt; 设置里保存的语言 &gt; 跟随系统。
///     <para>跟随系统：系统首选语言是中文（<c>zh</c> 开头）用 <c>zh-CN</c>，其余一律 <c>en-US</c>。</para>
///     <para>
///         项目开了 <c>InvariantGlobalization</c>，<c>CultureInfo.CurrentUICulture</c> 永远是不变区域性，
///         所以系统语言要直接问系统：macOS 读 <c>AppleLanguages</c>，Windows 调 <c>GetUserDefaultUILanguage</c>
///         （https://learn.microsoft.com/en-us/windows/win32/api/winnls/nf-winnls-getuserdefaultuilanguage ），
///         其余读 <c>LANGUAGE</c> / <c>LC_ALL</c> / <c>LC_MESSAGES</c> / <c>LANG</c>。
///     </para>
/// </summary>
internal static partial class UiLanguage {
    /// <summary>简体中文。</summary>
    public const string Chinese = "zh-CN";

    /// <summary>英文。</summary>
    public const string English = "en-US";

    private const int LangChinese = 0x04;

    /// <summary>按优先级挑出要用的语言代码。</summary>
    /// <param name="commandLine">启动参数 <c>--lang</c> 的值（没有则 <c>null</c>）。</param>
    /// <param name="saved">设置里保存的语言（<c>null</c> = 跟随系统）。</param>
    /// <param name="systemTag">系统首选语言标签（如 <c>zh-Hans-CN</c>、<c>en-US</c>；不知道时为 <c>null</c>）。</param>
    public static string Resolve(string? commandLine, string? saved, string? systemTag) {
        if (!string.IsNullOrWhiteSpace(commandLine)) return commandLine;
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        return FromSystemTag(systemTag);
    }

    /// <summary>系统语言标签 → 界面语言：中文用中文，其余（包括不知道）用英文。</summary>
    public static string FromSystemTag(string? tag) =>
        tag is not null && tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;

    /// <summary>跟随系统时实际用哪种语言。</summary>
    public static string SystemLanguage() => FromSystemTag(DetectSystemTag());

    /// <summary>读系统首选语言标签；读不到返回 <c>null</c>。</summary>
    public static string? DetectSystemTag() {
        // macOS / Windows 先读系统的界面语言：LANG 这类环境变量是终端的区域设置，
        // 从终端启动时会带进来，和"系统设置 → 语言"不一定一致。读不到再看环境变量（Linux 只看这个）。
        try {
            if (OperatingSystem.IsMacOS() && ReadAppleLanguage() is { } apple) return apple;
            if (OperatingSystem.IsWindows()) return (GetUserDefaultUILanguage() & 0x3FF) == LangChinese ? "zh" : "en";
        } catch (Exception) {
            // 读不到就看环境变量
        }

        foreach (var key in new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" }) {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith('C') && !value.StartsWith("POSIX", StringComparison.Ordinal)) {
                return value;
            }
        }

        return null;
    }

    /// <summary>解析 <c>defaults read -g AppleLanguages</c> 的输出，取第一个语言。</summary>
    internal static string? ParseAppleLanguages(string output) {
        var match = QuotedOrBare().Match(output ?? string.Empty);
        return match.Success ? match.Groups["tag"].Value : null;
    }

    private static string? ReadAppleLanguage() {
        var info = new ProcessStartInfo("/usr/bin/defaults") {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "read", "-g", "AppleLanguages" }) info.ArgumentList.Add(arg);

        using var process = Process.Start(info);
        if (process is null) return null;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(3000);
        return ParseAppleLanguages(output);
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    // ( "zh-Hans-CN", "en-CN" ) 或 ( en, ja )：取括号后的第一个标签
    [GeneratedRegex("""\(\s*"?(?<tag>[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]+)*)""")]
    private static partial Regex QuotedOrBare();
}
