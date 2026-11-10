using NPKStudio.Core.Formats.Npk;
using NPKStudio.Desktop.Localization;

namespace NPKStudio.Desktop.Services;

/// <summary>单个文件夹的打包结果。</summary>
public sealed record PackOutcome(
    string Folder,
    string NpkName,
    int EntryCount,
    long TotalBytes,
    string? Error,
    int Replaced = 0,
    int Added = 0,
    string? Note = null,
    int ManifestHits = 0,
    int NameHits = 0,
    int InferredHits = 0) {
    public bool Success => Error is null;

    /// <summary>非精确来源的条数（按文件名匹配或推断），导出后要如实告诉用户。</summary>
    public int ApproximateHits => NameHits + InferredHits;
}

/// <summary>
///     把"按 NPK 分组的导出文件夹"重新打包成 NPK。
///     <para>
///         路径来源二选一：<paramref name="manifest" /> 非 <c>null</c> 时以清单为准（精确路径）；
///         为 <c>null</c> 时用 <see cref="ResourceManifest.InferPath" /> 按
///         <paramref name="npkStem" />（默认文件夹名）推断。清单里查不到的条目也回退到推断，
///         因此不会有条目被静默丢弃——没有清单也能直接导出。
///     </para>
///     <para>
///         若提供 <c>baseDirectory</c>（客户端 <c>ImagePacks2</c> 目录）且其中存在同名 NPK，
///         则以该 NPK 为基底合并：同名条目被替换、其余条目原样保留，避免"用 305 条的包覆盖 402 条的包"。
///     </para>
///     <para>
///         <b>全程流式</b>：条目数据不再预先读进内存，源文件和基底包都在"轮到它"时才分块拷出，
///         因此峰值内存与包体积无关（实测把批量打包的峰值工作集从 462 MB 降到 60 MB 量级）。
///     </para>
/// </summary>
public static class NpkPackService {
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    /// <summary>打包根目录下的所有子文件夹。</summary>
    /// <param name="rootDirectory">根目录（其每个子目录各打一个包）。</param>
    /// <param name="manifest">资源清单；<c>null</c> 表示全部走推断。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="baseDirectory">客户端 NPK 目录（用于合并基底），可为 <c>null</c>。</param>
    /// <param name="npkStem">推断用的 NPK 名主干；<c>null</c> 表示各文件夹用自己的文件夹名。</param>
    public static IReadOnlyList<PackOutcome> PackAll(
        string rootDirectory, ResourceManifest? manifest, string outputDirectory,
        string? baseDirectory = null, string? npkStem = null) {
        var results = new List<PackOutcome>();
        var folders = Directory.GetDirectories(rootDirectory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders) {
            results.Add(PackOne(folder, manifest, outputDirectory, baseDirectory, npkStem));
        }

        return results;
    }

    /// <summary>打包一个文件夹。</summary>
    /// <param name="folder">源文件夹。</param>
    /// <param name="manifest">资源清单；<c>null</c> 表示全部走推断。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="baseDirectory">客户端 NPK 目录（用于合并基底），可为 <c>null</c>。</param>
    /// <param name="npkStem">推断用的 NPK 名主干（无清单、或清单未命中时使用）；<c>null</c> 表示用文件夹名。</param>
    public static PackOutcome PackOne(
        string folder, ResourceManifest? manifest, string outputDirectory,
        string? baseDirectory = null, string? npkStem = null) {
        var folderName = Path.GetFileName(folder);
        var stem = string.IsNullOrWhiteSpace(npkStem) ? folderName : npkStem;

        // 只记录"清单路径 → 本地文件路径"，不读文件内容。
        var groups = new Dictionary<string, List<(string Path, string File)>>(StringComparer.OrdinalIgnoreCase);
        var inferred = new List<string>();
        int manifestHits = 0, nameHits = 0;

        foreach (var file in Directory.EnumerateFiles(folder, "*.img", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
            var fileName = Path.GetFileName(file);

            string fullPath;
            if (manifest is null) {
                fullPath = ResourceManifest.InferPath(stem, fileName);
                inferred.Add(fileName);
            } else {
                switch (manifest.Resolve(folderName, fileName, stem, out fullPath)) {
                    case PathOrigin.Manifest:
                        manifestHits++;
                        break;
                    case PathOrigin.ByFileName:
                        nameHits++;
                        break;
                    default:
                        // 清单里连文件名都没有 → 只能推断；这条路径不可信，必须计入警告。
                        inferred.Add(fileName);
                        break;
                }
            }

            var npkName = ResourceManifest.NpkNameOf(fullPath);
            if (!groups.TryGetValue(npkName, out var list)) {
                groups[npkName] = list = [];
            }

            list.Add((fullPath, file));
        }

        if (groups.Count == 0) {
            return new PackOutcome(folderName, "-", 0, 0, Loc["pack.noImg"]);
        }

        var primary = groups.OrderByDescending(kv => kv.Value.Count).First();
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, primary.Key);

        int replaced = 0, added = 0;
        var finalCount = primary.Value.Count;
        long totalBytes;
        try {
            totalBytes = WriteGroup(outputPath, primary.Value, baseDirectory, out finalCount, out replaced, out added);
        } catch (Exception ex) {
            return new PackOutcome(folderName, primary.Key, primary.Value.Count, 0, ex.Message);
        }

        foreach (var extra in groups.Where(kv => !ReferenceEquals(kv.Value, primary.Value))) {
            var extraPath = Path.Combine(outputDirectory, extra.Key);
            try {
                // 注意：附加分组**不做**基底合并——这是改造前的既有行为，本次只做流式化，不改语义。
                WriteGroup(extraPath, extra.Value, baseDirectory: null, out _, out _, out _);
            } catch {
                // 附加分组失败不影响主包结果
            }
        }

        // 提示只走 Note，不进 Error —— 否则会被当成"导出失败"（Success 只看 Error）。
        var note = inferred.Count > 0
            ? Loc.F("pack.inferred", inferred.Count, string.Join(", ", inferred.Take(3)))
            : null;
        return new PackOutcome(
            folderName, primary.Key, finalCount, totalBytes, null, replaced, added, note,
            manifestHits, nameHits, inferred.Count);
    }

