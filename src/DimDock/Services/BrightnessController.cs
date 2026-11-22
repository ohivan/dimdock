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
    private bool _disposed;

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
        if (_disposed) return;

        await _refreshLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;

            StopAllWriters();

            var discovered = await Task.Run(() => _backend.EnumerateMonitors()).ConfigureAwait(false);

            _uiInvoke(() =>
            {
                Monitors.Clear();
                foreach (var monitor in discovered)
                {
                    Monitors.Add(monitor);
                    _writers[monitor.Id] = new MonitorWriter(monitor.Id, _backend, _writeThrottle);
                }

                MonitorsRefreshed?.Invoke(this, EventArgs.Empty);
                BrightnessSummaryChanged?.Invoke(this, GetTraySummaryPercent());
            });
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void SetMonitorBrightness(string monitorId, int brightnessPercent)
    {
        if (_disposed || Monitors.Count == 0) return;

        int clamped = Math.Clamp(brightnessPercent, 0, 100);

        if (Settings.LinkMonitors)
        {
            foreach (var monitor in Monitors)
            {
                ApplyMonitorBrightness(monitor, clamped);
            }
        }
        else
        {
            var target = Monitors.FirstOrDefault(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase));
            if (target is null) return;
            ApplyMonitorBrightness(target, clamped);
        }

        BrightnessSummaryChanged?.Invoke(this, GetTraySummaryPercent());
    }

    public void StepMonitorBrightness(string monitorId, int deltaNotches)
    {
        if (_disposed || Monitors.Count == 0 || deltaNotches == 0) return;

        var target = Monitors.FirstOrDefault(m => string.Equals(m.Id, monitorId, StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        int next = Math.Clamp(target.Brightness + (deltaNotches * Settings.ScrollStep), 0, 100);
        SetMonitorBrightness(monitorId, next);
    }

    public int? StepTrayBrightness(int deltaNotches)
    {
        if (_disposed || Monitors.Count == 0 || deltaNotches == 0)
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
        if (Monitors.Count == 0) return [];

        if (!string.Equals(Settings.TrayScrollTarget, AppSettings.AllMonitorsTarget, StringComparison.OrdinalIgnoreCase))
        {
            var specific = Monitors.FirstOrDefault(m =>
                string.Equals(m.Id, Settings.TrayScrollTarget, StringComparison.OrdinalIgnoreCase));
            if (specific is not null)
            {
                return [specific];
            }
        }

        return Monitors;
    }

    private void ApplyMonitorBrightness(MonitorInfo monitor, int clampedPercent)
    {
        monitor.Brightness = clampedPercent;
        if (_writers.TryGetValue(monitor.Id, out var writer))
        {
            writer.Enqueue(clampedPercent);
        }
    }

    private void StopAllWriters()
    {
        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }
        _writers.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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

        public MonitorWriter(string monitorId, IMonitorBackend backend, TimeSpan throttle)
        {
            _monitorId = monitorId;
            _backend = backend;
            _throttle = throttle;
            _workerTask = Task.Run(RunLoopAsync);
        }

        public void Enqueue(int percent)
        {
            Interlocked.Exchange(ref _pendingValue, percent);
            if (_signal.CurrentCount == 0)
            {
                try { _signal.Release(); }
                catch (SemaphoreFullException) { }
                catch (ObjectDisposedException) { }
            }
        }

        public async Task WaitForIdleAsync(CancellationToken ct)
        {
            while ((Volatile.Read(ref _pendingValue) >= 0 || Volatile.Read(ref _inFlight) > 0) && !ct.IsCancellationRequested)
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
                                _backend.SetBrightness(_monitorId, next);
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
            _cts.Cancel();
            try { _workerTask.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
            _cts.Dispose();
            _signal.Dispose();
        }
    }
}
