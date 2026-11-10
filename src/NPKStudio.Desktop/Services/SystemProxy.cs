using System.Diagnostics;
using System.Globalization;

namespace NPKStudio.Desktop.Services;

/// <summary>
///     找出当前要用的 HTTP(S) 代理，给联网检查和 Homebrew 安装共用。
///     <para>
///         顺序：环境变量 <c>HTTPS_PROXY</c> / <c>HTTP_PROXY</c>（大小写都认）&gt; macOS 系统代理（<c>scutil --proxy</c>）。
///         从访达 / 程序坞启动的 app 拿不到终端里 export 的变量，所以 macOS 上要读系统代理（代理软件"设为系统代理"就写在这里）。
///     </para>
/// </summary>
internal static class SystemProxy {
    private static readonly string[] EnvironmentKeys = ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"];

    /// <summary>当前代理；没有配置时为 <c>null</c>。</summary>
    public static Uri? Detect() {
        foreach (var key in EnvironmentKeys) {
            var value = Environment.GetEnvironmentVariable(key);
            if (TryParseProxy(value, out var fromEnvironment)) return fromEnvironment;
        }

        if (!OperatingSystem.IsMacOS()) return null;

        try {
            var info = new ProcessStartInfo("/usr/sbin/scutil") {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("--proxy");
            using var process = Process.Start(info);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return ParseScutil(output);
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>
    ///     解析 <c>scutil --proxy</c> 的输出：优先 HTTPS 代理，其次 HTTP 代理；只认已启用（<c>Enable : 1</c>）的。
    /// </summary>
    internal static Uri? ParseScutil(string output) {
        if (string.IsNullOrEmpty(output)) return null;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n')) {
            var line = raw.Trim();
            var colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon <= 0) continue;
            values[line[..colon].Trim()] = line[(colon + 3)..].Trim();
        }

        return FromKeys(values, "HTTPS") ?? FromKeys(values, "HTTP");
    }

    /// <summary>把代理地址写进子进程环境（Homebrew 用 curl 下载，认这几个变量）。</summary>
    internal static void ApplyTo(ProcessStartInfo info, Uri? proxy) {
        if (proxy is null) return;

        var value = proxy.ToString().TrimEnd('/');
        foreach (var key in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" }) {
            info.Environment[key] = value;
        }
    }

    private static Uri? FromKeys(Dictionary<string, string> values, string prefix) {
        if (!values.TryGetValue(prefix + "Enable", out var enabled) || enabled != "1") return null;
        if (!values.TryGetValue(prefix + "Proxy", out var host) || string.IsNullOrWhiteSpace(host)) return null;
        if (!values.TryGetValue(prefix + "Port", out var portText)
            || !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is <= 0 or > 65535) {
            return null;
        }

        return Uri.TryCreate($"http://{host}:{port.ToString(CultureInfo.InvariantCulture)}", UriKind.Absolute, out var uri) ? uri : null;
    }

    private static bool TryParseProxy(string? value, out Uri? proxy) {
        proxy = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

        // HttpClient 的 WebProxy 只认 http(s)：socks5 交给子进程环境变量即可，这里转成同端口的 http
        // （常见代理软件的混合端口同时接 http 与 socks5）
        proxy = uri.Scheme is "http" or "https" ? uri : new Uri($"http://{uri.Host}:{uri.Port}");
        return true;
    }
}
