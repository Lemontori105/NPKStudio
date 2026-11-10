using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using NPKStudio.Core.Diagnostics;
using NPKStudio.Desktop.Controls;
using NPKStudio.Desktop.Views;

namespace NPKStudio.Desktop;

public sealed partial class App : Application {
    public override void Initialize() {
        AvaloniaXamlLoader.Load(this);

        // macOS 应用菜单必须在这里设：Avalonia.Native 紧接着（AfterSetup）就读它，
        // 到 OnFrameworkInitializationCompleted 再设已经晚了，菜单里会是默认的 "About Avalonia"。
        // 主窗口这时还没建，点菜单时再去取。
        if (OperatingSystem.IsMacOS()) {
            NativeMenu.SetMenu(this, Platform.MacAppMenu.Build(
                openAbout: () => CurrentMainWindow()?.OpenAbout(),
                openSettings: () => CurrentMainWindow()?.OpenSettings()));
            Platform.MacWindowShortcuts.Install();
        }
    }

    private MainWindow? CurrentMainWindow() =>
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;

    public override void OnFrameworkInitializationCompleted() {
        // 全局兜底：任何没被局部 catch 的异常至少留下痕迹，而不是"窗口突然消失、什么都没有"。
        // UnhandledException 之后进程仍会终止（无法阻止），但日志里能查到原因；
        // UnobservedTaskException 标记 observed，避免后台任务异常在 GC 时把进程带走。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            TraceLog.Instance.Error("未处理异常", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) => {
            TraceLog.Instance.Error("未观察的任务异常", e.Exception);
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            var args = desktop.Args ?? [];

            string? file = null;
            string? exportPng = null;
            string? replacePng = null;
            string? saveNpk = null;
            string? language = null;
            string? background = null;
            string? backdrop = null;
            string? play = null;
            string? playFps = null;
            string? rateIndex = null;
            string? rateMid = null;
            var playNoLoop = false;
            string? effectName = null;

            string? renderShot = null;
            string? frameIndex = null;
            string? dumpState = null;
            string? windowShot = null;
            var collapseLeft = false;
            string? frameProbe = null;
            string? zoom = null;
            string? pointer = null;
            string? guides = null;
            string? theme = null;
            string? originMode = null;
            var noPixelAlign = false;
            string? hideFrames = null;
            string? dragPreview = null;
            string? frameMenuShot = null;
            string? entryIndex = null;
            string? changeMarks = null;
            string? rulers = null;

            for (var i = 0; i < args.Length; i++) {
                switch (args[i]) {
                    case "--export-png" when i + 1 < args.Length:
                        exportPng = args[++i];
                        break;
                    case "--replace-png" when i + 1 < args.Length:
                        replacePng = args[++i];
                        break;
                    case "--save-npk" when i + 1 < args.Length:
                        saveNpk = args[++i];
                        break;
                    case "--lang" when i + 1 < args.Length:
                        language = args[++i];
                        break;
                    case "--bg" when i + 1 < args.Length:
                        background = args[++i];
                        break;
                    // 截图 / 自动化专用：画布背景档位，取值同 CanvasBackground 枚举
                    // （Checker | Dark | Light | Green | Black | CheckerLight | CheckerDark，大小写不敏感）。
                    // 与 --bg 的区别：--backdrop 遇到非法值会报错并以退出码 1 结束，方便脚本断言；
                    // --bg 保持旧行为（未知值静默回退 Checker），避免破坏既有调用。
                    // 值缺失同样按非法处理：截图脚本宁可失败，也不要悄悄拍出一张默认背景的图。
                    case "--backdrop":
                        backdrop = i + 1 < args.Length ? args[++i] : "<missing>";
                        break;
                    case "--play" when i + 1 < args.Length:
                        play = args[++i];
                        break;
                    case "--play-fps" when i + 1 < args.Length:
                        playFps = args[++i];
                        break;
                    case "--rate" when i + 1 < args.Length:
                        rateIndex = args[++i];
                        break;
                    case "--rate-mid" when i + 1 < args.Length:
                        rateMid = args[++i];
                        break;
                    case "--play-no-loop":
                        playNoLoop = true;
                        break;
                    case "--additive":
                        effectName = "lineardodge";
                        break;
                    case "--effect" when i + 1 < args.Length:
                        effectName = args[++i];
                        break;

                    case "--linear-dodge":
                        effectName = "lineardodge";
                        break;
                    case "--render-shot" when i + 1 < args.Length:
                        renderShot = args[++i];
                        break;
                    case "--frame" when i + 1 < args.Length:
                        frameIndex = args[++i];
                        break;
                    case "--dump-state" when i + 1 < args.Length:
                        dumpState = args[++i];
                        break;
                    case "--window-shot" when i + 1 < args.Length:
                        windowShot = args[++i];
                        break;
                    case "--collapse-left":
                        collapseLeft = true;
                        break;
                    case "--frame-probe" when i + 1 < args.Length:
                        frameProbe = args[++i];
                        break;
                    case "--zoom" when i + 1 < args.Length:
                        zoom = args[++i];
                        break;
                    case "--pointer" when i + 1 < args.Length:
                        pointer = args[++i];
                        break;
                    case "--guides" when i + 1 < args.Length:
                        guides = args[++i];
                        break;
                    case "--theme" when i + 1 < args.Length:
                        theme = args[++i];
                        break;
                    // 截图 / 验收用：坐标基准 image（默认）| role
                    case "--origin" when i + 1 < args.Length:
                        originMode = args[++i];
                        break;
                    // 自动化截图 / 对比用：关掉默认开启的像素对齐，观察连续缩放
                    case "--no-pixel-align":
                        noPixelAlign = true;
                        break;
                    // 截图 / 验收用：打开后隐藏若干帧（0 基，逗号分隔）
                    case "--hide-frames" when i + 1 < args.Length:
                        hideFrames = args[++i];
                        break;
                    // 截图用：摆出拖拽中的样子（from:insert），不移动任何帧
                    case "--drag-preview" when i + 1 < args.Length:
                        dragPreview = args[++i];
                        break;
                    // 截图用：展开帧列表右键菜单，并把弹出层单独渲染到这个路径
                    case "--frame-menu-shot" when i + 1 < args.Length:
                        frameMenuShot = args[++i];
                        break;
                    // 截图 / 验收用：切到指定条目（0 基）
                    case "--entry" when i + 1 < args.Length:
                        entryIndex = args[++i];
                        break;
                    // 截图用：制造帧级变更，格式 replace,insertAfter,move（0 基）
                    case "--change-marks" when i + 1 < args.Length:
                        changeMarks = args[++i];
                        break;

                    // 截图 / 验收用：标尺开关（on | off，默认 on）
                    case "--rulers" when i + 1 < args.Length:
                        rulers = args[++i];
                        break;
                    default:
                        if (!args[i].StartsWith('-')) file ??= args[i];
                        break;
                }
            }

            // 背景档位：--backdrop 是严格版，非法值必须立刻失败（截图脚本靠退出码判断，
            // 静默回退会让"以为换了背景、实际还是棋盘格"的截图混进文档）。
            if (backdrop is not null) {
                if (!TryParseBackdrop(backdrop, out var backdropValue)) {
                    var message = $"参数 --backdrop 的取值 \"{backdrop}\" 无效；可用值："
                        + $"{string.Join(" | ", Enum.GetNames<CanvasBackground>())}（大小写不敏感）。";
                    Console.Error.WriteLine(message);
                    TraceLog.Instance.Error(message);
                    Environment.Exit(1);
                    return;
                }

                background = backdropValue.ToString();
            }

            // 语言必须在窗口构造前设定，XAML 绑定才会取到对应词表。
            // 优先级：--lang > 设置窗口里保存的语言 > 跟随系统（中文系统用中文，其余用英文）
            var savedLanguage = Services.AppSettingsStore.Load().Language;
            var savedOrSystem = savedLanguage is null ? Localization.UiLanguage.DetectSystemTag() : null;
            Localization.TranslationCatalog.Instance.SetLanguage(
                Localization.UiLanguage.Resolve(language, savedLanguage, savedOrSystem));

            // 主题也必须在窗口构造前定：截图要固定浅色 / 深色两套，不受系统设置影响
            if (!string.IsNullOrEmpty(theme)) {
                RequestedThemeVariant = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
                    ? ThemeVariant.Dark
                    : ThemeVariant.Light;
            }

            desktop.MainWindow = new MainWindow(file) {
                ExportPngOnOpen = exportPng,
                ReplacePngOnOpen = replacePng,
                SaveNpkOnOpen = saveNpk,
                BackgroundOnOpen = background,
                PlayOnOpen = play,
                PlaybackNoLoop = playNoLoop,
                PlaybackFps = playFps,
                RateOnOpen = rateIndex,
                RateMidOnOpen = rateMid,
                EffectOnOpen = effectName,

                RenderShotOnOpen = renderShot,
                FrameOnOpen = frameIndex,
                DumpStateOnOpen = dumpState,
                WindowShotOnOpen = windowShot,
                CollapseLeftOnOpen = collapseLeft,
                FrameProbeOnOpen = frameProbe,
                ZoomOnOpen = zoom,
                PointerOnOpen = pointer,
                GuidesOnOpen = guides,
                OriginModeOnOpen = originMode,
                NoPixelAlignOnOpen = noPixelAlign,
                HideFramesOnOpen = hideFrames,
                DragPreviewOnOpen = dragPreview,
                FrameMenuShotOnOpen = frameMenuShot,
                EntryOnOpen = entryIndex,
                ChangeMarksOnOpen = changeMarks,
                RulersOnOpen = rulers
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    ///     解析 <c>--backdrop</c>：只接受 <see cref="CanvasBackground" /> 的枚举名，大小写不敏感；
    ///     纯数字（<see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)" /> 会把它当成枚举值）一律视为非法。
    /// </summary>
    private static bool TryParseBackdrop(string value, out CanvasBackground backdrop) {
        backdrop = CanvasBackground.Checker;
        if (int.TryParse(value, out _)
            || !Enum.TryParse(value, ignoreCase: true, out CanvasBackground parsed)
            || !Enum.IsDefined(parsed)) {
            return false;
        }

        backdrop = parsed;
        return true;
    }
}
