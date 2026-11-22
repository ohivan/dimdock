using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DimDock.UI;

/// <summary>
/// Lightweight WinUI 3 indeterminate ProgressRing with Fluent 2 breathing arc sweep
/// and continuous rotation, matching native Windows 11 Settings / Control Center spinners.
/// </summary>
internal sealed class FluentProgressRing : Grid
{
    private const double RingSize = 14.0;
    private const double StrokeWidth = 1.65;
    private const double MinSweepDeg = 18.0;
    private const double MaxSweepDeg = 245.0;
    private const double BreathCycleSec = 1.333;
    private const double BaseRotationPeriodSec = 2.0;

    private readonly PathFigure _figure;
    private readonly ArcSegment _arc;
    private readonly Path _path;
    private long _startTimestamp;
    private bool _isRunning;

    public FluentProgressRing(Brush stroke)
    {
        Width = 16;
        Height = 16;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;

        double radius = (RingSize - StrokeWidth) / 2.0;

        _arc = new ArcSegment
        {
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false
        };

        _figure = new PathFigure
        {
            IsClosed = false,
            IsFilled = false,
            Segments = { _arc }
        };

        _path = new Path
        {
            Width = RingSize,
            Height = RingSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stroke = stroke,
            StrokeThickness = StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Data = new PathGeometry { Figures = { _figure } }
        };

        Children.Add(_path);
        UpdateGeometry(0.0);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _isRunning = true;
        _startTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Stop();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double elapsedSec = (Stopwatch.GetTimestamp() - _startTimestamp) / (double)Stopwatch.Frequency;
        UpdateGeometry(elapsedSec);
    }

    private void UpdateGeometry(double elapsedSec)
    {
        var (startDeg, sweepDeg) = ComputeArcAngles(elapsedSec);

        double center = RingSize / 2.0;
        double radius = (RingSize - StrokeWidth) / 2.0;

        double startRad = (startDeg - 90.0) * (Math.PI / 180.0);
        double endRad = (startDeg + sweepDeg - 90.0) * (Math.PI / 180.0);

        _figure.StartPoint = new Point(
            center + radius * Math.Cos(startRad),
            center + radius * Math.Sin(startRad));

        _arc.Point = new Point(
            center + radius * Math.Cos(endRad),
            center + radius * Math.Sin(endRad));
        _arc.IsLargeArc = sweepDeg > 180.0;
    }

    internal static (double StartDeg, double SweepDeg) ComputeArcAngles(double elapsedSec)
    {
        double safeSec = Math.Max(0.0, elapsedSec);
        double cycles = safeSec / BreathCycleSec;
        int completedCycles = (int)Math.Floor(cycles);
        double phase = cycles - completedCycles;

        double spanDelta = MaxSweepDeg - MinSweepDeg;
        double baseRotation = (safeSec / BaseRotationPeriodSec) * 360.0;
        double cycleOffset = completedCycles * spanDelta;

        double headAdvance;
        double tailAdvance;
        if (phase < 0.5)
        {
            double t = phase * 2.0;
            headAdvance = spanDelta * EvaluateFluentStandardEase(t);
            tailAdvance = 0.0;
        }
        else
        {
            double t = (phase - 0.5) * 2.0;
            headAdvance = spanDelta;
            tailAdvance = spanDelta * EvaluateFluentStandardEase(t);
        }

        double startDeg = (baseRotation + cycleOffset + tailAdvance) % 360.0;
        double sweepDeg = Math.Clamp(MinSweepDeg + (headAdvance - tailAdvance), MinSweepDeg, MaxSweepDeg);
        return (startDeg, sweepDeg);
    }

    // Standard Fluent / Material smooth S-curve: cubic-bezier(0.4, 0.0, 0.2, 1.0)
    private static double EvaluateFluentStandardEase(double t) =>
        NativeVSyncSlideAnimator.EvaluateCubicBezier(t, 0.4, 0.0, 0.2, 1.0);
}
