using DimDock.Interop;
using DimDock.UI;
using Xunit;

namespace DimDock.Tests;

public sealed class TaskbarPlacementHelperTests
{
    [Fact]
    public void RectsOverlap_DetectsIntersectionAndDisjointRects()
    {
        var a = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var inside = new RECT { Left = 0, Top = 1032, Right = 1920, Bottom = 1080 };
        var adjacent = new RECT { Left = 1920, Top = 0, Right = 3840, Bottom = 1080 };

        Assert.True(TaskbarPlacementHelper.RectsOverlap(a, inside));
        Assert.False(TaskbarPlacementHelper.RectsOverlap(a, adjacent));
    }

    [Fact]
    public void ComputeEffectiveVerticalBounds_BottomTaskbar_UsesWorkBottom()
    {
        var monitor = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var work = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1032 };
        var taskbar = new RECT { Left = 0, Top = 1032, Right = 1920, Bottom = 1080 };

        var bounds = TaskbarPlacementHelper.ComputeEffectiveVerticalBounds(monitor, work, taskbar, hasTaskbarRect: true);

        Assert.Equal(0, bounds.EffectiveTopPx);
        Assert.Equal(1032, bounds.EffectiveBottomPx);
        Assert.False(bounds.IsTaskbarAtTop);
    }

    [Fact]
    public void ComputeEffectiveVerticalBounds_AutoHideBottomTaskbarPoppedUp_ShrinksEffectiveBottom()
    {
        var monitor = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var work = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }; // Auto-hide leaves rcWork == rcMonitor
        var poppedTaskbar = new RECT { Left = 0, Top = 1032, Right = 1920, Bottom = 1080 };

        var bounds = TaskbarPlacementHelper.ComputeEffectiveVerticalBounds(monitor, work, poppedTaskbar, hasTaskbarRect: true);

        Assert.Equal(0, bounds.EffectiveTopPx);
        Assert.Equal(1032, bounds.EffectiveBottomPx);
        Assert.False(bounds.IsTaskbarAtTop);
    }

    [Fact]
    public void ComputeEffectiveVerticalBounds_AutoHideBottomTaskbarHidden_UsesFullMonitorBottom()
    {
        var monitor = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var work = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var hiddenTaskbar = new RECT { Left = 0, Top = 1078, Right = 1920, Bottom = 1080 };

        var bounds = TaskbarPlacementHelper.ComputeEffectiveVerticalBounds(monitor, work, hiddenTaskbar, hasTaskbarRect: true);

        Assert.Equal(0, bounds.EffectiveTopPx);
        Assert.Equal(1080, bounds.EffectiveBottomPx);
        Assert.False(bounds.IsTaskbarAtTop);
    }

    [Fact]
    public void ComputeEffectiveVerticalBounds_TopTaskbar_FlagsIsTaskbarAtTop()
    {
        var monitor = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var work = new RECT { Left = 0, Top = 48, Right = 1920, Bottom = 1080 };
        var topTaskbar = new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 48 };

        var bounds = TaskbarPlacementHelper.ComputeEffectiveVerticalBounds(monitor, work, topTaskbar, hasTaskbarRect: true);

        Assert.Equal(48, bounds.EffectiveTopPx);
        Assert.Equal(1080, bounds.EffectiveBottomPx);
        Assert.True(bounds.IsTaskbarAtTop);
    }
}
