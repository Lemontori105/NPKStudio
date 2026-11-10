using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NPKStudio.Desktop.Localization;
using NPKStudio.Desktop.ViewModels;
using NPKStudio.Desktop.Views;

using Xunit;

namespace NPKStudio.Desktop.Tests;

/// <summary>切换全局界面语言的测试单独成组、不与其它测试并行（别的测试正读着词表时不能把语言换掉）。</summary>
[CollectionDefinition(nameof(LanguageSwitchCollection), DisableParallelization = true)]
public sealed class LanguageSwitchCollection { }

/// <summary>
///     运行时切换语言（设置窗口里改语言，不重启）：切到英文后，窗口里看得见的文字不能再有中文。
///     <para>
///         XAML 的 <c>{loc:Tr}</c> 绑定本来就会刷新；这里守的是代码拼出来的文案——状态栏、缩放 / 坐标读数、
///         下拉选项（效果名、倍率）、帧信息、设置窗口里的依赖行等。失败信息会列出所有残留的中文和它所在的控件。
///     </para>
/// </summary>
[Collection(nameof(LanguageSwitchCollection))]
public sealed class LanguageSwitchUiTests
{
    [AvaloniaFact]
    public async Task SwitchingToEnglish_LeavesNoChineseText()
    {
        using var folder = TestFolder.Create();
        folder.WriteImg("walk.img", frameCount: 3, width: 8, height: 8);

        var catalog = TranslationCatalog.Instance;
        var original = catalog.CurrentLanguage;
        catalog.SetLanguage(UiLanguage.Chinese);

        var window = new MainWindow();
        window.Show();
        var viewModel = (MainWindowViewModel)window.DataContext!;
        await viewModel.OpenFolderAsync(folder.Path);

        using var settingsViewModel = new SettingsViewModel(null, _ => { });
        var settings = new SettingsWindow(settingsViewModel);
        settings.Show();
        Dispatcher.UIThread.RunJobs();

        try {
            // 先确认中文界面里确实有中文，免得"没找到中文"只是因为没取到文字
            Assert.NotEmpty(ChineseTexts(window));

            catalog.SetLanguage(UiLanguage.English);
            Dispatcher.UIThread.RunJobs();

            var leftovers = ChineseTexts(window).Concat(ChineseTexts(settings)).ToList();
            Assert.True(leftovers.Count == 0,
                "切到英文后仍是中文的文字（控件: 文字）：\n" + string.Join("\n", leftovers));
        } finally {
            catalog.SetLanguage(original);
            Dispatcher.UIThread.RunJobs();
            settings.Close();
            window.Close();
        }
    }

    /// <summary>窗口里所有看得见的文字（标题、TextBlock、文本框提示、工具提示）中含中文的那些。</summary>
    private static List<string> ChineseTexts(Window window)
    {
        var found = new List<string>();

        void Check(string where, object? value)
        {
            var text = value switch {
                string s => s,
                TextBlock block => TextOf(block),
                _ => null
            };
            // "简体中文"是语言选项本身的名字，任何语言下都这样写
            if (text is not null && text != "简体中文" && text.Any(IsChinese)) {
                found.Add($"{where}: {text}");
            }
        }

        Check($"{window.GetType().Name}.Title", window.Title);
        foreach (var visual in window.GetVisualDescendants()) {
            if (visual is not Control control || !control.IsEffectivelyVisible) continue;

            var where = $"{control.GetType().Name}{(string.IsNullOrEmpty(control.Name) ? "" : "#" + control.Name)}";
            switch (control) {
                case TextBlock block:
                    Check(where, TextOf(block));
                    break;
                case TextBox box:
                    Check(where + ".Watermark", box.Watermark);
                    break;
            }

            Check(where + ".ToolTip", ToolTip.GetTip(control));
        }

        return found.Distinct().ToList();
    }

    private static string? TextOf(TextBlock block) =>
        block.Inlines is { Count: > 0 } inlines
            ? string.Concat(inlines.OfType<Run>().Select(run => run.Text))
            : block.Text;

    private static bool IsChinese(char c) =>
        c is >= '一' and <= '鿿' or >= '　' and <= '〿' or >= '＀' and <= '￯';
}
