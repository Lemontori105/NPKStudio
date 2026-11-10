namespace NPKStudio.Desktop.Controls;

/// <summary>
///     像素完美缩放档位。
///     <para>
///         DNF 是像素艺术：放大时只允许整数倍（每个源像素恰好铺成 N×N 个屏幕像素），
///         缩小时只允许整数分之一（1/2、1/3、1/4…），避免出现"半个像素"的采样边界。
///     </para>
///     <para>
///         这里只做纯数值换算（不依赖控件），<see cref="ImageCanvas" /> 在开启
///         <see cref="ImageCanvas.IsPixelAlignEnabled" /> 时调用；关闭时保持原来的连续缩放。
///     </para>
/// </summary>
internal sealed class PixelZoom {
    /// <summary>合法档位（升序）。缩小的最小档是 1/10，与 <see cref="ViewportTransform.MinZoom" /> 对齐。</summary>
    private static readonly double[] Levels = [
        1.0 / 10,
        1.0 / 8,
        1.0 / 6,
        1.0 / 5,
        1.0 / 4,
        1.0 / 3,
        1.0 / 2,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16
    ];

    private const double Epsilon = 1e-9;

    /// <summary>吸附到最近的合法档位。</summary>
    public double Snap(double zoom) {
        var clamped = Math.Clamp(zoom, ViewportTransform.MinZoom, ViewportTransform.MaxZoom);
        var best = Levels[0];
        var bestDistance = double.MaxValue;
        foreach (var level in Levels) {
            var distance = Math.Abs(level - clamped);
            if (distance >= bestDistance) continue;
            best = level;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>向下吸附到不大于 <paramref name="zoom" /> 的合法档位（适配窗口用：宁可留边，也不要非整数缩放）。</summary>
    public double SnapDown(double zoom) {
        var clamped = Math.Clamp(zoom, ViewportTransform.MinZoom, ViewportTransform.MaxZoom);
        for (var i = Levels.Length - 1; i >= 0; i--) {
            if (Levels[i] <= clamped + Epsilon) return Levels[i];
        }

        return Levels[0];
    }

    /// <summary>
    ///     从当前档位往指定方向走一档（⌘+滚轮；Windows 为 Ctrl+滚轮）。
    ///     <para>若当前值不在档位上（例如刚关闭开关后留下的 3.7），向上取第一个更大的档、向下取第一个更小的档。</para>
    /// </summary>
    public double Next(double current, int direction) {
        if (direction > 0) {
            foreach (var level in Levels) {
                if (level > current + Epsilon) return level;
            }

            return Levels[^1];
        }

        for (var i = Levels.Length - 1; i >= 0; i--) {
            if (Levels[i] < current - Epsilon) return Levels[i];
        }

        return Levels[0];
    }

    /// <summary>是否为合法档位（测试与诊断用）。</summary>
    public bool IsLevel(double zoom) {
        foreach (var level in Levels) {
            if (Math.Abs(level - zoom) <= 1e-6) return true;
        }

        return false;
    }
}
