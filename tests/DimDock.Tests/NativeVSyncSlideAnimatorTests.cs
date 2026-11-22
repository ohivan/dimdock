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
}
