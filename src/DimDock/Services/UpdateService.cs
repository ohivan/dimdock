using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace DimDock.Services;

public enum UpdateState
{
    Idle,
    Checking,
    Downloading,
    ReadyToRestart,
    UpToDate,
    Error
}

internal interface IUpdateBackend
{
    bool IsInstalled { get; }
    string? CurrentVersion { get; }
    Task<PendingUpdateHandle?> CheckForUpdatesAsync(CancellationToken ct);
    Task DownloadUpdatesAsync(PendingUpdateHandle update, CancellationToken ct);
    void ApplyUpdatesAndRestart(PendingUpdateHandle update);
    void WaitExitThenApplyUpdates(PendingUpdateHandle update);
}

internal sealed record PendingUpdateHandle(string Version, UpdateInfo? NativeInfo);

public sealed class UpdateService : IDisposable
{
    private const string RepoUrl = "https://github.com/ohivan/dimdock";

    private readonly IUpdateBackend _backend;
    private readonly TimeSpan _idleResetDelay;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private volatile PendingUpdateHandle? _pendingUpdate;
    private int _state = (int)UpdateState.Idle;
    private int _disposed;

    public UpdateService()
        : this(new VelopackUpdateBackend(RepoUrl), TimeSpan.FromSeconds(4))
    {
    }

    internal UpdateService(IUpdateBackend backend, TimeSpan? idleResetDelay = null)
    {
        _backend = backend;
        _idleResetDelay = idleResetDelay ?? TimeSpan.FromSeconds(4);
    }

    public bool IsInstalled => _backend.IsInstalled;
    public UpdateState State => (UpdateState)Volatile.Read(ref _state);
    public string? AvailableVersion => _pendingUpdate?.Version;

    public string CurrentVersion
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_backend.CurrentVersion))
            {
                return _backend.CurrentVersion;
            }

            var asmVer = Assembly.GetExecutingAssembly().GetName().Version;
            return asmVer is not null ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "1.0.0";
        }
    }

    public event EventHandler? StateChanged;

    public void StartPeriodicChecks()
    {
        if (!IsInstalled || IsDisposed) return;

        CancellationToken ct;
        try
        {
            ct = _cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait briefly after startup so initial boot stays instant.
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    await CheckAndDownloadAsync(isManual: false).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromHours(6), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on exit.
            }
            catch (ObjectDisposedException)
            {
                // Expected if disposed concurrently.
            }
        });
    }

    public async Task CheckAndDownloadAsync(bool isManual)
    {
        if (IsDisposed) return;
        if (_pendingUpdate is not null)
        {
            SetState(UpdateState.ReadyToRestart);
            return;
        }

        if (!IsInstalled)
        {
            if (isManual)
            {
                SetState(UpdateState.UpToDate);
                ScheduleIdleReset();
            }
            return;
        }

        CancellationToken ct;
        try
        {
            ct = _cts.Token;
            if (!await _checkLock.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (IsDisposed) return;

            SetState(UpdateState.Checking);

            var updateInfo = await _backend.CheckForUpdatesAsync(ct).ConfigureAwait(false);
            if (updateInfo is null)
            {
                SetState(isManual ? UpdateState.UpToDate : UpdateState.Idle);
                if (isManual) ScheduleIdleReset();
                return;
            }

            SetState(UpdateState.Downloading);
            await _backend.DownloadUpdatesAsync(updateInfo, ct).ConfigureAwait(false);

            _pendingUpdate = updateInfo;
            AppLogger.Info($"Downloaded DimDock update v{updateInfo.Version}");
            SetState(UpdateState.ReadyToRestart);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Update check/download failed", ex);
            SetState(isManual ? UpdateState.Error : UpdateState.Idle);
            if (isManual) ScheduleIdleReset();
        }
        finally
        {
            try
            {
                _checkLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Ignore if disposed while unwinding.
            }
        }
    }

    public void ApplyPendingUpdateAndRestart()
    {
        var pending = _pendingUpdate;
        if (pending is null || !IsInstalled) return;

        try
        {
            _backend.ApplyUpdatesAndRestart(pending);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to apply update and restart", ex);
        }
    }

    public void ApplyPendingUpdateOnExit()
    {
        var pending = _pendingUpdate;
        if (pending is null || !IsInstalled) return;

        try
        {
            _backend.WaitExitThenApplyUpdates(pending);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Failed to stage pending update on exit", ex);
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void ScheduleIdleReset()
    {
        if (IsDisposed) return;

        CancellationToken ct;
        try
        {
            ct = _cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (_idleResetDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_idleResetDelay, ct).ConfigureAwait(false);
                }

                if (!IsDisposed && _pendingUpdate is null && State is UpdateState.UpToDate or UpdateState.Error)
                {
                    SetState(UpdateState.Idle);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });
    }

    private void SetState(UpdateState next)
    {
        int previous = Interlocked.Exchange(ref _state, (int)next);
        if (previous == (int)next) return;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
        _checkLock.Dispose();
    }

    private sealed class VelopackUpdateBackend : IUpdateBackend
    {
        private readonly UpdateManager _manager;

        public VelopackUpdateBackend(string repoUrl)
        {
            var source = new GithubSource(repoUrl, accessToken: null, prerelease: false);
            _manager = new UpdateManager(source);
        }

        public bool IsInstalled => _manager.IsInstalled;
        public string? CurrentVersion => _manager.CurrentVersion?.ToString();

        public async Task<PendingUpdateHandle?> CheckForUpdatesAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return info is null
                ? null
                : new PendingUpdateHandle(info.TargetFullRelease.Version.ToString(), info);
        }

        public Task DownloadUpdatesAsync(PendingUpdateHandle update, CancellationToken ct)
        {
            if (update.NativeInfo is null) return Task.CompletedTask;
            return _manager.DownloadUpdatesAsync(update.NativeInfo, cancelToken: ct);
        }

        public void ApplyUpdatesAndRestart(PendingUpdateHandle update)
        {
            if (update.NativeInfo is not null)
            {
                _manager.ApplyUpdatesAndRestart(update.NativeInfo);
            }
        }

        public void WaitExitThenApplyUpdates(PendingUpdateHandle update)
        {
            if (update.NativeInfo is not null)
            {
                _manager.WaitExitThenApplyUpdates(update.NativeInfo, silent: true, restart: false);
            }
        }
    }
}
