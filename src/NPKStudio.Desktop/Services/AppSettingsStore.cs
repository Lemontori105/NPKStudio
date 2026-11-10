using System.Text.Json;

namespace NPKStudio.Desktop.Services;

/// <summary>
///     全局界面设置（与具体素材无关的"观看习惯"）。
///     <para>
///         播放倍率：帧率是<b>观看习惯</b>而不是素材属性，所以全局一份，
///         不放进按 IMG 记忆的 <c>graphic-effect.json</c>。另存资源清单路径、界面语言。
///     </para>
///     <para>
///         存储：<c>%LOCALAPPDATA%\NPKStudio\settings.json</c>（macOS 上是 <c>~/Library/Application Support/NPKStudio</c>）。
///         读写失败一律回退默认值。
///     </para>
///     <para>
///         <b>多份实例</b>：主窗口和每个通道各自 <see cref="Load" /> 一份。<see cref="Save" /> 只写回
///         <b>这一份改过的键</b>（先重读文件再合并），不会用自己手里的旧值盖掉别的实例刚存的设置
///         ——以前整份覆盖时，关窗口时通道那份旧副本会把刚切换的界面语言改回去。
///     </para>
/// </summary>
public sealed class AppSettingsStore {
    /// <summary>默认倍率下标（3 = 1×，基准 8fps）。</summary>
    public const int DefaultRateIndex = 3;

    private const string FileName = "settings.json";
    private const string RateIndexKey = "playback.rateIndex";
    private const string ManifestPathKey = "pack.manifestPath";
    private const string LanguageKey = "ui.language";

    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);

    // 自上次读 / 存以来改过（含删除）的键；Save 只把这些合并进文件
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    private AppSettingsStore(string path) {
        _path = path;
    }

    /// <summary>
    ///     配置目录。
    ///     <para>
    ///         默认 <c>%LOCALAPPDATA%\NPKStudio</c>；环境变量 <c>NPKSTUDIO_CONFIG_DIR</c> 可覆盖
    ///         （自动化测试用它把配置写到临时目录，也方便做成便携版）。
    ///     </para>
    /// </summary>
    public static string ConfigDirectory {
        get {
            var overridden = Environment.GetEnvironmentVariable("NPKSTUDIO_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NPKStudio")
                : overridden;
        }
    }

    /// <summary>播放倍率下标（0~8）。</summary>
    public int RateIndex {
        get => _numbers.TryGetValue(RateIndexKey, out var value) ? value : DefaultRateIndex;
        set => SetNumber(RateIndexKey, value);
    }

    /// <summary>
    ///     记住的资源清单路径（<c>IMG: sprite/...</c> 的 txt）。
    ///     <para>设一次就一直生效，导出 NPK 时自动用它解析包内路径，不必每次都选文件。</para>
    /// </summary>
    public string? ManifestPath {
        get => GetString(ManifestPathKey);
        set => SetString(ManifestPathKey, value);
    }

    /// <summary>
    ///     界面语言（如 <c>zh-CN</c> / <c>en-US</c>）；<c>null</c> = 跟随系统（默认）。
    ///     <para>启动参数 <c>--lang</c> 优先于这里。</para>
    /// </summary>
    public string? Language {
        get => GetString(LanguageKey);
        set => SetString(LanguageKey, value);
    }

    /// <summary>读取配置；文件不存在或损坏时返回默认值。</summary>
    public static AppSettingsStore Load() {
        var store = new AppSettingsStore(Path.Combine(ConfigDirectory, FileName));
        ReadFile(store._path, store._numbers, store._strings);
        return store;
    }

    /// <summary>
    ///     落盘（调用方负责防抖/关窗 flush）。先重读文件，只把本实例改过的键合并进去，
    ///     然后本实例也同步成合并后的内容（能看到别的实例存过的设置）。
    /// </summary>
    public void Save() {
        try {
            var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadFile(_path, numbers, strings);

            foreach (var key in _changed) {
                numbers.Remove(key);
                strings.Remove(key);
                if (_numbers.TryGetValue(key, out var number)) {
                    numbers[key] = number;
                } else if (_strings.TryGetValue(key, out var text)) {
                    strings[key] = text;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // 手写 JSON：项目禁用了反射序列化（JsonSerializerIsReflectionEnabledByDefault=false）
            using (var stream = File.Create(_path))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) {
                writer.WriteStartObject();
                foreach (var pair in numbers) {
                    writer.WriteNumber(pair.Key, pair.Value);
                }

                foreach (var pair in strings) {
                    writer.WriteString(pair.Key, pair.Value);
                }

                writer.WriteEndObject();
            }

            Replace(_numbers, numbers);
            Replace(_strings, strings);
            _changed.Clear();
        } catch {
            // 写失败不该打断使用
        }
    }

    private string? GetString(string key) =>
        _strings.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private void SetNumber(string key, int value) {
        _numbers[key] = value;
        _changed.Add(key);
    }

    private void SetString(string key, string? value) {
        if (string.IsNullOrWhiteSpace(value)) {
            _strings.Remove(key);
        } else {
            _strings[key] = value;
        }

        _changed.Add(key);
    }

    private static void Replace<T>(Dictionary<string, T> target, Dictionary<string, T> source) {
        target.Clear();
        foreach (var pair in source) {
            target[pair.Key] = pair.Value;
        }
    }

    private static void ReadFile(string path, Dictionary<string, int> numbers, Dictionary<string, string> strings) {
        try {
            if (!File.Exists(path)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;

            // 先看类型再取值：JsonElement.TryGetInt32 遇到非 Number 会抛 InvalidOperationException
            // 而不是返回 false，被下面的 catch 吞掉后，后面所有设置都会丢失。
            foreach (var property in document.RootElement.EnumerateObject()) {
                switch (property.Value.ValueKind) {
                    case JsonValueKind.Number when property.Value.TryGetInt32(out var number):
                        numbers[property.Name] = number;
                        break;
                    case JsonValueKind.String:
                        strings[property.Name] = property.Value.GetString() ?? string.Empty;
                        break;
                }
            }
        } catch {
            // 配置损坏不影响启动
        }
    }
}
