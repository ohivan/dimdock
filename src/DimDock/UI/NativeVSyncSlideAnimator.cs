using System.Diagnostics;
using System.Windows.Threading;
using DimDock.Interop;

namespace DimDock.UI;

/// <summary>
/// Drives native-refresh-rate window slide animations using DwmFlush() and
/// Windows 11 Fluent 2 Control Center cubic-bezier easing curves.
/// </summary>
internal sealed class NativeVSyncSlideAnimator : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCts;
    private bool _disposed;

    public NativeVSyncSlideAnimator(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _activeCts is not null;
            }
        }
    }

    public void Start(
        IntPtr hwnd,
        bool opening,
        int xPx,
        int startYPx,
        int endYPx,
        double durationMs,
        Action? onCompleted = null)
    {
        CancellationTokenSource cts;
        CancellationToken ct;

        lock (_gate)
        {
            if (_disposed) return;
            CancelActiveLocked();

            cts = new CancellationTokenSource();
            _activeCts = cts;
            ct = cts.Token;
        }

        // Run a dedicated DwmFlush() loop so movement locks to the monitor's true native refresh rate
        // (e.g. 240Hz / 144Hz / 60Hz) instead of WPF's 60fps CompositionTarget cap.
        var thread = new Thread(() =>
        {
            try
            {
                long startTicks = Stopwatch.GetTimestamp();
                int lastY = startYPx;

                while (!ct.IsCancellationRequested)
                {
                    if (NativeMethods.DwmFlush() != 0)
                    {
                        Thread.Sleep(4);
                    }

                    if (ct.IsCancellationRequested) break;

                    double elapsedMs = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
                    double t = Math.Clamp(elapsedMs / durationMs, 0.0, 1.0);

                    // Official Windows 11 Fluent 2 Control Center curves:
                    // Open:  cubic-bezier(0.1, 0.9, 0.2, 1.0) — explosive start, long smooth settle
                    // Close: cubic-bezier(0.7, 0.0, 1.0, 0.5) — gentle pull-off, fast whip into taskbar
                    double eased = opening
                        ? EvaluateOpenEase(t)
                        : EvaluateCloseEase(t);

                    int currentY = (int)Math.Round(startYPx + (endYPx - startYPx) * eased);
                    if (currentY != lastY)
                    {
                        lastY = currentY;
                        NativeMethods.SetWindowPos(
                            hwnd,
                            IntPtr.Zero,
                            xPx,
                            currentY,
                            0,
                            0,
                            NativeMethods.SWP_NOSIZE |
                            NativeMethods.SWP_NOACTIVATE |
                            NativeMethods.SWP_NOZORDER |
                            NativeMethods.SWP_ASYNCWINDOWPOS);
                    }

                    if (t >= 1.0) break;
                }

                if (!ct.IsCancellationRequested)
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        lock (_gate)
                        {
                            if (!ReferenceEquals(_activeCts, cts)) return;
                            _activeCts = null;
                        }

                        onCompleted?.Invoke();
                    });
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_activeCts, cts) && ct.IsCancellationRequested)
                    {
                        _activeCts = null;
                    }
                }
                cts.Dispose();
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "DimDock_VSyncSlide"
        };

        thread.Start();
    }

    public void Stop()
    {
        lock (_gate)
        {
            CancelActiveLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelActiveLocked();
        }
    }

    private void CancelActiveLocked()
    {
        if (_activeCts is { } cts)
        {
            _activeCts = null;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    internal static double EvaluateOpenEase(double t) => EvaluateCubicBezier(t, 0.1, 0.9, 0.2, 1.0);

    internal static double EvaluateCloseEase(double t) => EvaluateCubicBezier(t, 0.7, 0.0, 1.0, 0.5);

    internal static double EvaluateCubicBezier(double x, double x1, double y1, double x2, double y2)
    {
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;

        // Solve bezier X(u) = x via Newton-Raphson with binary search fallback.
        double u = x;
        for (int i = 0; i < 6; i++)
        {
            double bx = SampleBezier(u, x1, x2) - x;
            if (Math.Abs(bx) < 1e-5) break;
            double dx = SampleBezierDerivative(u, x1, x2);
            if (Math.Abs(dx) < 1e-6) break;
            u = Math.Clamp(u - bx / dx, 0.0, 1.0);
        }

        if (Math.Abs(SampleBezier(u, x1, x2) - x) > 1e-4)
        {
            double lo = 0.0, hi = 1.0;
            for (int i = 0; i < 12; i++)
            {
                u = (lo + hi) * 0.5;
                if (SampleBezier(u, x1, x2) < x) lo = u;
                else hi = u;
            }
        }

        return SampleBezier(u, y1, y2);
    }

    private static double SampleBezier(double u, double p1, double p2)
    {
        double inv = 1.0 - u;
        return 3.0 * inv * inv * u * p1 + 3.0 * inv * u * u * p2 + u * u * u;
    }

    private static double SampleBezierDerivative(double u, double p1, double p2)
    {
        double inv = 1.0 - u;
        return 3.0 * inv * inv * p1 + 6.0 * inv * u * (p2 - p1) + 3.0 * u * u * (1.0 - p2);
    }
}
