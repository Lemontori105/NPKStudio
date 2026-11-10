using System.Collections.ObjectModel;
using System.Diagnostics;

using Avalonia.Threading;

using NPKStudio.Desktop.Services;

namespace NPKStudio.Desktop.ViewModels.Channels;

/// <summary>
///     一个通道自己的播放机：倍率 / 帧率 / 定时器 / 推进。
///     <para>
///         原 <c>PlaybackController</c>（计时）+ <c>PlaybackPresenter</c>（推进）合并成这一份，
///         每个 <see cref="FrameChannel" /> 持有一个实例——两个通道各播各的，互不影响。
///     </para>
///     <para>
///         <b>不持有通道</b>：要读"当前显示的帧"、要"换到某一帧"时的两个动作由通道以
///         <see cref="CurrentFrame" /> / <see cref="ShowFrame" /> 注入，避免互相反向引用。
///         <b>也没有任何静态播放状态</b>：定时器、单调时钟、倍率缓存全是实例字段。
///     </para>
/// </summary>
internal sealed class ChannelPlayback : ViewModelBase
{
    /// <summary>基准帧率（IMG 里没有每帧时长字段，只能全局统一）。</summary>
    public const int BaseFps = 8;

    /// <summary>九档播放倍率。</summary>
    public static readonly double[] RateMultipliers = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0];

    private readonly AppSettingsStore _settings;
    private readonly ObservableCollection<FrameViewModel> _frames;

    private DispatcherTimer? _timer;
    private readonly Stopwatch _clock = new();
    private double _nextTickDueMs;
    private string[]? _rateOptions;
    private FrameViewModel? _highlighted;

    public ChannelPlayback(AppSettingsStore settings, ObservableCollection<FrameViewModel> frames)
    {
        _settings = settings;
        _frames = frames;
        RateIndex = Math.Clamp(settings.RateIndex, 0, RateMultipliers.Length - 1);
    }

    /// <summary>读"当前显示的帧"（由通道注入）。</summary>
    public Func<FrameViewModel?>? CurrentFrame { get; set; }

    /// <summary>把某一帧推上画布（由通道注入；通道内部走 <c>SelectedFrame</c>）。</summary>
    public Action<FrameViewModel?>? ShowFrame { get; set; }

    /// <summary>全部帧都被隐藏：已自动停表，通道据此写状态栏。</summary>
    public event EventHandler? AllFramesHidden;

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying { get; private set; }

    /// <summary>是否循环播放；关闭时播到末帧自动停止。</summary>
    public bool LoopPlayback {
        get => _loopPlayback;
        set {
            if (_loopPlayback == value) return;
            _loopPlayback = value;
            OnPropertyChanged();
        }
    }

    /// <summary>播放推进过多少次（自动化统计实际 fps 用）。</summary>
    public int AdvanceCount { get; set; }

    /// <summary>播放倍率下标（0~8，默认 3 = 1×）。改值会重建定时器并落盘。</summary>
    public int RateIndex {
        get => _rateIndex;
        set => SetRateIndex(value);
    }

    /// <summary>当前倍率对应的实际帧率（上限 120）。</summary>
    public int EffectiveFps => Math.Clamp((int)Math.Round(BaseFps * RateMultipliers[_rateIndex]), 1, 120);

    /// <summary>下拉选项：倍率 + 对应 fps（首次读取时构建一次并缓存）。</summary>
    public IReadOnlyList<string> RateOptions => _rateOptions ??= BuildRateOptions();

    /// <summary>下拉当前项（ComboBox 直接绑字符串）。</summary>
    public string SelectedRateText {
        get => RateOptions[_rateIndex];
        set {
            var index = RateOptions.ToList().IndexOf(value);
            if (index >= 0) {
                RateIndex = index;
            }
        }
    }

    private bool _loopPlayback = true;
    private int _rateIndex;

    /// <summary>语言切换后清掉倍率文案缓存。</summary>
    public void InvalidateRateOptions() => _rateOptions = null;

    /// <inheritdoc />
    protected override void OnLanguageChanged() => InvalidateRateOptions();

    /// <summary>播放 / 暂停。</summary>
    public void TogglePlay() {
        if (IsPlaying) {
            Stop();
            return;
        }

        if (_frames.Count > 0) {
            StartTimer();
            IsPlaying = true;
        }

        OnPropertyChanged(nameof(IsPlaying));
    }

    /// <summary>停止播放：停表、丢弃定时器、复位状态。</summary>
    public void Stop() {
        _timer?.Stop();
        _timer = null;
        TimerResolution.End();

        IsPlaying = false;
        OnPropertyChanged(nameof(IsPlaying));
    }

    /// <summary>
    ///     推进一拍：定时器到点、自动化脚本、测试三处共用这一条路径。
    ///     <para>
    ///         <b>全部帧都被隐藏</b>时不停在半空：立刻停表并广播 <see cref="AllFramesHidden" />，
    ///         当前帧原地不动（用户还能继续编辑它）。
    ///     </para>
    /// </summary>
    public void Step() {
        if (!HasVisibleFrames) {
            Stop();
            AllFramesHidden?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (Advance(CurrentFrame?.Invoke()) is { } next) {
            ShowFrame?.Invoke(next);
            return;
        }

        Stop();
    }

    /// <summary>
    ///     定时器推进：末帧时按 <see cref="LoopPlayback" /> 决定回绕还是停下。
    ///     <para>
    ///         <b>跳过被隐藏的帧</b>（<see cref="FrameViewModel.IsVisible" /> 为 <c>false</c>）：
    ///         隐藏是"调试时先别放这一帧"，不是删除。全部隐藏时返回 <c>null</c>，
    ///         由调用方停表并在状态栏说明，避免空转。
    ///     </para>
    ///     <para>手动步进（<see cref="StepFrame" />）<b>不跳过</b>：被隐藏的帧仍要能被选中编辑。</para>
    /// </summary>
    /// <param name="current">当前显示的帧。</param>
    /// <returns>下一帧；<c>null</c> 表示应当停止（不循环且已到末帧、没有帧、或全部帧都被隐藏）。</returns>
    public FrameViewModel? Advance(FrameViewModel? current) {
        if (_frames.Count == 0) {
            return null;
        }

        var index = current is null ? -1 : _frames.IndexOf(current);
        if (index < 0) {
            index = -1;
        }

        for (var step = 1; step <= _frames.Count; step++) {
            var next = index + step;
            if (next >= _frames.Count) {
                if (!LoopPlayback) {
                    return null;
                }

                next -= _frames.Count;
            }

            if (_frames[next].IsVisible) {
                return _frames[next];
            }
        }

        // 一圈都没找到可见帧：全被隐藏了
        return null;
    }

    /// <summary>是否还有至少一个可见帧（全部隐藏时播放应立即停下并提示）。</summary>
    public bool HasVisibleFrames {
        get {
            foreach (var frame in _frames) {
                if (frame.IsVisible) return true;
            }

            return false;
        }
    }

    /// <summary>手动步进（正数向后、负数向前），到两端回绕。</summary>
    /// <param name="current">当前显示的帧。</param>
    /// <param name="delta">相对当前帧的偏移。</param>
    /// <returns>目标帧；没有帧时为 <c>null</c>。</returns>
    public FrameViewModel? StepFrame(FrameViewModel? current, int delta) {
        if (_frames.Count == 0) {
            return null;
        }

        var index = current is null ? 0 : _frames.IndexOf(current);
        if (index < 0) index = 0;

        return _frames[((index + delta) % _frames.Count + _frames.Count) % _frames.Count];
    }

    /// <summary>只更新"旧帧 / 新帧"两个对象的高亮，不遍历集合。</summary>
    /// <param name="selected">新的当前帧。</param>
    public void Highlight(FrameViewModel? selected) {
        if (ReferenceEquals(_highlighted, selected)) return;

        if (_highlighted is not null) {
            _highlighted.IsCurrent = false;
        }

        _highlighted = selected;

        if (_highlighted is not null) {
            _highlighted.IsCurrent = true;
        }
    }

    /// <summary>清掉高亮记录（换 IMG / 重建帧列表时调用）。</summary>
    public void ResetHighlight() => Highlight(null);

    /// <summary>设置倍率：夹到合法区间、落盘，并按需重建定时器。</summary>
    private void SetRateIndex(int value) {
        var clamped = Math.Clamp(value, 0, RateMultipliers.Length - 1);
        if (clamped == _rateIndex) {
            return;
        }

        _rateIndex = clamped;
        _settings.RateIndex = clamped;
        _settings.Save();

        if (_timer is not null) {
            StartTimer();
        }

        OnPropertyChanged(nameof(RateIndex));
        OnPropertyChanged(nameof(EffectiveFps));
        OnPropertyChanged(nameof(SelectedRateText));
    }

    /// <summary>按单调时钟排下一拍，抵消处理耗时带来的漂移。</summary>
    private void ScheduleNextTick() {
        if (_timer is null) {
            return;
        }

        var interval = 1000.0 / EffectiveFps;
        _nextTickDueMs += interval;

        var now = _clock.Elapsed.TotalMilliseconds;
        // 落后超过一帧就先追一帧（delay 下限 1ms），避免倍率被拖慢
        var delay = Math.Max(1.0, _nextTickDueMs - now);
        _timer.Interval = TimeSpan.FromMilliseconds(delay);
    }

    private void StartTimer() {
        _timer?.Stop();
        _timer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(1000.0 / EffectiveFps)
        };
        _timer.Tick += (_, _) => {
            AdvanceCount++;
            ScheduleNextTick();
            Step();
        };

        // 默认 15.6ms 定时器精度会把 31ms 的间隔量化成 ~47ms，高倍率下实际帧率明显偏低
        TimerResolution.Begin();

        // 用单调时钟定"下一个到期时刻"，每拍只等剩余时间：
        // 这样每帧的处理耗时不会累积成漂移（DispatcherTimer 固定 Interval 会一路往后拖）
        _clock.Restart();
        _nextTickDueMs = 1000.0 / EffectiveFps;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _nextTickDueMs));
        _timer.Start();
    }

    private string[] BuildRateOptions() {
        var options = new string[RateMultipliers.Length];
        for (var i = 0; i < RateMultipliers.Length; i++) {
            var fps = Math.Clamp((int)Math.Round(BaseFps * RateMultipliers[i]), 1, 120);
            options[i] = $"{FormatRate(RateMultipliers[i])} ({fps} fps)";
        }

        return options;
    }

    private static string FormatRate(double rate) =>
        rate == Math.Floor(rate) ? $"{rate:0}×" : $"{rate:0.##}×";
}
