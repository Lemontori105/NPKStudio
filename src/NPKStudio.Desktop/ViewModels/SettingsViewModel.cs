using System.Collections.ObjectModel;
using System.Text;
using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.Services;

namespace NPKStudio.Desktop.ViewModels;

/// <summary>一个依赖条目的按钮动作。</summary>
internal enum ToolAction {
    /// <summary>已是最新 / 还没检测：按钮灰掉。</summary>
    None,

    /// <summary>没装：用 Homebrew 安装。</summary>
    Install,

    /// <summary>有新版本：用 Homebrew 升级。</summary>
    Upgrade,

    /// <summary>不能自动安装（没有 Homebrew / 非 macOS）：打开下载页。</summary>
    OpenPage
}

/// <summary>设置窗口里一行依赖（一个工具包）。</summary>
public sealed class ToolItemViewModel : ViewModelBase {
    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    private string _installedText = string.Empty;
    private string _latestText = "—";
    private string _statusText = string.Empty;
    private string _actionText = string.Empty;
    private bool _canAct;
    private bool _isBusy;
    private bool _showLink;
    private Uri? _linkUri;

    internal ToolItemViewModel(ExternalToolPackage package) {
        Package = package;
        _installedText = Loc["settings.tool.notChecked"];
        _actionText = Loc["settings.tool.install"];
    }

    internal ExternalToolPackage Package { get; }
    internal InstalledToolInfo? Installed { get; set; }
    internal LatestToolInfo? Latest { get; set; }
    internal ToolAction Action { get; set; }

    /// <summary>包名。</summary>
    public string Name => Package.DisplayName;

    /// <summary>用途 + 提供的命令。</summary>
    public string Purpose => Loc.F("settings.tool.purposeLine", Loc[Package.PurposeKey], string.Join(" / ", Package.Commands));

    /// <summary>已安装版本 / 未安装。</summary>
    public string InstalledText { get => _installedText; set => SetProperty(ref _installedText, value); }

    /// <summary>最新版本。</summary>
    public string LatestText { get => _latestText; set => SetProperty(ref _latestText, value); }

    /// <summary>状态说明（失败原因等）。</summary>
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    /// <summary>按钮文字。</summary>
    public string ActionText { get => _actionText; set => SetProperty(ref _actionText, value); }

    /// <summary>按钮能不能点（已是最新时灰掉）。</summary>
    public bool CanAct { get => _canAct; set => SetProperty(ref _canAct, value); }

    /// <summary>正在检测 / 安装。</summary>
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    /// <summary>是否显示"下载页面"文字链（联网超时时出现）。</summary>
    public bool ShowLink { get => _showLink; set => SetProperty(ref _showLink, value); }

    /// <summary>文字链的目标：现场从官网抓到的下载页。</summary>
    public Uri? LinkUri { get => _linkUri; set => SetProperty(ref _linkUri, value); }

    /// <summary>文字链的文字（不显示网址本身）。</summary>
    public string LinkText => Loc["settings.tool.downloadPage"];

    /// <summary>语言切换后重发所有文案；还没检测过的行把"未检测 / 安装"换成新语言（检测过的由设置窗口按结果重算）。</summary>
    internal void RefreshTexts() {
        if (Installed is null && !IsBusy) {
            InstalledText = Loc["settings.tool.notChecked"];
            ActionText = Loc["settings.tool.install"];
        }

        OnPropertyChanged(nameof(Purpose));
        OnPropertyChanged(nameof(LinkText));
    }
}

/// <summary>
///     设置窗口：界面语言 + 外部工具（音效功能依赖）的检测与安装。
///     <para>
///         "检测环境"一次查全部依赖；每个依赖各有一个按钮：已是最新时灰掉，没装或有新版本时显示最新版本号并可点。
///         查最新版本或安装时网络超时，就在那一行后面显示"下载页面"文字链，地址是现场从官网抓的下载页。
///     </para>
/// </summary>
/// <summary>设置窗口语言下拉里的一项。</summary>
public sealed class LanguageOption : ViewModelBase {
    private string _label;

    internal LanguageOption(string? code, string label) {
        Code = code;
        _label = label;
    }

    /// <summary>语言代码；<c>null</c> = 跟随系统。</summary>
    public string? Code { get; }

    /// <summary>显示的文字。</summary>
    public string Label { get => _label; internal set => SetProperty(ref _label, value); }
}

public sealed class SettingsViewModel : ViewModelBase, IDisposable {
    private const int MaxLogLength = 20000;

    private static TranslationCatalog Loc => TranslationCatalog.Instance;

