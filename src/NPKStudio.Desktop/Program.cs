using Avalonia;

namespace NPKStudio.Desktop;

internal static class Program {
    [STAThread]
    public static void Main(string[] args) {
        try {
            // 无界面模式：不启动 Avalonia
            if (PackCommandLine.TryScan(args)) return;
            if (PackCommandLine.TryRun(args)) return;

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        } catch (Exception ex) {
            CrashReporter.Write(ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            // macOS 应用菜单的"服务 / 隐藏 / 退出"等标准项由 Platform.MacAppMenu 自己建（跟随界面语言），
            // 不要 Avalonia 再追加一套写死英文的
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
            .LogToTrace();
    }
}
