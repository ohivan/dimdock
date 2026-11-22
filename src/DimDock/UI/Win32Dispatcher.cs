using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DimDock.Interop;

namespace DimDock.UI;

/// <summary>
/// Lightweight Win32 message-loop dispatcher replacing WPF's Dispatcher + DispatcherTimer.
/// Marshals cross-thread callbacks via WM_APP posts to a hidden top-level host window.
/// </summary>
internal sealed class Win32Dispatcher : IDisposable
{
    private const uint WmInvoke = NativeMethods.WM_APP + 100;

    private readonly int _uiThreadId;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Dictionary<nuint, Action> _timers = [];
    private readonly List<Func<IntPtr, uint, IntPtr, IntPtr, (bool Handled, IntPtr Result)>> _hooks = [];
    private readonly NativeMethods.WndProcDelegate _wndProcDelegate;
    private readonly string _className;
    private IntPtr _hwnd;
    private nuint _nextTimerId = 1;
    private bool _disposed;

    public Win32Dispatcher()
    {
        _uiThreadId = Environment.CurrentManagedThreadId;
        _wndProcDelegate = WndProc;
        _className = $"DimDock_Host_{Environment.ProcessId}";

        IntPtr hInstance = NativeMethods.GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = _className
        };

        NativeMethods.RegisterClassExW(ref wc);

        // Hidden top-level window (WS_POPUP | WS_EX_TOOLWINDOW) so broadcast messages like
        // TaskbarCreated, WM_SETTINGCHANGE, WM_DISPLAYCHANGE, WM_POWERBROADCAST arrive.
        _hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_TOOLWINDOW,
            _className,
            "DimDock_TrayHost",
            NativeMethods.WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);
    }

    public IntPtr HostHwnd => _hwnd;

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _uiThreadId;

    public void AddMessageHook(Func<IntPtr, uint, IntPtr, IntPtr, (bool Handled, IntPtr Result)> hook)
    {
        _hooks.Add(hook);
    }

    public void RemoveMessageHook(Func<IntPtr, uint, IntPtr, IntPtr, (bool Handled, IntPtr Result)> hook)
    {
        _hooks.Remove(hook);
    }

    public void BeginInvoke(Action action)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        _queue.Enqueue(action);
        NativeMethods.PostMessageW(_hwnd, WmInvoke, IntPtr.Zero, IntPtr.Zero);
    }

    public void Invoke(Action action)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        if (CheckAccess())
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? captured = null;
        BeginInvoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (captured is not null)
        {
            throw captured;
        }
    }

    public Win32Timer CreateTimer(TimeSpan interval, Action onTick)
    {
        nuint id = _nextTimerId++;
        return new Win32Timer(this, id, interval, onTick);
    }

    internal void StartTimer(nuint id, uint intervalMs, Action callback)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        _timers[id] = callback;
        NativeMethods.SetTimer(_hwnd, id, Math.Max(1u, intervalMs), IntPtr.Zero);
    }

    internal void StopTimer(nuint id)
    {
        _timers.Remove(id);
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_hwnd, id);
        }
    }

    public void RunMessageLoop()
    {
        while (NativeMethods.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
    }

    public void Shutdown()
    {
        if (_disposed) return;
        if (CheckAccess())
        {
            NativeMethods.PostQuitMessage(0);
        }
        else
        {
            BeginInvoke(() => NativeMethods.PostQuitMessage(0));
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmInvoke)
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Services.AppLogger.Error("Unhandled UI dispatcher exception", ex);
                }
            }
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_TIMER)
        {
            nuint id = (nuint)wParam.ToInt64();
            if (_timers.TryGetValue(id, out var callback))
            {
                try
                {
                    callback();
                }
                catch (Exception ex)
                {
                    Services.AppLogger.Error("Unhandled timer exception", ex);
                }
            }
            return IntPtr.Zero;
        }

        for (int i = 0; i < _hooks.Count; i++)
        {
            var (handled, result) = _hooks[i](hwnd, msg, wParam, lParam);
            if (handled)
            {
                return result;
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (nuint id in _timers.Keys.ToList())
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.KillTimer(_hwnd, id);
            }
        }
        _timers.Clear();
        _hooks.Clear();

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}

internal sealed class Win32Timer
{
    private readonly Win32Dispatcher _dispatcher;
    private readonly nuint _id;
    private readonly Action _onTick;

    public Win32Timer(Win32Dispatcher dispatcher, nuint id, TimeSpan interval, Action onTick)
    {
        _dispatcher = dispatcher;
        _id = id;
        Interval = interval;
        _onTick = onTick;
    }

    public TimeSpan Interval { get; set; }

    public void Start()
    {
        _dispatcher.StartTimer(_id, (uint)Math.Max(1, Math.Round(Interval.TotalMilliseconds)), _onTick);
    }

    public void Stop()
    {
        _dispatcher.StopTimer(_id);
    }
}
