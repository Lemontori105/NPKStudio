using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using NPKStudio.Desktop.Controls;
using NPKStudio.Desktop.Input;
using NPKStudio.Desktop.ViewModels;
using NPKStudio.Desktop.Views;

using Xunit;

namespace NPKStudio.Desktop.Tests;

/// <summary>
///     滚轮 / 触控板滚动 / 触控板捏合 → 缩放的换算（见 <see cref="ZoomInput" />）。
///     <para>重点回归：触控板一次滑动是一串很小的 Delta，旧实现每个事件都缩 10%，几十个事件就缩到最小。</para>
/// </summary>
public sealed class ZoomInputTests
{
    private const double Tolerance = 1e-9;

    [Theory]
    [InlineData(1.0, 1.1)]
    [InlineData(-1.0, 1 / 1.1)]
    [InlineData(0.0, 1.0)]
    [InlineData(2.0, 1.21)]
    public void FactorForTicks_OneTickIsTenPercent(double ticks, double expected) =>
        Assert.Equal(expected, ZoomInput.FactorForTicks(ticks), 9);

    [Fact]
    public void FactorForTicks_IsProportional_AndClamped() {
        Assert.Equal(Math.Sqrt(1.1), ZoomInput.FactorForTicks(0.5), 9);
        Assert.Equal(Math.Pow(1.1, ZoomInput.MaxTicksPerEvent), ZoomInput.FactorForTicks(50), 9);
        Assert.Equal(Math.Pow(1.1, -ZoomInput.MaxTicksPerEvent), ZoomInput.FactorForTicks(-50), 9);
        Assert.Equal(1.0, ZoomInput.FactorForTicks(double.NaN));
    }

    /// <summary>触控板滑一下 ≈ 40 个 0.05 格的小事件（共 2 格）：新实现只缩约 17%，旧实现会缩到 2%。</summary>
    [Fact]
    public void TrackpadSwipe_ZoomsByTotalDistance_NotByEventCount() {
        var zoom = 1.0;
        for (var i = 0; i < 40; i++) {
            zoom *= ZoomInput.FactorForTicks(-0.05);
        }

        Assert.Equal(Math.Pow(1.1, -2), zoom, 9);
        Assert.True(zoom > 0.8, $"一次滑动缩得太多：{zoom}");
    }

    [Fact]
    public void StepsForTicks_AccumulatesUntilOneFullTick() {
        var input = new ZoomInput();
        Assert.Equal(0, input.StepsForTicks(0.2));
        Assert.Equal(0, input.StepsForTicks(0.2));
        Assert.Equal(0, input.StepsForTicks(0.2));
        Assert.Equal(0, input.StepsForTicks(0.2));
        Assert.Equal(1, input.StepsForTicks(0.2));
        Assert.Equal(0, input.StepsForTicks(0.2));
    }

    [Fact]
    public void StepsForTicks_WholeTicksStepImmediately_AndKeepRemainder() {
        var input = new ZoomInput();
        Assert.Equal(1, input.StepsForTicks(1));
        Assert.Equal(-1, input.StepsForTicks(-1));
        Assert.Equal(2, input.StepsForTicks(2.5));
        Assert.Equal(1, input.StepsForTicks(0.5));
        Assert.Equal((int)ZoomInput.MaxTicksPerEvent, new ZoomInput().StepsForTicks(50));
        Assert.Equal(0, input.StepsForTicks(0));
        Assert.Equal(0, input.StepsForTicks(double.NaN));
    }

    [Fact]
    public void StepsForTicks_DirectionChangeDropsPendingAmount() {
        var input = new ZoomInput();
        Assert.Equal(0, input.StepsForTicks(0.6));
        // 反向：之前攒的 0.6 作废，不用先抵消
        Assert.Equal(0, input.StepsForTicks(-0.3));
        Assert.Equal(-1, input.StepsForTicks(-0.7));
    }

    [Fact]
    public void ResetPendingTicks_DropsPendingAmount() {
        var input = new ZoomInput();
        Assert.Equal(0, input.StepsForTicks(0.9));
        input.ResetPendingTicks();
        Assert.Equal(0, input.StepsForTicks(0.5));
    }

    [Fact]
    public void PinchTarget_MultipliesWithinOneGesture() {
        var input = new ZoomInput();
        Assert.Equal(1.1, input.PinchTarget(1.0, 0.1, nowMs: 0), 9);
        // 同一轮捏合里不看 currentZoom（像素对齐时实际缩放被吸附过），在虚拟缩放上继续乘
        Assert.Equal(1.21, input.PinchTarget(1.0, 0.1, nowMs: 100), 9);
        Assert.Equal(1.21 * 0.9, input.PinchTarget(1.0, -0.1, nowMs: 200), 9);
    }

    [Fact]
    public void PinchTarget_NewGestureStartsFromCurrentZoom() {
        var input = new ZoomInput();
        input.PinchTarget(1.0, 0.5, nowMs: 0);
        Assert.Equal(2.2, input.PinchTarget(2.0, 0.1, nowMs: ZoomInput.GestureIdleMs + 1), 9);

        input.ResetPinch();
        Assert.Equal(3.3, input.PinchTarget(3.0, 0.1, nowMs: ZoomInput.GestureIdleMs + 2), 9);
    }

