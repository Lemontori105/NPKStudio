using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NPKStudio.Core.Diagnostics;

namespace NPKStudio.Desktop.Services;

/// <summary>
///     一个外部工具包（音效功能依赖的命令行工具）。程序本身不带任何第三方包，这些工具由用户按需安装。
/// </summary>
/// <param name="Id">包标识，也是 Homebrew formula 名。</param>
/// <param name="DisplayName">界面上显示的名字。</param>
/// <param name="PurposeKey">"用途"文案的词表 key。</param>
/// <param name="Commands">这个包提供、程序会调用的命令（全部找到才算已安装；版本取第一个）。</param>
/// <param name="VersionArgument">查版本的参数。</param>
/// <param name="FallbackHomepage">联网全部失败时才用的官网首页（正常情况下用 Homebrew API 返回的 homepage 再现场抓下载页）。</param>
internal sealed record ExternalToolPackage(
    string Id,
    string DisplayName,
    string PurposeKey,
    IReadOnlyList<string> Commands,
    string VersionArgument,
    string FallbackHomepage);

/// <summary>本机安装情况。</summary>
/// <param name="Version">已安装版本；没装或认不出版本号时为 <c>null</c>。</param>
/// <param name="MissingCommands">缺少的命令（空 = 全都在）。</param>
internal sealed record InstalledToolInfo(string? Version, IReadOnlyList<string> MissingCommands) {
    /// <summary>所有命令都找到了。</summary>
    public bool IsInstalled => MissingCommands.Count == 0;
}

/// <summary>联网查到的最新版本信息。</summary>
/// <param name="Version">最新稳定版本；查不到时为 <c>null</c>。</param>
/// <param name="Homepage">官网（Homebrew API 里的 homepage）。</param>
/// <param name="TimedOut">是否因超时 / 连不上而失败。</param>
internal sealed record LatestToolInfo(string? Version, string? Homepage, bool TimedOut);

/// <summary>安装结果。</summary>
/// <param name="Success">是否成功。</param>
/// <param name="TimedOut">是否因网络超时 / 连不上而失败（界面据此显示下载页链接）。</param>
/// <param name="Message">失败原因（工具输出里的最后一条错误）。</param>
internal sealed record ToolInstallResult(bool Success, bool TimedOut, string? Message);

/// <summary>
///     外部工具清单与纯函数（版本解析 / 比较、从网页里找下载链接）。
/// </summary>
internal static partial class ExternalToolCatalog {
    /// <summary>音效功能依赖的两个包。</summary>
    public static IReadOnlyList<ExternalToolPackage> Packages { get; } = [
        new("ffmpeg", "FFmpeg", "settings.tool.ffmpeg.purpose", ["ffmpeg", "ffplay"], "-version", "https://ffmpeg.org/"),
        new("vorbis-tools", "vorbis-tools", "settings.tool.vorbis.purpose", ["oggenc"], "--version", "https://xiph.org/")
    ];

    /// <summary>从命令输出的第一行取版本号（如 <c>ffmpeg version 9.0.2</c>、<c>oggenc from vorbis-tools 1.4.3</c>）。</summary>
    public static string? ParseVersion(string output) {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        var match = VersionPattern().Match(firstLine);
        return match.Success ? match.Value : null;
    }

