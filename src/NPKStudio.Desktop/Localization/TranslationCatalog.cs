using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPKStudio.Desktop.Input;

namespace NPKStudio.Desktop.Localization;

/// <summary>
///     词表的 JSON 序列化上下文（源生成）。
///     <para>用它替代 <c>JsonSerializer.Deserialize&lt;T&gt;</c> 的反射路径：词表在启动时加载一次，
///     但反射元数据构建仍会分配并触发元数据缓存，源生成后完全走编译期生成的代码。</para>
/// </summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LocalizationJsonContext : JsonSerializerContext { }

/// <summary>
///     界面文案的运行时词表。
///     <para>
///         默认从程序集内嵌的 <c>zh-CN.json</c> 加载；调用 <see cref="SetLanguage" /> 可切换到其它语言，
///         只要同名 json 存在。切换后所有通过 <c>{loc:Tr Key}</c> 绑定的控件会自动刷新，
///         无需重启窗口。
///     </para>
///     <para>
///         <b>扩展方式</b>：复制 <c>Localization/zh-CN.json</c> 为 <c>zh-TW.json</c> / <c>ko-KR.json</c>，
///         翻译值即可，key 不要改。新增界面文字时先在本文件加 key，再到 json 里补两种语言的值。
///     </para>
/// </summary>
public sealed class TranslationCatalog : INotifyPropertyChanged {
    private const string Fallback = "zh-CN";

    /// <summary>C# 索引器 <c>this[string]</c> 在反射里的属性名。</summary>
    internal const string IndexerPropertyName = "Item";

    private static readonly Lazy<TranslationCatalog> LazyInstance = new(() => new TranslationCatalog());

    private Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);

    private TranslationCatalog() {
        Load(Fallback);
    }

    /// <summary>全局唯一实例（XAML 绑定源）。</summary>
    public static TranslationCatalog Instance => LazyInstance.Value;

    /// <summary>当前语言代码，如 <c>zh-CN</c>。</summary>
    public string CurrentLanguage { get; private set; } = Fallback;

    /// <summary>取文案；key 不存在时返回 key 本身，便于快速发现漏翻。</summary>
    public string this[string key] =>
        _strings.TryGetValue(key, out var value) ? value : key;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>带参数的格式化取词，例如 <c>F("status.opened", name, count)</c>。</summary>
    public string F(string key, params object?[] args) {
        var template = this[key];
        try {
            return string.Format(template, args);
        } catch (FormatException) {
            return template;
        }
    }

    /// <summary>切换语言。找不到对应词表时保持当前语言并返回 <c>false</c>。</summary>
    public bool SetLanguage(string language) {
        if (string.IsNullOrWhiteSpace(language) || !Load(language)) return false;

        CurrentLanguage = language;
        // 索引器绑定的变更通知要用索引器的属性名 "Item"，不是 WPF 习惯的 "Item[]"：
        // Avalonia 11.3 的 {loc:Tr} 走反射绑定 ReflectionIndexerNode，它的 ShouldUpdate 拿通知里的名字
        // GetDeclaredProperty(name)，是带索引参数的属性才刷新（反编译查 Avalonia.Base 11.3.22）。
        // 用 "Item[]" 时找不到属性，XAML 里所有 {loc:Tr} 文字切换语言后都不会变。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(IndexerPropertyName));
        return true;
    }

    /// <summary>当前程序集内可用的语言（按内嵌资源名探测）。</summary>
    public static IReadOnlyList<string> AvailableLanguages() {
        var prefix = "NPKStudio.Desktop.Localization.";
        return typeof(TranslationCatalog).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[prefix.Length..^5])
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool Load(string language) {
        var assembly = typeof(TranslationCatalog).Assembly;
        var name = $"NPKStudio.Desktop.Localization.{language}.json";
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) {
            return language.Equals(Fallback, StringComparison.OrdinalIgnoreCase) && _strings.Count > 0;
        }

        using var reader = new StreamReader(stream);
        var parsed = JsonSerializer.Deserialize(reader.ReadToEnd(),
            LocalizationJsonContext.Default.DictionaryStringString);
        if (parsed is null || parsed.Count == 0) return false;

        // 词表里的快捷键按 Windows 写（Ctrl+O）；macOS 上统一换成 Mac 写法（⌘O、⇧⌘Z、⌫），见 PlatformShortcuts.Localize
        _strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parsed) {
            _strings[key] = PlatformShortcuts.Localize(value);
        }

        return true;
    }
}