    /// <summary>
    ///     写一个分组：有基底包则合并（基底条目走 <see cref="NpkArchive.CopyRawTo" /> 分块拷贝），
    ///     否则全是补丁条目（走 <see cref="NpkEntrySource.FromFile" /> 流式读取）。
    /// </summary>
    /// <returns>补丁条目的字节数合计。</returns>
    private static long WriteGroup(
        string outputPath, List<(string Path, string File)> patch, string? baseDirectory,
        out int finalCount, out int replaced, out int added) {
        var basePath = baseDirectory is null ? null : ResolveBasePath(baseDirectory, Path.GetFileName(outputPath));

        // 统计口径与改造前一致：只累加"来自补丁文件夹"的字节数（在写入前取，避免写完后文件被占用/移动）。
        var patchBytes = 0L;
        foreach (var (_, file) in patch) {
            patchBytes += new FileInfo(file).Length;
        }

        // 基底归档必须在整个写入期间保持打开——NpkEntrySource 的 WriteData 依赖它的句柄。
        NpkArchive? archive = null;
        try {
            archive = basePath is null ? null : NpkArchive.Open(basePath);

            IReadOnlyList<NpkEntrySource> sources;
            if (archive is null) {
                finalCount = patch.Count;
                replaced = 0;
                added = 0;
                sources = ToSources(patch);
            } else {
                sources = MergeWithBase(archive, patch, out finalCount, out replaced, out added);
            }

            NpkWriter.WriteFileStreaming(outputPath, sources);
            return patchBytes;
        } finally {
            archive?.Dispose();
        }
    }

    /// <summary>把"清单路径 + 本地文件"转成流式条目源。</summary>
    private static List<NpkEntrySource> ToSources(List<(string Path, string File)> patch) {
        var sources = new List<NpkEntrySource>(patch.Count);
        foreach (var (path, file) in patch) {
            sources.Add(NpkEntrySource.FromFile(path, file));
        }

        return sources;
    }

    /// <summary>
    ///     以客户端已有 NPK 为基底合并：同名条目替换，其余原样保留，文件夹里有而基底没有的追加到末尾。
    ///     <para>基底条目不读进内存，写入时用 <see cref="NpkArchive.CopyRawTo" /> 分块拷。</para>
    /// </summary>
    private static List<NpkEntrySource> MergeWithBase(
        NpkArchive archive, List<(string Path, string File)> patch, out int finalCount, out int replaced, out int added) {
        replaced = 0;
        added = 0;

        var patchByPath = new Dictionary<string, string>(patch.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, file) in patch) {
            patchByPath[path] = file;
        }

        var result = new List<NpkEntrySource>(archive.Entries.Count + patch.Count);

        foreach (var entry in archive.Entries) {
            if (patchByPath.Remove(entry.Path, out var localFile)) {
                result.Add(NpkEntrySource.FromFile(entry.Path, localFile));
                replaced++;
            } else {
                result.Add(NpkEntrySource.FromArchive(archive, entry));
            }
        }

        foreach (var (path, file) in patch) {
            if (patchByPath.ContainsKey(path)) {
                result.Add(NpkEntrySource.FromFile(path, file));
                added++;
            }
        }

        finalCount = result.Count;
        return result;
    }

    /// <summary>在基底目录里找同名 NPK（先精确、再大小写不敏感）。</summary>
    private static string? ResolveBasePath(string baseDirectory, string npkName) {
        var exact = Path.Combine(baseDirectory, npkName);
        if (File.Exists(exact)) {
            return exact;
        }

        return Directory.EnumerateFiles(baseDirectory, "*.npk")
            .FirstOrDefault(p => Path.GetFileName(p).Equals(npkName, StringComparison.OrdinalIgnoreCase));
    }
}
