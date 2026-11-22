using DimDock.Interop;

namespace DimDock.UI;

/// <summary>
/// Lightweight WinUI 3 indeterminate ProgressRing math + Direct2D renderer
/// with Fluent 2 breathing arc sweep and continuous rotation.
/// </summary>
internal static class FluentProgressRing
{
    private const float RingSize = 14.0f;
    private const float StrokeWidth = 1.65f;
    private const double MinSweepDeg = 18.0;
    private const double MaxSweepDeg = 245.0;
    private const double BreathCycleSec = 1.333;
    private const double BaseRotationPeriodSec = 2.0;

    public static void Draw(
        IntPtr d2dFactory,
        IntPtr rt,
        IntPtr brush,
        IntPtr roundStrokeStyle,
        float centerX,
        float centerY,
        double elapsedSec)
    {
        if (d2dFactory == IntPtr.Zero || rt == IntPtr.Zero || brush == IntPtr.Zero) return;

        var (startDeg, sweepDeg) = ComputeArcAngles(elapsedSec);
        float radius = (RingSize - StrokeWidth) * 0.5f;

        double startRad = (startDeg - 90.0) * (Math.PI / 180.0);
        double endRad = (startDeg + sweepDeg - 90.0) * (Math.PI / 180.0);

        var startPt = new D2D1_POINT_2F(
            centerX + (float)(radius * Math.Cos(startRad)),
            centerY + (float)(radius * Math.Sin(startRad)));

        var endPt = new D2D1_POINT_2F(
            centerX + (float)(radius * Math.Cos(endRad)),
            centerY + (float)(radius * Math.Sin(endRad)));

        IntPtr geom = Direct2D.CreatePathGeometry(d2dFactory);
        if (geom == IntPtr.Zero) return;

        try
        {
            IntPtr sink = Direct2D.OpenPathGeometry(geom);
            if (sink == IntPtr.Zero) return;

            try
            {
                Direct2D.SinkBeginFigure(sink, startPt);
                Direct2D.SinkAddArc(sink, new D2D1_ARC_SEGMENT
                {
                    Point = endPt,
                    Size = new D2D1_SIZE_F(radius, radius),
                    RotationAngle = 0f,
                    SweepDirection = Direct2D.D2D1_SWEEP_DIRECTION_CLOCKWISE,
                    ArcSize = sweepDeg > 180.0 ? Direct2D.D2D1_ARC_SIZE_LARGE : Direct2D.D2D1_ARC_SIZE_SMALL
                });
                Direct2D.SinkEndFigure(sink);
                Direct2D.SinkClose(sink);
            }
            finally
            {
                Direct2D.Release(sink);
            }

            Direct2D.DrawGeometry(rt, geom, brush, StrokeWidth, roundStrokeStyle);
        }
        finally
        {
            Direct2D.Release(geom);
        }
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

    private static double EvaluateFluentStandardEase(double t) =>
        NativeVSyncSlideAnimator.EvaluateCubicBezier(t, 0.4, 0.0, 0.2, 1.0);
}
