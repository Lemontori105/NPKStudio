using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NPKStudio.Desktop.Localization;

namespace NPKStudio.Desktop.Views;

/// <summary>「关于 NPKStudio」：名称、版本、用途、许可与上游仓库链接。Esc 关闭。</summary>
public sealed partial class AboutWindow : Window {
    /// <summary>上游仓库。</summary>
    private static readonly Uri Repository = new("https://github.com/MaiKuraki/NPKStudio");

    public AboutWindow() {
        InitializeComponent();
        VersionText.Text = TranslationCatalog.Instance.F("about.version", AppInfo.Version);
        KeyDown += (_, e) => {
            if (e.Key == Key.Escape) Close();
        };
    }

    private async void OnOpenRepository(object? sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(Repository);
}