    [Fact]
    public void PinchTarget_ClampsToZoomRangeAndMagnificationLimits() {
        var input = new ZoomInput();
        var zoom = 1.0;
        for (var i = 0; i < 100; i++) {
            zoom = input.PinchTarget(zoom, 0.5, nowMs: i);
        }

        Assert.Equal(ViewportTransform.MaxZoom, zoom, 9);

        var shrink = new ZoomInput();
        // 单个事件的增量最多 -50%，异常的 -0.99 不会一下缩到底
        Assert.Equal(0.5, shrink.PinchTarget(1.0, -0.99, nowMs: 0), 9);
        Assert.Equal(0.5, shrink.PinchTarget(1.0, double.NaN, nowMs: 1), 9);
    }

    /// <summary>像素对齐：一次小幅捏合不跳档，捏够了才吸附到下一档。</summary>
    [Fact]
    public void Pinch_WithPixelAlign_SnapsOnlyWhenFarEnough() {
        var input = new ZoomInput();
        var levels = new PixelZoom();

        Assert.Equal(1.0, levels.Snap(input.PinchTarget(1.0, 0.05, nowMs: 0)), Tolerance);
        Assert.Equal(1.0, levels.Snap(input.PinchTarget(1.0, 0.2, nowMs: 50)), Tolerance);
        // 1.05 × 1.2 × 1.3 ≈ 1.64，离 2 更近
        Assert.Equal(2.0, levels.Snap(input.PinchTarget(1.0, 0.3, nowMs: 100)), Tolerance);
    }

    /// <summary>双指拖动：滚动量乘回 50 点，内容跟着手指方向走（和 ScrollViewer 同向）。</summary>
    [Fact]
    public void PanDelta_ScalesBackToPoints() {
        Assert.Equal(new Vector(10, -25), ZoomInput.PanDelta(new Vector(0.2, -0.5), shift: false));
        Assert.Equal(default, ZoomInput.PanDelta(default, shift: false));
        Assert.Equal(default, ZoomInput.PanDelta(new Vector(double.NaN, 1), shift: false));
    }

    /// <summary>Shift + 只有竖向的滚轮（普通鼠标）当横向；已经有横向分量时（macOS 自己转过）原样用。</summary>
    [Fact]
    public void PanDelta_ShiftTurnsVerticalWheelIntoHorizontal() {
        Assert.Equal(new Vector(50, 0), ZoomInput.PanDelta(new Vector(0, 1), shift: true));
        Assert.Equal(new Vector(-50, 0), ZoomInput.PanDelta(new Vector(-1, 0), shift: true));
    }

    [Fact]
    public void SmartZoom_TargetIsDoubleFit_Clamped() {
        Assert.True(ZoomInput.IsNearFit(2.0, 2.0));
        Assert.True(ZoomInput.IsNearFit(2.01, 2.0));
        Assert.False(ZoomInput.IsNearFit(2.1, 2.0));
        Assert.False(ZoomInput.IsNearFit(2.0, 0));

        Assert.Equal(3.0, ZoomInput.SmartZoomTarget(1.5), 9);
        Assert.Equal(ViewportTransform.MaxZoom, ZoomInput.SmartZoomTarget(12), 9);
    }

    /// <summary>智能缩放：适配窗口 → 放大（像素对齐时落在合法档位）→ 再点一次回到适配窗口；手动缩放过的直接回适配。</summary>
    [AvaloniaFact]
    public async Task SmartZoom_TogglesBetweenFitAndZoomedIn() {
        using var folder = TestFolder.Create();
        folder.WriteImg("big.img", frameCount: 1, width: 300, height: 300);

        var window = new MainWindow();
        window.Show();
        var vm = (MainWindowViewModel)window.DataContext!;
        await vm.OpenFolderAsync(folder.Path);

        var canvas = window.FindControl<ImageCanvas>("FrameCanvas");
        Assert.NotNull(canvas);
        Assert.NotNull(canvas!.Image);

        canvas.FitToView();
        var fit = canvas.Zoom;
        Assert.True(fit < ViewportTransform.MaxZoom / 2, $"适配缩放 {fit} 太大，放不大一倍，测试前提不成立");

        canvas.ToggleSmartZoom();
        var zoomed = canvas.Zoom;
        Assert.True(zoomed >= fit * 1.5, $"智能缩放没有放大：fit={fit}，现在={zoomed}");
        if (canvas.IsPixelAlignEnabled) {
            Assert.True(new PixelZoom().IsLevel(zoomed), $"像素对齐下放大到了非整档 {zoomed}");
        }

        canvas.ToggleSmartZoom();
        Assert.Equal(fit, canvas.Zoom, 9);

        canvas.SetZoomLevel(fit * 3);
        Assert.False(ZoomInput.IsNearFit(canvas.Zoom, fit));
        canvas.ToggleSmartZoom();
        Assert.Equal(fit, canvas.Zoom, 9);

        window.Close();
    }

    /// <summary>无头测试里没有原生 AvnView：挂接只是什么也不做，不能抛异常。</summary>
    [Fact]
    public void MacSmartZoom_InstallIsHarmlessWithoutNativeView() {
        var ex = Record.Exception(() => MacSmartZoom.EnsureInstalled(() => { }));
        Assert.Null(ex);
    }
}
