using Avalonia.Headless.XUnit;

using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.Services;
using NPKStudio.Desktop.ViewModels;

using Xunit;

namespace NPKStudio.Desktop.Tests;

/// <summary>
///     设置窗口背后的纯逻辑：代理解析、版本解析 / 比较、下载页抓取、Homebrew API 解析、语言选择、设置存取。
///     <para>联网与真正的安装依赖外部环境，不在这里测。</para>
/// </summary>
public sealed class SettingsAndToolsTests
{
    // ── 代理 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseScutil_PrefersEnabledHttpsProxy() {
        const string output = """
            <dictionary> {
              HTTPEnable : 1
              HTTPPort : 8080
              HTTPProxy : 10.0.0.1
              HTTPSEnable : 1
              HTTPSPort : 7890
              HTTPSProxy : 127.0.0.1
              SOCKSEnable : 1
            }
            """;
        Assert.Equal(new Uri("http://127.0.0.1:7890"), SystemProxy.ParseScutil(output));
    }

    [Fact]
    public void ParseScutil_FallsBackToHttp_AndIgnoresDisabledOrBroken() {
        Assert.Equal(new Uri("http://10.0.0.1:8080"), SystemProxy.ParseScutil("HTTPEnable : 1\nHTTPPort : 8080\nHTTPProxy : 10.0.0.1\nHTTPSEnable : 0\n"));
        Assert.Null(SystemProxy.ParseScutil("HTTPSEnable : 0\nHTTPSPort : 7890\nHTTPSProxy : 127.0.0.1\n"));
        Assert.Null(SystemProxy.ParseScutil("HTTPSEnable : 1\nHTTPSPort : notaport\nHTTPSProxy : 127.0.0.1\n"));
        Assert.Null(SystemProxy.ParseScutil(string.Empty));
    }

    // ── 版本 ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ffmpeg version 9.0.2 Copyright (c) 2000-2026 the FFmpeg developers\nbuilt with clang", "9.0.2")]
    [InlineData("ffplay version 9.0.2 Copyright (c) 2003-2026 the FFmpeg developers", "9.0.2")]
    [InlineData("oggenc from vorbis-tools 1.4.3\n", "1.4.3")]
    [InlineData("ffmpeg version N-123456-gabcdef Copyright (c) 2000-2026", null)]
    [InlineData("", null)]
    public void ParseVersion_ReadsFirstDottedNumber(string output, string? expected) =>
        Assert.Equal(expected, ExternalToolCatalog.ParseVersion(output));

    [Theory]
    [InlineData("9.0.2", "9.0.2", 0)]
    [InlineData("9.0.10", "9.0.2", 1)]
    [InlineData("9.0", "9.0.1", -1)]
    [InlineData("10.0", "9.9.9", 1)]
    [InlineData("1.4.3", "1.4.3.0", 0)]
    public void CompareVersions_ComparesNumerically(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(ExternalToolCatalog.CompareVersions(a, b)));

    // ── 下载页 / Homebrew API ─────────────────────────────────────────────────

    [Fact]
    public void FindDownloadPage_TakesFirstDownloadLink_AsAbsoluteUrl() {
        const string html = """
            <a href="#main">skip</a>
            <a href="about.html">About</a>
            <a class="btn" href="download.html#releases">Download</a>
            <a href="https://elsewhere.example/download">Other</a>
            """;
        Assert.Equal(new Uri("https://ffmpeg.org/download.html#releases"),
            ExternalToolCatalog.FindDownloadPage(new Uri("https://ffmpeg.org/"), html));
    }

    [Fact]
    public void FindDownloadPage_GitHubUsesTags_AndMissingLinkIsNull() {
        Assert.Equal(new Uri("https://github.com/xiph/vorbis-tools/tags"),
            ExternalToolCatalog.FindDownloadPage(new Uri("https://github.com/xiph/vorbis-tools"), null));
        Assert.Null(ExternalToolCatalog.FindDownloadPage(new Uri("https://example.org/"), "<a href=\"/about\">About</a>"));
        Assert.Null(ExternalToolCatalog.FindDownloadPage(new Uri("https://example.org/"), null));
    }

    [Fact]
    public void ParseFormulaJson_ReadsStableVersionAndHomepage() {
        const string json = """{"name":"ffmpeg","homepage":"https://ffmpeg.org/","versions":{"stable":"9.0.2","head":"HEAD"}}""";
        var (version, homepage) = ExternalToolCatalog.ParseFormulaJson(json);
        Assert.Equal("9.0.2", version);
        Assert.Equal("https://ffmpeg.org/", homepage);

        var (badVersion, badHomepage) = ExternalToolCatalog.ParseFormulaJson("not json");
        Assert.Null(badVersion);
        Assert.Null(badHomepage);
    }

    [Theory]
    [InlineData("curl: (28) Operation timed out after 30001 milliseconds", true)]
    [InlineData("curl: (7) Failed to connect to ghcr.io port 443", true)]
    [InlineData("Error: Could not resolve host: formulae.brew.sh", true)]
    [InlineData("==> Pouring ffmpeg--9.0.2.arm64_sequoia.bottle.tar.gz", false)]
    public void LooksLikeNetworkFailure_MatchesCurlAndBrewErrors(string line, bool expected) =>
        Assert.Equal(expected, ExternalToolCatalog.LooksLikeNetworkFailure(line));

    [Fact]
    public void Packages_CoverFfmpegAndVorbisTools() {
        Assert.Contains(ExternalToolCatalog.Packages, p => p.Id == "ffmpeg" && p.Commands.Contains("ffplay"));
        Assert.Contains(ExternalToolCatalog.Packages, p => p.Id == "vorbis-tools" && p.Commands.Contains("oggenc"));
        Assert.All(ExternalToolCatalog.Packages, p => Assert.NotEqual(p.PurposeKey, TranslationCatalog.Instance[p.PurposeKey]));
    }

    // ── 语言 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_CommandLineThenSavedThenSystem() {
        Assert.Equal("en-US", UiLanguage.Resolve("en-US", "zh-CN", "zh-Hans-CN"));
        Assert.Equal("en-US", UiLanguage.Resolve(null, "en-US", "zh-Hans-CN"));
        Assert.Equal("zh-CN", UiLanguage.Resolve(null, null, "zh-Hans-CN"));
        Assert.Equal("en-US", UiLanguage.Resolve(null, null, "ja-JP"));
        Assert.Equal("en-US", UiLanguage.Resolve(null, null, null));
    }

    [Fact]
    public void ParseAppleLanguages_TakesFirstEntry() {
        Assert.Equal("zh-Hans-CN", UiLanguage.ParseAppleLanguages("(\n    \"zh-Hans-CN\",\n    \"en-CN\"\n)\n"));
        Assert.Equal("en", UiLanguage.ParseAppleLanguages("(\n    en,\n    ja\n)\n"));
        Assert.Null(UiLanguage.ParseAppleLanguages(string.Empty));
    }

    /// <summary>
    ///     设置里存的语言能读回来；设成 null（跟随系统）就从文件里去掉。只写测试专用的配置目录。
    ///     文件里同时有数字（播放倍率）和字符串时也要能读：曾经 TryGetInt32 遇到字符串会抛异常，字符串设置全丢。
    /// </summary>
    [AvaloniaFact]
    public void SettingsStore_PersistsLanguage() {
        Assert.Equal(TestEnvironment.ConfigDirectory, Environment.GetEnvironmentVariable("NPKSTUDIO_CONFIG_DIR"));

        var store = AppSettingsStore.Load();
        store.RateIndex = store.RateIndex; // 保证文件里有一个数字键
        store.Language = "en-US";
        store.Save();
        Assert.Equal("en-US", AppSettingsStore.Load().Language);

        store.Language = null;
        store.Save();
        Assert.Null(AppSettingsStore.Load().Language);
    }

    /// <summary>
    ///     多份设置实例互不覆盖：主窗口和每个通道各自 Load 一份。以前 Save 整份写回，
    ///     通道手里的旧副本在关窗口时一存，就把刚在设置窗口切换的语言改回去（"卡在英文"）。
    /// </summary>
    [AvaloniaFact]
    public void SettingsStore_StaleCopyDoesNotOverwriteOtherKeys() {
        var stale = AppSettingsStore.Load(); // 通道那份：在切语言之前就读好了
        var main = AppSettingsStore.Load();
        try {
            main.Language = "en-US";
            main.Save();

            stale.Save(); // 什么都没改也存一次（关窗口时的 Flush 就是这样）
            Assert.Equal("en-US", AppSettingsStore.Load().Language);

            stale.ManifestPath = "/tmp/npkstudio-test-manifest.txt"; // 旧副本只改了别的键
            stale.Save();
            var reloaded = AppSettingsStore.Load();
            Assert.Equal("en-US", reloaded.Language);
            Assert.Equal("/tmp/npkstudio-test-manifest.txt", reloaded.ManifestPath);
            Assert.Equal("en-US", stale.Language); // 存完以后旧副本也同步到了文件里的最新值

            main.Language = null; // 跟随系统 = 从文件里删掉；不能被旧副本再写回来
            main.Save();
            stale.Save();
            Assert.Null(AppSettingsStore.Load().Language);
        } finally {
            main.Language = null;
            main.ManifestPath = null;
            main.Save();
        }
    }

    /// <summary>
    ///     语言下拉的选项列表始终是同一份（只改"跟随系统"的文字）：整份换掉会让下拉框重置选中项，
    ///     切到英文以后就切不回来。
    /// </summary>
    [AvaloniaFact]
    public void SettingsViewModel_LanguageOptionsStayTheSameList() {
        using var vm = new SettingsViewModel(null, _ => { });
        var options = vm.LanguageOptions;
        Assert.Equal(new string?[] { null, "zh-CN", "en-US" }, options.Select(option => option.Code));

        vm.LanguageIndex = 2;
        vm.RefreshTexts();
        vm.LanguageIndex = 1;

        Assert.Same(options, vm.LanguageOptions);
        Assert.Equal(1, vm.LanguageIndex);
    }

    /// <summary>设置窗口的语言下拉：0 跟随系统（null）、1 中文、2 英文；初始值来自已保存的语言。</summary>
    [AvaloniaFact]
    public void SettingsViewModel_LanguageIndexMapsToCodes() {
        var applied = new List<string?>();
        using var vm = new SettingsViewModel("en-US", applied.Add);
        Assert.Equal(2, vm.LanguageIndex);
        Assert.Equal(3, vm.LanguageOptions.Count);

        vm.LanguageIndex = 1;
        vm.LanguageIndex = 0;
        vm.LanguageIndex = 5;

        Assert.Equal(new List<string?> { "zh-CN", null }, applied);
        Assert.Equal(0, vm.LanguageIndex);
        Assert.Equal(ExternalToolCatalog.Packages.Count, vm.Items.Count);
        Assert.All(vm.Items, item => Assert.False(item.CanAct));
    }
}