    private readonly Action<string?> _applyLanguage;
    private readonly ExternalToolService _tools = new();
    private readonly StringBuilder _log = new();
    private int _languageIndex;
    private bool _isChecking;
    private string _environmentText = string.Empty;
    private bool _showHomebrewLink;

    /// <param name="savedLanguage">设置里保存的语言（<c>null</c> = 跟随系统）。</param>
    /// <param name="applyLanguage">切换语言的回调（保存设置 + 切换词表）；参数 <c>null</c> = 跟随系统。</param>
    internal SettingsViewModel(string? savedLanguage, Action<string?> applyLanguage) {
        _applyLanguage = applyLanguage;
        _languageIndex = savedLanguage switch {
            UiLanguage.Chinese => 1,
            UiLanguage.English => 2,
            _ => 0
        };
        LanguageOptions = [
            new LanguageOption(null, Loc["settings.language.system"]),
            new LanguageOption(UiLanguage.Chinese, "简体中文"),
            new LanguageOption(UiLanguage.English, "English")
        ];
        Items = new ObservableCollection<ToolItemViewModel>(ExternalToolCatalog.Packages.Select(p => new ToolItemViewModel(p)));
        UpdateEnvironmentText();
    }

    /// <summary>
    ///     语言选项：跟随系统 / 简体中文 / English。
    ///     <para>
    ///         列表本身始终是同一份，切换语言时只改"跟随系统"那一项的文字：
    ///         整份换掉会让下拉框重置选中项，再把它写回 <see cref="LanguageIndex" />。
    ///     </para>
    /// </summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    /// <summary>当前选中的语言选项。</summary>
    public int LanguageIndex {
        get => _languageIndex;
        set {
            if (value is < 0 or > 2 || !SetProperty(ref _languageIndex, value)) return;

            // 词表切换后 LanguageRefresh 会回调 OnLanguageChanged → RefreshTexts
            _applyLanguage(value switch {
                1 => UiLanguage.Chinese,
                2 => UiLanguage.English,
                _ => null
            });
        }
    }

    /// <summary>依赖列表。</summary>
    public ObservableCollection<ToolItemViewModel> Items { get; }

    /// <summary>正在检测。</summary>
    public bool IsChecking { get => _isChecking; private set => SetProperty(ref _isChecking, value); }

    /// <summary>安装方式与代理的说明行。</summary>
    public string EnvironmentText { get => _environmentText; private set => SetProperty(ref _environmentText, value); }

    /// <summary>需要安装 Homebrew 时显示 brew.sh 的文字链。</summary>
    public bool ShowHomebrewLink { get => _showHomebrewLink; private set => SetProperty(ref _showHomebrewLink, value); }

    /// <summary>安装输出。</summary>
    public string LogText => _log.ToString();

    /// <summary>一次检测全部依赖：本机版本 + Homebrew 上的最新版本。</summary>
    public async Task CheckAllAsync() {
        if (IsChecking) return;

        IsChecking = true;
        UpdateEnvironmentText();
        try {
            await Task.WhenAll(Items.Select(CheckOneAsync));
        } finally {
            IsChecking = false;
        }
    }

    /// <summary>点某一行的按钮：安装 / 升级；不能自动安装时返回要打开的下载页。</summary>
    /// <returns>需要用浏览器打开的地址（没有则 <c>null</c>）。</returns>
    public async Task<Uri?> ActAsync(ToolItemViewModel item) {
        switch (item.Action) {
            case ToolAction.OpenPage:
                return item.LinkUri ?? await _tools.ResolveDownloadPageAsync(item.Package, item.Latest?.Homepage);
            case ToolAction.Install:
            case ToolAction.Upgrade:
                await InstallAsync(item);
                return null;
            default:
                return null;
        }
    }

    /// <inheritdoc />
    protected override void OnLanguageChanged() => RefreshTexts();

