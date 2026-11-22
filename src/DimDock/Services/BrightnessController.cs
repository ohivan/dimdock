using System.Collections.ObjectModel;
using DimDock.Models;

namespace DimDock.Services;

public sealed class BrightnessController : IDisposable
{
    private readonly IMonitorBackend _backend;
    private readonly SettingsService _settingsService;
    private readonly TimeSpan _writeThrottle;
    private readonly Action<Action> _uiInvoke;
    private readonly Dictionary<string, MonitorWriter> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile MonitorInfo[] _monitorsSnapshot = [];
    private int _disposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public BrightnessController(
        IMonitorBackend backend,
        SettingsService settingsService,
        TimeSpan? writeThrottle = null,
        Action<Action>? uiInvoke = null)
    {
        _backend = backend;
        _settingsService = settingsService;
        _writeThrottle = writeThrottle ?? TimeSpan.FromMilliseconds(35);
        _uiInvoke = uiInvoke ?? (action => action());
    }

    public ObservableCollection<MonitorInfo> Monitors { get; } = [];
    public AppSettings Settings => _settingsService.Current;

    public event EventHandler? MonitorsRefreshed;
    public event EventHandler<int?>? BrightnessSummaryChanged;

    public async Task RefreshMonitorsAsync()
    {
        if (IsDisposed) return;

        try
        {
            await _refreshLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (IsDisposed) return;

            StopAllWriters();

            var discovered = await Task.Run(() => _backend.EnumerateMonitors()).ConfigureAwait(false);

            _uiInvoke(() =>
            {
                if (IsDisposed) return;

                var snapshot = discovered as MonitorInfo[] ?? [.. discovered];
                lock (_writers)
                {
                    if (IsDisposed) return;
                    foreach (var monitor in snapshot)
                    {
                        _writers[monitor.Id] = new MonitorWriter(monitor.Id, _backend, _writeThrottle);
                    }
                }

                _monitorsSnapshot = snapshot;

                Monitors.Clear();
                foreach (var monitor in snapshot)
                {
                    Monitors.Add(monitor);
                }

                MonitorsRefreshed?.Invoke(this, EventArgs.Empty);
                BrightnessSummaryChanged?.Invoke(this, GetTraySummaryPercent());
            });
        }
        finally
        {
            try
            {
                _refreshLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Ignore if disposed while refresh was unwinding.
            }
        }
    }

