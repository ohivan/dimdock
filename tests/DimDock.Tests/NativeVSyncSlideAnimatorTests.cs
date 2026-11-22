using DimDock.UI;
using Xunit;

namespace DimDock.Tests;

public sealed class NativeVSyncSlideAnimatorTests
{
    [Fact]
    public void EvaluateCubicBezier_ClampsBoundariesExactly()
    {
        Assert.Equal(0.0, NativeVSyncSlideAnimator.EvaluateOpenEase(-0.5));
        Assert.Equal(0.0, NativeVSyncSlideAnimator.EvaluateOpenEase(0.0));
        Assert.Equal(1.0, NativeVSyncSlideAnimator.EvaluateOpenEase(1.0));
        Assert.Equal(1.0, NativeVSyncSlideAnimator.EvaluateOpenEase(1.5));

        Assert.Equal(0.0, NativeVSyncSlideAnimator.EvaluateCloseEase(0.0));
        Assert.Equal(1.0, NativeVSyncSlideAnimator.EvaluateCloseEase(1.0));

        Assert.Equal(0.0, NativeVSyncSlideAnimator.EvaluateMenuOpenEase(-0.5));
        Assert.Equal(0.0, NativeVSyncSlideAnimator.EvaluateMenuOpenEase(0.0));
        Assert.Equal(1.0, NativeVSyncSlideAnimator.EvaluateMenuOpenEase(1.0));
        Assert.Equal(1.0, NativeVSyncSlideAnimator.EvaluateMenuOpenEase(1.5));
    }

    [Fact]
    public void FluentOpenCurve_IsMonotonicAndFastOut()
    {
        double prev = 0.0;
        for (int i = 1; i <= 100; i++)
        {
            double t = i / 100.0;
            double y = NativeVSyncSlideAnimator.EvaluateOpenEase(t);
            Assert.True(y >= prev, $"Expected monotonic open curve at t={t}: {y} < {prev}");
            Assert.InRange(y, 0.0, 1.0);
            prev = y;
        }

        // Fluent 2 Control Center open curve (0.1, 0.9, 0.2, 1.0) accelerates sharply at start.
        Assert.True(NativeVSyncSlideAnimator.EvaluateOpenEase(0.25) > 0.75);
    }

    [Fact]
    public void FluentCloseCurve_IsMonotonicAndFastIn()
    {
        double prev = 0.0;
        for (int i = 1; i <= 100; i++)
        {
            double t = i / 100.0;
            double y = NativeVSyncSlideAnimator.EvaluateCloseEase(t);
            Assert.True(y >= prev, $"Expected monotonic close curve at t={t}: {y} < {prev}");
            Assert.InRange(y, 0.0, 1.0);
            prev = y;
        }

        // Fluent 2 close curve (0.7, 0.0, 1.0, 0.5) eases gently off rest before accelerating.
        Assert.True(NativeVSyncSlideAnimator.EvaluateCloseEase(0.25) < 0.15);
    }

    [Fact]
    public void MenuOpenCurve_IsMonotonicAndSmoothEaseOut()
    {
        double prev = 0.0;
        for (int i = 1; i <= 100; i++)
        {
            double t = i / 100.0;
            double y = NativeVSyncSlideAnimator.EvaluateMenuOpenEase(t);
            Assert.True(y >= prev, $"Expected monotonic menu open curve at t={t}: {y} < {prev}");
            Assert.InRange(y, 0.0, 1.0);
            prev = y;
        }

        // Cubic ease-out (0.33, 1.0, 0.68, 1.0) moves crisply at start while preserving visible glide.
        Assert.InRange(NativeVSyncSlideAnimator.EvaluateMenuOpenEase(0.25), 0.5, 0.65);
    }

    [Fact]
    public void TrayContextMenu_ComputeSlideStartY_MovesUpFromBottomAndDownFromTop()
    {
        Assert.Equal(816, TrayContextMenu.ComputeSlideStartY(800, dpiScale: 1.0, isTaskbarAtTop: false));
        Assert.Equal(824, TrayContextMenu.ComputeSlideStartY(800, dpiScale: 1.5, isTaskbarAtTop: false));
        Assert.Equal(44, TrayContextMenu.ComputeSlideStartY(60, dpiScale: 1.0, isTaskbarAtTop: true));
        Assert.Equal(816, TrayContextMenu.ComputeSlideStartY(800, dpiScale: 0.0, isTaskbarAtTop: false));
    }
}
