using NPKStudio.Core.Diagnostics;
using NPKStudio.Core.Exceptions;
using NPKStudio.Core.Formats;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.ViewModels;

namespace NPKStudio.Desktop.Services;

/// <summary>
///     "NPK 源目录"：一个文件夹，里面是按 NPK 分包导出的若干 <c>.img</c> 文件。
///     <para>
///         这是"文件夹模式"的数据源，与 <c>NpkArchive</c>（NPK 模式）并列。
///         <b>枚举时只读文件长度</b>（<c>FileInfo.Length</c>），不读内容——
///         305 个 img 的文件夹也不会因此产生 IO 峰值；内容等选中该条目时才 <see cref="Load" />。
///     </para>
///     <para>
///         <b>不猜 NPK 内路径</b>：文件夹结构里没有可靠的包内路径信息（实测有
///         <c>sprite_character_atpriest_effect.npk</c> 里装的是 <c>sprite/character/priest/effect/...</c>），
///         所以 <see cref="EntryItem.Path" /> 一律用相对源目录的路径，只作显示。
///     </para>
/// </summary>
public sealed class FolderEntrySource {
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    private readonly List<EntryItem> _entries = [];

    private FolderEntrySource(string rootPath) {
        RootPath = rootPath;
        Name = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(Name)) {
            Name = rootPath;
        }
    }

    private FolderEntrySource(string rootPath, string name) {
        RootPath = rootPath;
        Name = name;
    }

    /// <summary>源目录绝对路径。</summary>
    public string RootPath { get; }

    /// <summary>文件夹名（用于窗口标题与打包时的 NPK 名）。</summary>
    public string Name { get; }

    /// <summary>该目录下（递归）所有 <c>.img</c> 条目，按相对路径排序。</summary>
    public IReadOnlyList<EntryItem> Entries => _entries;

    /// <summary>枚举期间被跳过的文件数（无权限/被占用等）。</summary>
    public int SkippedCount { get; private set; }

    /// <summary>
    ///     打开一个源目录（只做枚举，不读任何 img 内容）。
    /// </summary>
    /// <param name="rootPath">目录路径。</param>
    /// <returns>源目录对象。</returns>
    /// <exception cref="DirectoryNotFoundException">目录不存在。</exception>
    public static FolderEntrySource Open(string rootPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var full = Path.GetFullPath(rootPath);
        if (!Directory.Exists(full)) {
            throw new DirectoryNotFoundException(Loc.F("error.dirNotFound", rootPath));
        }

        var source = new FolderEntrySource(full);
        source.Enumerate();
        return source;
    }

    /// <summary>
    ///     只打开<b>单个 <c>.img</c> 文件</b>：把它当成"只有一个条目的源目录"。
    ///     <para>
    ///         这样单 img 复用整套文件夹模式流程（帧加载、换图、写回、导出 NPK 都不用改）：
    ///         <see cref="RootPath" /> 是该文件所在目录，<see cref="Name" /> 是文件名主干，
    ///         因此「另存为 NPK」的默认文件名就是 <c>&lt;img 名&gt;.NPK</c>——
    ///         用户把它改成符合 DNF 规则的 NPK 名，推断出的包内路径就是对的。
    ///     </para>
    /// </summary>
    /// <param name="imgPath">单个 <c>.img</c> 的路径。</param>
    /// <returns>只含该文件的源对象。</returns>
    /// <exception cref="ArgumentException">路径为空、或无法确定所在目录。</exception>
    /// <exception cref="FileNotFoundException">文件不存在。</exception>
    public static FolderEntrySource OpenSingleFile(string imgPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(imgPath);

        var full = Path.GetFullPath(imgPath);
        if (!File.Exists(full)) {
            throw new FileNotFoundException(Loc.F("error.fileNotFound", imgPath), full);
        }

        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory)) {
            throw new ArgumentException($"无法确定文件所在目录：{imgPath}", nameof(imgPath));
        }

        var fileName = Path.GetFileName(full);
        var source = new FolderEntrySource(directory, Path.GetFileNameWithoutExtension(full));
        source._entries.Add(EntryItem.FromFile(fileName, full, new FileInfo(full).Length));
        return source;
    }

    /// <summary>
    ///     读取并解析某个条目的 IMG 内容。
    /// </summary>
    /// <param name="item">来自本源的条目。</param>
    /// <returns>解析后的 IMG。</returns>
    /// <exception cref="ArgumentException">条目不属于文件夹来源。</exception>
    /// <exception cref="FileNotFoundException">文件已不存在。</exception>
    public ImgFile Load(EntryItem item) {
        ArgumentNullException.ThrowIfNull(item);
        var path = item.FilePath ?? throw new ArgumentException("该条目不是文件夹来源。", nameof(item));

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return ImgFile.Read(stream, item.Path, stream.Length);
    }

    /// <summary>
    ///     把内存中的 IMG <b>原子写回</b>原文件：先写 <c>.saving</c> 临时文件 → 自检 → 替换。
    ///     <para>
    ///         自检会重新解析临时文件并核对版本与帧数，避免"写到一半失败把原文件毁了"或写出结构性损坏的 IMG。
    ///     </para>
    /// </summary>
    /// <param name="item">要写回的条目。</param>
    /// <param name="image">内存中的 IMG（可能已被编辑）。</param>
    /// <exception cref="ArgumentException">条目不属于文件夹来源。</exception>
    /// <exception cref="ImgFormatException">写出后自检失败。</exception>
    /// <exception cref="IOException">目标文件被占用，无法替换。</exception>
    public void Save(EntryItem item, ImgFile image) {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(image);
        var path = item.FilePath ?? throw new ArgumentException("该条目不是文件夹来源。", nameof(item));

        var temp = path + ".saving";
        try {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024)) {
                image.Write(stream);
            }

            Verify(temp, image, item);

            try {
                File.Move(temp, path, overwrite: true);
            } catch (UnauthorizedAccessException ex) {
                throw new IOException(Loc.F("error.fileLocked", path), ex);
            }
        } catch {
            TryDelete(temp);
            throw;
        }

        TraceLog.Instance.Info($"IMG 已写回：{path}（{image.Frames.Count} 帧）。");
    }

    /// <summary>递归枚举 <c>.img</c>（大小写不敏感），只取文件长度，不读内容。</summary>
    private void Enumerate() {
        var options = new EnumerationOptions {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        var files = new List<(string Full, string Relative, long Length)>();
        foreach (var file in Directory.EnumerateFiles(RootPath, "*", options)) {
            if (!file.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            try {
                var length = new FileInfo(file).Length;
                var relative = Path.GetRelativePath(RootPath, file).Replace('\\', '/');
                files.Add((file, relative, length));
            } catch (IOException ex) {
                SkippedCount++;
                TraceLog.Instance.Warn($"枚举源目录时跳过文件：{file}", ex);
            } catch (UnauthorizedAccessException ex) {
                SkippedCount++;
                TraceLog.Instance.Warn($"枚举源目录时无权限：{file}", ex);
            }
        }

        files.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase));
        foreach (var (full, relative, length) in files) {
            _entries.Add(EntryItem.FromFile(relative, full, length));
        }
    }

    /// <summary>写出后自检：重新解析临时文件，核对版本与帧数。</summary>
    private static void Verify(string tempPath, ImgFile image, EntryItem item) {
        ImgFile reparsed;
        using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            reparsed = ImgFile.Read(stream, item.Path, stream.Length);
        }

        if (reparsed.Version != image.Version) {
            throw new ImgFormatException(
                Loc.F("error.writeBackVersion", image.Version, reparsed.Version, item.Path));
        }

        if (reparsed.Frames.Count != image.Frames.Count) {
            throw new ImgFormatException(
                Loc.F("error.writeBackFrames", image.Frames.Count, reparsed.Frames.Count, item.Path));
        }
    }

    private static void TryDelete(string path) {
        if (!File.Exists(path)) {
            return;
        }

        try {
            File.Delete(path);
        } catch (IOException ex) {
            TraceLog.Instance.Warn($"清理临时文件失败：{path}", ex);
        }
    }

    /// <summary>返回可读文本，便于日志定位。</summary>
    public override string ToString() => $"FolderEntrySource({RootPath}, {_entries.Count} img)";
}