    public void SetMonitorBrightness(string monitorId, int brightnessPercent)
    {
        var monitors = _monitorsSnapshot;
        if (IsDisposed || monitors.Length == 0) return;

        int clamped = Math.Clamp(brightnessPercent, 0, 100);

        if (Settings.LinkMonitors)
        {
            foreach (var monitor in monitors)
            {
                ApplyMonitorBrightness(monitor, clamped);
            }
        }
        else
        {
            var target = monitors.FirstOrDefault(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;
            ApplyMonitorBrightness(target, clamped);
        }

        BrightnessSummaryChanged?.Invoke(this, GetTraySummaryPercent());
    }

    public void StepMonitorBrightness(string monitorId, int deltaNotches)
    {
        var monitors = _monitorsSnapshot;
        if (IsDisposed || monitors.Length == 0 || deltaNotches == 0) return;

        var target = monitors.FirstOrDefault(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        int next = Math.Clamp(target.Brightness + (deltaNotches * Settings.ScrollStep), 0, 100);
        SetMonitorBrightness(monitorId, next);
    }

    public int? StepTrayBrightness(int deltaNotches)
    {
        if (IsDisposed || _monitorsSnapshot.Length == 0 || deltaNotches == 0)
        {
            return GetTraySummaryPercent();
        }

        int delta = deltaNotches * Settings.ScrollStep;
        var targets = ResolveTrayTargets();
        if (targets.Count == 0) return null;

        if (targets.Count == 1)
        {
            var single = targets[0];
            ApplyMonitorBrightness(single, Math.Clamp(single.Brightness + delta, 0, 100));
        }
        else if (Settings.LinkMonitors)
        {
            int anchor = (int)Math.Round(targets.Average(m => m.Brightness));
            int synced = Math.Clamp(anchor + delta, 0, 100);
            foreach (var monitor in targets)
            {
                ApplyMonitorBrightness(monitor, synced);
            }
        }
        else
        {
            foreach (var monitor in targets)
            {
                ApplyMonitorBrightness(monitor, Math.Clamp(monitor.Brightness + delta, 0, 100));
            }
        }

        int? summary = GetTraySummaryPercent();
        BrightnessSummaryChanged?.Invoke(this, summary);
        return summary;
    }

    public int? GetTraySummaryPercent()
    {
        var targets = ResolveTrayTargets();
        if (targets.Count == 0) return null;
        if (targets.Count == 1) return targets[0].Brightness;
        return (int)Math.Round(targets.Average(m => m.Brightness));
    }

    public string FormatTrayTooltip()
    {
        var targets = ResolveTrayTargets();
        if (targets.Count == 0) return "DimDock";

        return string.Join(Environment.NewLine, targets.Select(m => $"{m.DisplayName}: {m.Brightness}%"));
    }

    public void SetLinkMonitors(bool linked)
    {
        if (Settings.LinkMonitors == linked) return;
        Settings.LinkMonitors = linked;
        _settingsService.Save();
    }

    public void SetScrollStep(int step)
    {
        Settings.ScrollStep = step;
        Settings.Normalize();
        _settingsService.Save();
    }

    public void SetTrayScrollTarget(string targetId)
    {
        Settings.TrayScrollTarget = string.IsNullOrWhiteSpace(targetId)
            ? AppSettings.AllMonitorsTarget
            : targetId;
        _settingsService.Save();
        BrightnessSummaryChanged?.Invoke(this, GetTraySummaryPercent());
    }

    internal async Task FlushPendingWritesAsync(CancellationToken cancellationToken = default)
    {
        List<MonitorWriter> snapshot;
        lock (_writers)
        {
            snapshot = _writers.Values.ToList();
        }

        foreach (var writer in snapshot)
        {
            await writer.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<MonitorInfo> ResolveTrayTargets()
    {
        var monitors = _monitorsSnapshot;
        if (monitors.Length == 0) return [];

        if (!string.Equals(Settings.TrayScrollTarget, AppSettings.AllMonitorsTarget, StringComparison.OrdinalIgnoreCase))
        {
            var specific = monitors.FirstOrDefault(m =>
                string.Equals(m.Id, Settings.TrayScrollTarget, StringComparison.OrdinalIgnoreCase));
            if (specific is not null)
            {
                return [specific];
            }
        }

        return monitors;
    }

    private void ApplyMonitorBrightness(MonitorInfo monitor, int clampedPercent)
    {
        monitor.Brightness = clampedPercent;
        MonitorWriter? writer;
        lock (_writers)
        {
            _writers.TryGetValue(monitor.Id, out writer);
        }

        writer?.Enqueue(clampedPercent);
    }

    private void StopAllWriters()
    {
        List<MonitorWriter> snapshot;
        lock (_writers)
        {
            snapshot = [.. _writers.Values];
            _writers.Clear();
        }

        foreach (var writer in snapshot)
        {
            writer.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopAllWriters();
        _refreshLock.Dispose();
    }

    private sealed class MonitorWriter : IDisposable
    {
        private readonly string _monitorId;
        private readonly IMonitorBackend _backend;
        private readonly TimeSpan _throttle;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _workerTask;
        private int _pendingValue = -1;
        private int _inFlight;
        private int _disposed;

        public MonitorWriter(string monitorId, IMonitorBackend backend, TimeSpan throttle)
        {
            _monitorId = monitorId;
            _backend = backend;
            _throttle = throttle;
            _workerTask = Task.Run(RunLoopAsync);
        }

        public void Enqueue(int percent)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            Interlocked.Exchange(ref _pendingValue, percent);
            try
            {
                if (_signal.CurrentCount == 0)
                {
                    _signal.Release();
                }
            }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public async Task WaitForIdleAsync(CancellationToken ct)
        {
            while (Volatile.Read(ref _disposed) == 0 &&
                   (Volatile.Read(ref _pendingValue) >= 0 || Volatile.Read(ref _inFlight) > 0) &&
                   !ct.IsCancellationRequested)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }

        private async Task RunLoopAsync()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);

                    while (!ct.IsCancellationRequested)
                    {
                        int next = Interlocked.Exchange(ref _pendingValue, -1);
                        if (next < 0) break;

                        Interlocked.Exchange(ref _inFlight, 1);
                        try
                        {
                            bool ok = _backend.SetBrightness(_monitorId, next);
                            if (!ok && !ct.IsCancellationRequested)
                            {
                                await Task.Delay(35, ct).ConfigureAwait(false);
                                if (!_backend.SetBrightness(_monitorId, next))
                                {
                                    AppLogger.Warn($"DDC brightness write failed after retry for monitor '{_monitorId}' (target {next}%)");
                                }
                            }

                            if (_throttle > TimeSpan.Zero && !ct.IsCancellationRequested)
                            {
                                await Task.Delay(_throttle, ct).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _inFlight, 0);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown or monitor refresh.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _cts.Cancel();
            try { _workerTask.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
            _cts.Dispose();
            _signal.Dispose();
        }
    }
}
