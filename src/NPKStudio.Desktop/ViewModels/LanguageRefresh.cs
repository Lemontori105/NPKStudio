using NPKStudio.Desktop.Localization;

namespace NPKStudio.Desktop.ViewModels;

/// <summary>
///     运行时切换语言时刷新所有还活着的 ViewModel。
///     <para>
///         XAML 里 <c>{loc:Tr key}</c> 的绑定自己会跟着 <see cref="TranslationCatalog" /> 刷新；
///         但 ViewModel 里由代码拼出来的文案（状态栏、计数、下拉选项、帧信息……）只有在属性变更通知时才重新取。
///         这里在词表切换后挨个调 <see cref="ViewModelBase.RefreshForLanguage" />。
///     </para>
///     <para>
///         只持有弱引用：条目 / 帧的 ViewModel 可能成千上万个，用完就该被回收；列表里的死引用定期清掉。
///         顺序按创建先后：主窗口的 ViewModel 先于它创建的通道，先清缓存再通知，子级取到的已经是新文案。
///     </para>
/// </summary>
internal static class LanguageRefresh {
    private static readonly List<WeakReference<ViewModelBase>> Tracked = [];
    private static readonly object Gate = new();
    private static int s_pruneAt = 256;
    private static bool s_subscribed;

    /// <summary>登记一个 ViewModel（<see cref="ViewModelBase" /> 构造时调用，可能来自后台线程）。</summary>
    public static void Track(ViewModelBase viewModel) {
        lock (Gate) {
            if (!s_subscribed) {
                s_subscribed = true;
                TranslationCatalog.Instance.PropertyChanged += (_, _) => RefreshAll();
            }

            Tracked.Add(new WeakReference<ViewModelBase>(viewModel));
            if (Tracked.Count >= s_pruneAt) {
                Tracked.RemoveAll(reference => !reference.TryGetTarget(out _));
                s_pruneAt = Math.Max(256, Tracked.Count * 2);
            }
        }
    }

    /// <summary>刷新所有还活着的 ViewModel（在 UI 线程上调用：语言切换本身就在 UI 线程）。</summary>
    public static void RefreshAll() {
        List<ViewModelBase> alive;
        lock (Gate) {
            alive = new List<ViewModelBase>(Tracked.Count);
            foreach (var reference in Tracked) {
                if (reference.TryGetTarget(out var viewModel)) alive.Add(viewModel);
            }

            Tracked.Clear();
            Tracked.AddRange(alive.Select(viewModel => new WeakReference<ViewModelBase>(viewModel)));
        }

        // 锁外刷新：刷新过程中可能新建 ViewModel（会再进来登记）
        foreach (var viewModel in alive) {
            viewModel.RefreshForLanguage();
        }
    }
}
