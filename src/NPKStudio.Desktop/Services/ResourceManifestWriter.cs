using System.Text;

using NPKStudio.Core.Diagnostics;
using NPKStudio.Core.Formats.Npk;
using NPKStudio.Desktop.Localization;

namespace NPKStudio.Desktop.Services;

/// <summary>一次清单扫描的结果：扫到的 NPK 数、写出的路径条数、解析失败的 NPK 数。</summary>
internal sealed record ManifestScanResult(int NpkCount, int PathCount, int FailedCount);

/// <summary>
///     从客户端已有的 NPK 目录反向生成资源清单——这就是"清单从哪来"的答案。
///     <para>
///         NPK 的条目表里<b>本来就有每个条目的完整包内路径</b>，所以只要读条目表即可，
///         既不需要 PVF，也不需要按文件名猜（因此不受
///         <c>sprite_character_atpriest_effect.npk</c> 这类反例影响）。
///     </para>
///     <para>
///         <b>流式</b>：逐个 <c>NpkArchive.Open</c> → 写出该包的路径 → <c>Dispose</c>，
///         绝不把整个 <c>ImagePacks2</c>（数百个包、数万条目）同时打开或缓存；
///         输出也是边扫边写，峰值内存只与"单个 NPK 的条目表"有关。
///     </para>
/// </summary>
internal static class ResourceManifestWriter {
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    /// <summary>清单行前缀，与 <see cref="ResourceManifest.Load" /> 的解析口径一致。</summary>
    private const string LinePrefix = "IMG: ";

    /// <summary>扫描用的文件模式。</summary>
    private const string SearchPattern = "*.npk";

    /// <summary>
    ///     扫描目录（递归）下所有 NPK，把包内条目路径逐行写成清单。
    /// </summary>
    /// <param name="npkDirectory">装有 NPK 的目录（典型是客户端的 <c>ImagePacks2</c>）。</param>
    /// <param name="outputPath">清单输出路径（<c>*.txt</c>）。</param>
    /// <returns>扫描结果。</returns>
    /// <exception cref="ArgumentException">参数为空。</exception>
    /// <exception cref="DirectoryNotFoundException">目录不存在。</exception>
    public static ManifestScanResult Write(string npkDirectory, string outputPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(npkDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var full = Path.GetFullPath(npkDirectory);
        if (!Directory.Exists(full)) {
            throw new DirectoryNotFoundException(Loc.F("error.dirNotFound", npkDirectory));
        }

        var outputFull = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(outputFull);
        if (!string.IsNullOrEmpty(outputDirectory)) {
            Directory.CreateDirectory(outputDirectory);
        }

        var options = new EnumerationOptions {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        var npkCount = 0;
        var pathCount = 0;
        var failed = 0;

        using (var writer = new StreamWriter(outputFull, false, new UTF8Encoding(false))) {
            foreach (var file in Directory.EnumerateFiles(full, SearchPattern, options)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
                try {
                    using var archive = NpkArchive.Open(file);
                    npkCount++;

                    foreach (var entry in archive.Entries) {
                        writer.Write(LinePrefix);
                        writer.WriteLine(entry.Path);
                        pathCount++;
                    }
                } catch (Exception ex) {
                    // 单个坏包不该让整次扫描失败——客户端里确实存在被部分损坏的 NPK。
                    failed++;
                    TraceLog.Instance.Warn($"生成清单时跳过无法解析的 NPK：{file}", ex);
                }
            }
        }

        TraceLog.Instance.Info($"资源清单已生成：{outputFull}（{npkCount} 个 NPK / {pathCount} 条路径 / 失败 {failed}）。");
        return new ManifestScanResult(npkCount, pathCount, failed);
    }
}
