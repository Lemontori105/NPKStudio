using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NPKStudio.Desktop.ViewModels;

/// <summary>
///     最小可用的 MVVM 基类：属性变更通知 + 脏标记。刻意不引入 CommunityToolkit.Mvvm，
///     因为本项目只需要 <c>SetProperty</c> 与命令包装（约 60 行），引入源生成器会增加构建不确定性。
///     <para>
///         <b>运行时切换语言</b>：每个实例创建时登记到 <see cref="LanguageRefresh" />（弱引用）。
///         切换语言后先调 <see cref="OnLanguageChanged" />（子类在这里清文案缓存、重算存下来的文字），
///         再发一次属性名为空的变更通知——Avalonia 的编译绑定把空属性名当成"全部属性都变了"重新取值
///         （反编译查 Avalonia.Markup.Xaml 11.3.22 <c>InpcPropertyAccessor.OnEvent</c>：名字相同或 <c>IsNullOrEmpty</c> 都刷新）。
///     </para>
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged {
    protected ViewModelBase() => LanguageRefresh.Track(this);

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null) {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>界面语言切换后调用：子类清掉本地化缓存、重算存下来的文案。默认什么也不做。</summary>
    protected virtual void OnLanguageChanged() { }

    /// <summary>由 <see cref="LanguageRefresh" /> 调用：先重算，再让所有绑定重新取值。</summary>
    internal void RefreshForLanguage() {
        OnLanguageChanged();
        OnPropertyChanged(string.Empty);
    }
}