    /// <summary>语言切换后重算所有文案。</summary>
    internal void RefreshTexts() {
        LanguageOptions[0].Label = Loc["settings.language.system"];
        UpdateEnvironmentText();
        foreach (var item in Items) {
            item.RefreshTexts();
            if (item.Installed is not null) Apply(item);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _tools.Dispose();

    private async Task CheckOneAsync(ToolItemViewModel item) {
        item.IsBusy = true;
        item.StatusText = Loc["settings.tool.checking"];
        item.ShowLink = false;
        try {
            var installedTask = ExternalToolService.DetectInstalledAsync(item.Package);
            var latestTask = _tools.FetchLatestAsync(item.Package);
            item.Installed = await installedTask;
            item.Latest = await latestTask;
            item.StatusText = string.Empty;

            if (item.Latest.TimedOut) {
                item.StatusText = Loc["settings.tool.checkTimeout"];
                await ShowDownloadLinkAsync(item);
            } else if (item.Action is ToolAction.OpenPage || NeedsPage(item)) {
                item.LinkUri = await _tools.ResolveDownloadPageAsync(item.Package, item.Latest.Homepage);
            }

            Apply(item);
        } finally {
            item.IsBusy = false;
        }
    }

    private async Task InstallAsync(ToolItemViewModel item) {
        item.IsBusy = true;
        item.CanAct = false;
        item.ShowLink = false;
        item.StatusText = Loc["settings.tool.installing"];
        var progress = new Progress<string>(AppendLog);
        try {
            var result = await _tools.InstallAsync(item.Package, progress);
            if (result.Success) {
                item.StatusText = Loc["settings.tool.installed"];
                item.Installed = await ExternalToolService.DetectInstalledAsync(item.Package);
                Apply(item);
                item.StatusText = Loc["settings.tool.installed"];
                return;
            }

            item.StatusText = result.TimedOut
                ? Loc["settings.tool.installTimeout"]
                : Loc.F("settings.tool.installFailed", result.Message ?? "?");
            if (result.TimedOut) await ShowDownloadLinkAsync(item);
            Apply(item);
        } finally {
            item.IsBusy = false;
        }
    }

    private async Task ShowDownloadLinkAsync(ToolItemViewModel item) {
        item.LinkUri = await _tools.ResolveDownloadPageAsync(item.Package, item.Latest?.Homepage);
        item.ShowLink = true;
    }

    /// <summary>按检测结果决定按钮：已是最新 → 灰掉；没装 / 有新版 → 显示版本号并可点。</summary>
    private void Apply(ToolItemViewModel item) {
        var installed = item.Installed;
        var latest = item.Latest?.Version;
        var canInstall = ExternalToolService.CanAutoInstall && ExternalToolService.FindBrew() is not null;
        item.LatestText = latest ?? "—";

        if (installed is null) return;

        if (!installed.IsInstalled) {
            item.InstalledText = installed.MissingCommands.Count < item.Package.Commands.Count
                ? Loc.F("settings.tool.partial", string.Join(", ", installed.MissingCommands))
                : Loc["settings.tool.notInstalled"];
            SetAction(item, canInstall ? ToolAction.Install : ToolAction.OpenPage, latest);
            return;
        }

        item.InstalledText = installed.Version ?? Loc["settings.tool.versionUnknown"];
        if (latest is not null && installed.Version is not null
            && ExternalToolCatalog.CompareVersions(latest, installed.Version) > 0) {
            SetAction(item, canInstall ? ToolAction.Upgrade : ToolAction.OpenPage, latest);
            return;
        }

        SetAction(item, ToolAction.None, latest);
    }

    private static void SetAction(ToolItemViewModel item, ToolAction action, string? latest) {
        item.Action = action;
        item.CanAct = action != ToolAction.None;
        item.ActionText = action switch {
            ToolAction.Install => latest is null ? Loc["settings.tool.install"] : Loc.F("settings.tool.installVersion", latest),
            ToolAction.Upgrade => Loc.F("settings.tool.upgradeVersion", latest),
            ToolAction.OpenPage => Loc["settings.tool.openPage"],
            _ => item.Latest?.Version is null ? Loc["settings.tool.unknownLatest"] : Loc["settings.tool.upToDate"]
        };
    }

    private static bool NeedsPage(ToolItemViewModel item) =>
        !(ExternalToolService.CanAutoInstall && ExternalToolService.FindBrew() is not null)
        && (item.Installed is { IsInstalled: false } || item.Latest?.Version is not null);

    private void UpdateEnvironmentText() {
        var brew = ExternalToolService.FindBrew();
        var proxy = _tools.Proxy is null ? Loc["settings.env.noProxy"] : Loc.F("settings.env.proxy", _tools.Proxy.Authority);
        if (!ExternalToolService.CanAutoInstall) {
            EnvironmentText = Loc.F("settings.env.manual", proxy);
            ShowHomebrewLink = false;
        } else if (brew is null) {
            EnvironmentText = Loc.F("settings.env.noBrew", proxy);
            ShowHomebrewLink = true;
        } else {
            EnvironmentText = Loc.F("settings.env.brew", brew, proxy);
            ShowHomebrewLink = false;
        }
    }

    private void AppendLog(string line) {
        _log.AppendLine(line);
        if (_log.Length > MaxLogLength) _log.Remove(0, _log.Length - MaxLogLength);
        OnPropertyChanged(nameof(LogText));
    }
}
