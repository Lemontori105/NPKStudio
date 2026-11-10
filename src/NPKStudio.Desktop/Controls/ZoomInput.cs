using Avalonia;

namespace NPKStudio.Desktop.Controls;

/// <summary>
///     把滚轮 / 触控板滚动 / 触控板捏合换算成缩放动作（纯数值逻辑，可单元测试）。
///     <para>
///         <b>单位"格"</b>：Avalonia 已经把滚动量归一化过——macOS 后端（libAvaloniaNative 的
///         <c>-[AvnView mouseEvent:withType:]</c>）对触控板的精确滚动除以 50、对普通滚轮除以 5，
///         Windows 一格滚轮是 1。所以这里把 <c>Delta.Y = 1</c> 当作"一格"：缩放 10%，像素对齐时走一档。
///     </para>
///     <para>
///         以前每个滚动事件不论大小都缩放 10%：触控板滑一下（含松手后的惯性）会连发几十个小事件，
///         一下子缩到最小 10%。现在按滚动量折算，滑得越多缩得越多。
///     </para>
///     <para>
///         <b>捏合</b>：macOS 后端把 <c>NSEvent.magnification</c>（每个事件的放大率增量）原样作为 Delta 传上来，
///         这里按 <c>1 + magnification</c> 连乘；像素对齐时在"虚拟缩放"上连乘、显示时吸附到最近档位，
///         这样捏合幅度和缩放幅度成正比，不会因为一次小捏合就跳一整档。
///     </para>
/// </summary>
internal sealed class ZoomInput {
    /// <summary>一格对应的缩放倍率（10%）。</summary>
    public const double StepFactor = 1.1;

    /// <summary>单个事件最多算几格（快速甩滚轮时的加速度上限，避免一次跳太多）。</summary>
    public const double MaxTicksPerEvent = 4;

    /// <summary>两次捏合事件间隔超过这个毫秒数，就当作新一轮捏合，从当前实际缩放重新起算。</summary>
    public const long GestureIdleMs = 300;

    /// <summary>单个捏合事件的放大率增量上下限（防止异常值）。</summary>
    private const double MinMagnification = -0.5;

    private const double MaxMagnification = 1.0;

    // 像素对齐模式下尚未用掉的滚动量（不足一格先攒着）
    private double _pendingTicks;

    // 本轮捏合的虚拟缩放（未吸附）与上次事件时间
    private double? _pinchZoom;
    private long _lastPinchAtMs;

    /// <summary>连续缩放：本次滚动量对应的乘数（一格 = ×1.1，按比例折算，单次最多 <see cref="MaxTicksPerEvent" /> 格）。</summary>
    public static double FactorForTicks(double ticks) {
        if (double.IsNaN(ticks) || double.IsInfinity(ticks)) return 1.0;
        return Math.Pow(StepFactor, Math.Clamp(ticks, -MaxTicksPerEvent, MaxTicksPerEvent));
    }

    /// <summary>
    ///     像素对齐缩放：把滚动量攒起来，满一格走一档。返回本次要走的档数（正 = 放大，负 = 缩小，0 = 先攒着）。
    ///     <para>方向一反转就丢掉之前攒的量，避免"往回滚还要先抵消"的拖沓感。</para>
    /// </summary>
    public int StepsForTicks(double ticks) {
        if (double.IsNaN(ticks) || double.IsInfinity(ticks) || ticks == 0) return 0;

        ticks = Math.Clamp(ticks, -MaxTicksPerEvent, MaxTicksPerEvent);
        if (_pendingTicks != 0 && Math.Sign(_pendingTicks) != Math.Sign(ticks)) {
            _pendingTicks = 0;
        }

        _pendingTicks += ticks;
        // 浮点累加 0.2×5 可能得到 0.9999999，留一点容差
        var steps = (int)Math.Truncate(_pendingTicks + Math.Sign(_pendingTicks) * 1e-9);
        _pendingTicks -= steps;
        return steps;
    }

    /// <summary>
    ///     捏合：在本轮的虚拟缩放上乘以 <c>1 + magnification</c>，返回新的目标缩放（未吸附、已夹到合法范围）。
    /// </summary>
    /// <param name="currentZoom">画布当前实际缩放（新一轮捏合从它起算）。</param>
    /// <param name="magnification">本事件的放大率增量（正 = 张开放大，负 = 捏合缩小）。</param>
    /// <param name="nowMs">单调时钟毫秒数（测试可注入）。</param>
    public double PinchTarget(double currentZoom, double magnification, long nowMs) {
        if (_pinchZoom is null || nowMs - _lastPinchAtMs > GestureIdleMs) {
            _pinchZoom = currentZoom;
        }

        _lastPinchAtMs = nowMs;
        if (double.IsNaN(magnification) || double.IsInfinity(magnification)) return _pinchZoom.Value;

        var m = Math.Clamp(magnification, MinMagnification, MaxMagnification);
        _pinchZoom = Math.Clamp(_pinchZoom.Value * (1 + m), ViewportTransform.MinZoom, ViewportTransform.MaxZoom);
        return _pinchZoom.Value;
    }

    /// <summary>别的途径改了缩放（滚轮、适配窗口、1:1）：结束当前这轮捏合，下次捏合从实际缩放重新起算。</summary>
    public void ResetPinch() => _pinchZoom = null;

    /// <summary>丢掉像素对齐模式下攒着的滚动量。</summary>
    public void ResetPendingTicks() => _pendingTicks = 0;

    // ==================== 平移（不按修饰键的滚动 = 双指拖动） ====================

    /// <summary>
    ///     一格滚动量平移多少点。macOS 后端把触控板精确滚动量除以 50 后才交上来，这里乘回 50，
    ///     双指拖多少点画面就跟着走多少点；鼠标滚轮一格约平移 50 点。
    /// </summary>
    public const double PanPointsPerTick = 50;

    /// <summary>
    ///     滚动量 → 画面平移量（加到视口 Offset 上，内容跟着手指方向走，和 Avalonia 的 ScrollViewer 同向）。
    ///     <para>按住 Shift 且只有竖向滚动量时（普通鼠标）当作横向；macOS 自己会把 Shift+滚轮换成横向，那时 X 本来就有值。</para>
    /// </summary>
    public static Vector PanDelta(Vector delta, bool shift) {
        if (double.IsNaN(delta.X) || double.IsNaN(delta.Y) || double.IsInfinity(delta.X) || double.IsInfinity(delta.Y)) {
            return default;
        }

        if (shift && delta.X == 0) {
            delta = new Vector(delta.Y, 0);
        }

        return delta * PanPointsPerTick;
    }

    // ==================== 智能缩放（双指轻点两下） ====================

    /// <summary>智能缩放放大到"适配窗口"的几倍。</summary>
    public const double SmartZoomFactor = 2.0;

    /// <summary>当前缩放是否就是"适配窗口"那一档（相差不到 1%）。</summary>
    public static bool IsNearFit(double zoom, double fitZoom) =>
        fitZoom > 0 && Math.Abs(zoom / fitZoom - 1) < 0.01;

    /// <summary>智能缩放的放大目标（未吸附档位，已夹到合法范围）。</summary>
    public static double SmartZoomTarget(double fitZoom) =>
        Math.Clamp(fitZoom * SmartZoomFactor, ViewportTransform.MinZoom, ViewportTransform.MaxZoom);
}
