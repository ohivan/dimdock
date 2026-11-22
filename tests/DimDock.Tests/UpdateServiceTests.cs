using System.IO;
using DimDock.Services;
using Xunit;

namespace DimDock.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckAndDownloadAsync_UpdateAvailable_TransitionsToReadyToRestart()
    {
        var backend = new FakeUpdateBackend
        {
            IsInstalled = true,
            CurrentVersion = "1.0.0",
            AvailableUpdate = new PendingUpdateHandle("1.1.0", null)
        };

        var states = new List<UpdateState>();
        using var service = new UpdateService(backend, TimeSpan.Zero);
        service.StateChanged += (_, _) => states.Add(service.State);

        await service.CheckAndDownloadAsync(isManual: true);

        Assert.Equal(UpdateState.ReadyToRestart, service.State);
        Assert.Equal("1.1.0", service.AvailableVersion);
        Assert.Equal([UpdateState.Checking, UpdateState.Downloading, UpdateState.ReadyToRestart], states);
        Assert.Equal(1, backend.DownloadCalls);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_NoUpdateManual_ReportsUpToDateThenResetsToIdle()
    {
        var backend = new FakeUpdateBackend { IsInstalled = true, CurrentVersion = "1.0.0" };
        using var service = new UpdateService(backend, TimeSpan.FromMilliseconds(20));

        await service.CheckAndDownloadAsync(isManual: true);

        Assert.Equal(UpdateState.UpToDate, service.State);
        await WaitUntilAsync(() => service.State == UpdateState.Idle);
        Assert.Equal(UpdateState.Idle, service.State);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_NotInstalledManual_ReportsUpToDateWithoutNetworkCall()
    {
        var backend = new FakeUpdateBackend { IsInstalled = false };
        using var service = new UpdateService(backend, TimeSpan.FromMilliseconds(20));

        await service.CheckAndDownloadAsync(isManual: true);

        Assert.Equal(UpdateState.UpToDate, service.State);
        Assert.Equal(0, backend.CheckCalls);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_BackendFailureManual_ReportsErrorThenResets()
    {
        var backend = new FakeUpdateBackend
        {
            IsInstalled = true,
            ThrowOnCheck = new InvalidOperationException("network down")
        };

        using var service = new UpdateService(backend, TimeSpan.FromMilliseconds(20));

        await service.CheckAndDownloadAsync(isManual: true);

        Assert.Equal(UpdateState.Error, service.State);
        await WaitUntilAsync(() => service.State == UpdateState.Idle);
        Assert.Equal(UpdateState.Idle, service.State);
    }

    [Fact]
    public async Task CheckAndDownloadAsync_ConcurrentCalls_DeduplicatesCheck()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeUpdateBackend
        {
            IsInstalled = true,
            CheckGate = gate.Task
        };

        using var service = new UpdateService(backend, TimeSpan.Zero);

        var first = service.CheckAndDownloadAsync(isManual: true);
        var second = service.CheckAndDownloadAsync(isManual: true);

        gate.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, backend.CheckCalls);
    }

    [Fact]
    public async Task Dispose_WhileDownloadInFlight_CancelsCleanlyWithoutThrowing()
    {
        var downloadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeUpdateBackend
        {
            IsInstalled = true,
            AvailableUpdate = new PendingUpdateHandle("1.2.0", null),
            OnDownloadStarted = () => downloadStarted.TrySetResult(true),
            WaitForCancellationOnDownload = true
        };

        var service = new UpdateService(backend, TimeSpan.Zero);
        var checkTask = service.CheckAndDownloadAsync(isManual: true);

        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.Dispose();
        await checkTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ApplyPendingUpdateOnExit_BackendThrows_DoesNotPropagate()
    {
        var backend = new FakeUpdateBackend
        {
            IsInstalled = true,
            AvailableUpdate = new PendingUpdateHandle("1.1.0", null),
            ThrowOnApplyExit = new IOException("locked")
        };

        using var service = new UpdateService(backend, TimeSpan.Zero);
        await service.CheckAndDownloadAsync(isManual: false);

        var ex = Record.Exception(() => service.ApplyPendingUpdateOnExit());
        Assert.Null(ex);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 1500)
    {
        int waited = 0;
        while (!predicate() && waited < timeoutMs)
        {
            await Task.Delay(15);
            waited += 15;
        }
    }

    private sealed class FakeUpdateBackend : IUpdateBackend
    {
        private int _checkCalls;
        private int _downloadCalls;

        public bool IsInstalled { get; set; }
        public string? CurrentVersion { get; set; }
        public PendingUpdateHandle? AvailableUpdate { get; set; }
        public Exception? ThrowOnCheck { get; set; }
        public Exception? ThrowOnApplyExit { get; set; }
        public Task? CheckGate { get; set; }
        public Action? OnDownloadStarted { get; set; }
        public bool WaitForCancellationOnDownload { get; set; }

        public int CheckCalls => Volatile.Read(ref _checkCalls);
        public int DownloadCalls => Volatile.Read(ref _downloadCalls);

        public async Task<PendingUpdateHandle?> CheckForUpdatesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _checkCalls);
            if (CheckGate is not null)
            {
                await CheckGate.WaitAsync(ct);
            }

            if (ThrowOnCheck is not null) throw ThrowOnCheck;
            return AvailableUpdate;
        }

        public async Task DownloadUpdatesAsync(PendingUpdateHandle update, CancellationToken ct)
        {
            Interlocked.Increment(ref _downloadCalls);
            OnDownloadStarted?.Invoke();
            if (WaitForCancellationOnDownload)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        }

        public void ApplyUpdatesAndRestart(PendingUpdateHandle update) { }

        public void WaitExitThenApplyUpdates(PendingUpdateHandle update)
        {
            if (ThrowOnApplyExit is not null) throw ThrowOnApplyExit;
        }
    }
}
