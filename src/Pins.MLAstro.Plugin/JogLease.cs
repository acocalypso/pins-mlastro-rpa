using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.MLAstroRPA;

/// <summary>A browser must keep renewing its lease; expired leases cannot be revived.</summary>
public sealed class JogLease : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Func<string, int, CancellationToken, Task> start;
    private readonly Func<string, int, CancellationToken, Task> refresh;
    private readonly Func<string, int, CancellationToken, Task> stop;
    private readonly Task watchdog;
    private string lease, axis;
    private int direction;
    private long renewed, refreshed;
    public bool Active => Volatile.Read(ref lease) != null;
    public string Error { get; private set; }

    public JogLease(Func<string, int, CancellationToken, Task> start, Func<string, int, CancellationToken, Task> refresh, Func<string, int, CancellationToken, Task> stop)
    {
        this.start = start; this.refresh = refresh; this.stop = stop;
        watchdog = WatchAsync();
    }
    public async Task<string> StartAsync(string axis, int direction)
    {
        await gate.WaitAsync();
        try {
            if (Active) throw new InvalidOperationException("A jog is already active or awaiting stop confirmation.");
            this.axis = axis; this.direction = direction;
            lease = Guid.NewGuid().ToString("N");
            renewed = refreshed = Stopwatch.GetTimestamp();
            Error = null;
            try { await InvokeAsync(start); }
            catch { await StopLockedAsync(); throw; }
            return lease;
        } finally { gate.Release(); }
    }
    public async Task RenewAsync(string id)
    {
        await gate.WaitAsync();
        try {
            if (id == null || id != lease) throw new InvalidOperationException("Jog lease is no longer active.");
            if (Stopwatch.GetElapsedTime(renewed).TotalMilliseconds >= 900) {
                await StopLockedAsync();
                throw new InvalidOperationException("Jog lease expired; press the arrow again.");
            }
            renewed = Stopwatch.GetTimestamp();
        } finally { gate.Release(); }
    }
    public async Task StopAsync(string id = null)
    {
        await gate.WaitAsync();
        try { if (Active && (id == null || id == lease)) await StopLockedAsync(); }
        finally { gate.Release(); }
    }
    private async Task StopLockedAsync()
    {
        // Keep the lease active on a failed stop so subsequent starts remain blocked.
        try { await InvokeAsync(stop); lease = null; }
        catch (Exception ex) { Error = "Stop could not be sent; inspect the RPA connection."; NINA.Core.Utility.Logger.Error(ex); throw; }
    }
    private async Task InvokeAsync(Func<string, int, CancellationToken, Task> operation)
    {
        using var deadline = new CancellationTokenSource(250);
        // The transport honors cancellation, including waiting for its send lock. The outer
        // deadline also prevents a broken transport implementation from trapping Stop forever.
        var pending = operation(axis, direction, deadline.Token);
        try { await pending.WaitAsync(TimeSpan.FromMilliseconds(350)); }
        catch {
            deadline.Cancel();
            _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
    }
    private async Task WatchAsync()
    {
        try {
            while (true) {
                await Task.Delay(100, shutdown.Token);
                await gate.WaitAsync(shutdown.Token);
                try {
                    if (!Active) continue;
                    try {
                        if (Stopwatch.GetElapsedTime(renewed).TotalMilliseconds >= 900) await StopLockedAsync();
                        else if (Stopwatch.GetElapsedTime(refreshed).TotalMilliseconds >= 250) {
                            await InvokeAsync(refresh); refreshed = Stopwatch.GetTimestamp();
                        }
                    } catch (Exception ex) {
                        NINA.Core.Utility.Logger.Error(ex);
                        // Do not keep sending movement after a transport failure.
                        renewed = 0;
                        try { await StopLockedAsync(); } catch { }
                    }
                } finally { gate.Release(); }
            }
        } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel(); await watchdog;
        await StopAsync(); shutdown.Dispose();
    }
}
