using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NPKStudio.Desktop.ViewModels;

namespace NPKStudio.Desktop.Views;

/// <summary>
///     设置窗口：界面语言 + 外部工具（音效功能依赖）的检测、安装与下载页链接。
///     <para>每行的按钮在列表上统一接住 Click，再按样式类区分"安装 / 升级"与"下载页面"链接。</para>
/// </summary>
public sealed partial class SettingsWindow : Window {
    /// <summary>Homebrew 官网（没装 Homebrew 时给的链接）。</summary>
    private static readonly Uri HomebrewSite = new("https://brew.sh/");

    public SettingsWindow() {
        InitializeComponent();
        ToolList.AddHandler(Button.ClickEvent, OnToolButtonClick);
    }

    internal SettingsWindow(SettingsViewModel viewModel) : this() {
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private async void OnCheckAll(object? sender, RoutedEventArgs e) {
        if (ViewModel is { } vm) await vm.CheckAllAsync();
    }

    private async void OnToolButtonClick(object? sender, RoutedEventArgs e) {
        if (e.Source is not Button button || button.DataContext is not ToolItemViewModel item || ViewModel is not { } vm) return;

        e.Handled = true;
        if (button.Classes.Contains("toolLink")) {
            if (item.LinkUri is { } link) await Launcher.LaunchUriAsync(link);
            return;
        }

        if (button.Classes.Contains("toolAction")) {
            var page = await vm.ActAsync(item);
            if (page is not null) await Launcher.LaunchUriAsync(page);
        }
    }

    private async void OnOpenHomebrew(object? sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(HomebrewSite);

    /// <summary>安装输出更新时滚到最后一行。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(SettingsViewModel.LogText)) {
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        }
    }

    protected override void OnClosed(EventArgs e) {
        if (ViewModel is { } vm) {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.Dispose();
        }

        base.OnClosed(e);
    }
}