    /// <summary>比较两个点分版本号（缺的段按 0 算；不是数字的段按 0 算）。</summary>
    /// <returns>负数 = <paramref name="a" /> 较旧，0 = 相同，正数 = <paramref name="a" /> 较新。</returns>
    public static int CompareVersions(string a, string b) {
        var left = a.Split('.');
        var right = b.Split('.');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++) {
            var x = i < left.Length && int.TryParse(left[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0;
            var y = i < right.Length && int.TryParse(right[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
            if (x != y) return x.CompareTo(y);
        }

        return 0;
    }

    /// <summary>
    ///     从官网首页 HTML 里找"下载"页面：第一个链接文字或地址里带 download 的 <c>&lt;a&gt;</c>，转成绝对地址。
    ///     GitHub 仓库首页直接用它的 tags 页（列出最新版本）。找不到返回 <c>null</c>。
    /// </summary>
    public static Uri? FindDownloadPage(Uri homepage, string? html) {
        if (homepage.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
            return new Uri(homepage.ToString().TrimEnd('/') + "/tags");
        }

        if (string.IsNullOrEmpty(html)) return null;

        foreach (Match match in AnchorPattern().Matches(html)) {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            var text = match.Groups["text"].Value;
            if (href.Length == 0 || href.StartsWith('#') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!href.Contains("download", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("download", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (Uri.TryCreate(homepage, href, out var absolute) && absolute.Scheme is "http" or "https") {
                return absolute;
            }
        }

        return null;
    }

    /// <summary>从 Homebrew formula API 的 JSON 里取最新稳定版与官网。</summary>
    public static (string? Version, string? Homepage) ParseFormulaJson(string json) {
        try {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? version = null;
            if (root.TryGetProperty("versions", out var versions) && versions.TryGetProperty("stable", out var stable)
                && stable.ValueKind == JsonValueKind.String) {
                version = stable.GetString();
            }

            var homepage = root.TryGetProperty("homepage", out var page) && page.ValueKind == JsonValueKind.String
                ? page.GetString()
                : null;
            return (version, homepage);
        } catch (JsonException) {
            return (null, null);
        }
    }

    /// <summary>Homebrew / curl 输出里表示"网络不通 / 超时"的特征。</summary>
    public static bool LooksLikeNetworkFailure(string line) =>
        NetworkFailurePattern().IsMatch(line);

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex VersionPattern();

    [GeneratedRegex("""<a\s[^>]*?href\s*=\s*["'](?<href>[^"']+)["'][^>]*>(?<text>.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorPattern();

    [GeneratedRegex(@"timed out|timeout|could not resolve host|failed to connect|connection refused|connection reset|curl: \((6|7|28|35|56)\)|network is unreachable", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkFailurePattern();
}

/// <summary>
///     外部工具的检测、查最新版、找下载页、安装（macOS 走 Homebrew）。
///     <para>
///         版本来源用 Homebrew 的官方 API（<c>formulae.brew.sh</c>，https://formulae.brew.sh/docs/api/ ）：安装也走 Homebrew，
///         所以"最新版本"就是点按钮后实际会装上的版本。联网统一走 <see cref="SystemProxy" />。
///     </para>
/// </summary>
internal sealed class ExternalToolService : IDisposable {
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);
    private static readonly string[] BrewLocations = ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"];

    private readonly HttpClient _http;

    public ExternalToolService() {
        Proxy = SystemProxy.Detect();
        var handler = new HttpClientHandler();
        if (Proxy is not null) {
            handler.Proxy = new WebProxy(Proxy);
            handler.UseProxy = true;
        }

        _http = new HttpClient(handler) { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"NPKStudio/{AppInfo.Version}");
    }

    /// <summary>检测到的代理（没有为 <c>null</c>）。</summary>
    public Uri? Proxy { get; }

    /// <summary>能否在本机自动安装（目前只支持 macOS + Homebrew）。</summary>
    public static bool CanAutoInstall => OperatingSystem.IsMacOS();

    /// <summary>Homebrew 的路径；没装返回 <c>null</c>。</summary>
    public static string? FindBrew() => BrewLocations.FirstOrDefault(File.Exists);

    /// <summary>查本机安装情况：每个命令都要找得到，版本取第一个命令的输出。</summary>
    public static async Task<InstalledToolInfo> DetectInstalledAsync(ExternalToolPackage package) {
        var missing = package.Commands.Where(command => AudioTools.Find(command) is null).ToList();
        if (missing.Count > 0) return new InstalledToolInfo(null, missing);

        var exe = AudioTools.Find(package.Commands[0])!;
        var (output, _) = await RunCaptureAsync(exe, [package.VersionArgument], TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return new InstalledToolInfo(ExternalToolCatalog.ParseVersion(output), []);
    }

    /// <summary>从 Homebrew API 查最新稳定版和官网。</summary>
    public async Task<LatestToolInfo> FetchLatestAsync(ExternalToolPackage package) {
        try {
            var json = await _http.GetStringAsync($"https://formulae.brew.sh/api/formula/{package.Id}.json").ConfigureAwait(false);
            var (version, homepage) = ExternalToolCatalog.ParseFormulaJson(json);
            return new LatestToolInfo(version, homepage, false);
        } catch (TaskCanceledException) {
            return new LatestToolInfo(null, null, true);
        } catch (HttpRequestException ex) {
            TraceLog.Instance.Warn($"查询 {package.Id} 最新版本失败", ex);
            return new LatestToolInfo(null, null, true);
        }
    }

    /// <summary>
    ///     现场找下载页：打开官网首页，抓里面的下载链接（GitHub 仓库直接用 tags 页）。
    ///     官网也打不开时退回官网首页本身。
    /// </summary>
    public async Task<Uri> ResolveDownloadPageAsync(ExternalToolPackage package, string? homepage) {
        var home = Uri.TryCreate(homepage, UriKind.Absolute, out var parsed) ? parsed : new Uri(package.FallbackHomepage);
        if (home.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
            return ExternalToolCatalog.FindDownloadPage(home, null) ?? home;
        }

        try {
            var html = await _http.GetStringAsync(home).ConfigureAwait(false);
            return ExternalToolCatalog.FindDownloadPage(home, html) ?? home;
        } catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException) {
            return home;
        }
    }

    /// <summary>
    ///     用 Homebrew 安装或升级（已由 Homebrew 装过的走 upgrade，否则 install）。输出逐行交给 <paramref name="log" />。
    /// </summary>
    public async Task<ToolInstallResult> InstallAsync(ExternalToolPackage package, IProgress<string> log) {
        var brew = FindBrew();
        if (!CanAutoInstall || brew is null) {
            return new ToolInstallResult(false, false, "brew");
        }

        var (listed, _) = await RunCaptureAsync(brew, ["list", "--versions", package.Id], TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var verb = string.IsNullOrWhiteSpace(listed) ? "install" : "upgrade";
        log.Report($"$ brew {verb} {package.Id}" + (Proxy is null ? string.Empty : $"   (proxy {Proxy})"));

        var info = CreateStartInfo(brew, [verb, package.Id]);
        info.Environment["HOMEBREW_NO_ENV_HINTS"] = "1";
        info.Environment["HOMEBREW_NO_COLOR"] = "1";
        SystemProxy.ApplyTo(info, Proxy);

        var networkFailure = false;
        string? lastError = null;
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        void OnLine(string? line, bool isError) {
            if (string.IsNullOrWhiteSpace(line)) return;
            log.Report(line);
            if (ExternalToolCatalog.LooksLikeNetworkFailure(line)) networkFailure = true;
            if (isError || line.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) lastError = line;
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data, false);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data, true);

        try {
            process.Start();
        } catch (Exception ex) {
            return new ToolInstallResult(false, false, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(InstallTimeout);
        try {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            TryKill(process);
            return new ToolInstallResult(false, true, "timeout");
        }

        process.WaitForExit(); // 等异步输出读完
        return process.ExitCode == 0
            ? new ToolInstallResult(true, false, null)
            : new ToolInstallResult(false, networkFailure, lastError);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private static ProcessStartInfo CreateStartInfo(string exe, IEnumerable<string> args) {
        var info = new ProcessStartInfo(exe) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        // 从访达启动的 app 的 PATH 只有系统目录：把 brew 所在目录补到最前面，brew 自己的脚本要用
        var brewDir = Path.GetDirectoryName(exe);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/sbin:/sbin";
        if (!string.IsNullOrEmpty(brewDir)) info.Environment["PATH"] = brewDir + Path.PathSeparator + path;
        return info;
    }

    private static async Task<(string Output, int ExitCode)> RunCaptureAsync(string exe, IEnumerable<string> args, TimeSpan timeout) {
        using var process = new Process { StartInfo = CreateStartInfo(exe, args) };
        try {
            process.Start();
        } catch (Exception) {
            return (string.Empty, -1);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            TryKill(process);
            return (string.Empty, -1);
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        // oggenc --version 写 stdout，ffmpeg -version 也是 stdout；以防万一 stdout 为空时用 stderr
        return (string.IsNullOrWhiteSpace(output) ? error : output, process.ExitCode);
    }

    private static void TryKill(Process process) {
        try {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        } catch (InvalidOperationException) {
            // 已退出
        }
    }
}
