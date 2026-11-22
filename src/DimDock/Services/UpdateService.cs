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

public sealed class UpdateService : IDisposable
{
    private const string RepoUrl = "https://github.com/ohivan/dimdock";

    private readonly UpdateManager _updateManager;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private UpdateInfo? _pendingUpdate;
    private bool _disposed;

    public UpdateService()
    {
        var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false);
        _updateManager = new UpdateManager(source);
    }

    public bool IsInstalled => _updateManager.IsInstalled;
    public UpdateState State { get; private set; } = UpdateState.Idle;
    public string? AvailableVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    public string CurrentVersion
    {
        get
        {
            if (_updateManager.CurrentVersion is { } installedVer)
            {
                return installedVer.ToString();
            }

            var asmVer = Assembly.GetExecutingAssembly().GetName().Version;
            return asmVer is not null ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}" : "1.0.0";
        }
    }

    public event EventHandler? StateChanged;

    public void StartPeriodicChecks()
    {
        if (!IsInstalled || _disposed) return;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait briefly after startup so initial boot stays instant.
                await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token).ConfigureAwait(false);

                while (!_cts.Token.IsCancellationRequested)
                {
                    await CheckAndDownloadAsync(isManual: false).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromHours(6), _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on exit.
            }
        });
    }

    public async Task CheckAndDownloadAsync(bool isManual)
    {
        if (_disposed) return;
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

        if (!await _checkLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            SetState(UpdateState.Checking);

            var updateInfo = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo is null)
            {
                SetState(isManual ? UpdateState.UpToDate : UpdateState.Idle);
                if (isManual) ScheduleIdleReset();
                return;
            }

            SetState(UpdateState.Downloading);
            await _updateManager.DownloadUpdatesAsync(updateInfo, cancelToken: _cts.Token).ConfigureAwait(false);

            _pendingUpdate = updateInfo;
            SetState(UpdateState.ReadyToRestart);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch
        {
            SetState(isManual ? UpdateState.Error : UpdateState.Idle);
            if (isManual) ScheduleIdleReset();
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public void ApplyPendingUpdateAndRestart()
    {
        if (_pendingUpdate is null || !IsInstalled) return;
        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }

    public void ApplyPendingUpdateOnExit()
    {
        if (_pendingUpdate is null || !IsInstalled) return;
        try
        {
            _updateManager.WaitExitThenApplyUpdates(_pendingUpdate, silent: true, restart: false);
        }
        catch
        {
            // Never block shutdown on updater error.
        }
    }

    private void ScheduleIdleReset()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), _cts.Token).ConfigureAwait(false);
                if (_pendingUpdate is null && State is UpdateState.UpToDate or UpdateState.Error)
                {
                    SetState(UpdateState.Idle);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void SetState(UpdateState next)
    {
        if (State == next) return;
        State = next;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _checkLock.Dispose();
    }
}
