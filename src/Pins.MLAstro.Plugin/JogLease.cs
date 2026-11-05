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
    private readonly object callbackLock = new();
    private CancellationTokenSource motionFence = new();
    private bool fenced;
    private int fencing;
    private int callbacks;
    private long generation;
    private string lease, axis;
    private int direction;
    private long renewed, refreshed;
    public bool Active => Volatile.Read(ref lease) != null;
    public string Error { get; private set; }
    public bool IsQuiescent { get { lock (callbackLock) return callbacks == 0 && fencing == 0; } }
    public event Action<string> Faulted;

    // Fence synchronously, before waiting for the lease gate or attempting a release.
    public void Fence()
    {
        CancellationTokenSource fence;
        lock (callbackLock) { fenced = true; generation++; fencing++; fence = motionFence; }
        try {
            _ = fence.CancelAsync().ContinueWith(t => {
                if (t.IsFaulted) { try { Faulted?.Invoke(t.Exception?.Message); } catch { } }
                lock (callbackLock) fencing--;
            }, TaskScheduler.Default);
        } catch {
            lock (callbackLock) fencing--;
            throw;
        }
    }

    public void Unfence()
    {
        lock (callbackLock) {
            if (fencing != 0 || callbacks != 0 || Active) throw new InvalidOperationException("Jog callbacks or lease are still active.");
            motionFence.Dispose();
            motionFence = new CancellationTokenSource();
            fenced = false;
        }
    }

    // Caller must first establish a new connection with fresh READY telemetry.
    public void ResetAfterReconnect()
    {
        if (!gate.Wait(400)) throw new InvalidOperationException("A jog operation is still active.");
        try {
        lock (callbackLock) {
            if (fencing != 0 || callbacks != 0) throw new InvalidOperationException("Jog callbacks are still active.");
            lease = null;
            motionFence.Dispose();
            motionFence = new CancellationTokenSource();
            fenced = false;
            Error = null;
        }
        } finally { gate.Release(); }
    }

    public JogLease(Func<string, int, CancellationToken, Task> start, Func<string, int, CancellationToken, Task> refresh, Func<string, int, CancellationToken, Task> stop)
    {
        this.start = start; this.refresh = refresh; this.stop = stop;
        watchdog = WatchAsync();
    }
    public async Task<string> StartAsync(string axis, int direction)
    {
        long requestedGeneration;
        lock (callbackLock) { if (fenced) throw new InvalidOperationException("Jog is inhibited."); requestedGeneration = generation; }
        await gate.WaitAsync();
        try {
            lock (callbackLock) if (fenced || requestedGeneration != generation) throw new InvalidOperationException("Jog is inhibited.");
            if (Active || !IsQuiescent) throw new InvalidOperationException("A jog is already active or awaiting stop confirmation.");
            this.axis = axis; this.direction = direction;
            lease = Guid.NewGuid().ToString("N");
            renewed = refreshed = Stopwatch.GetTimestamp();
            Error = null;
            try { await InvokeAsync(start, true); }
            catch { try { await StopLockedAsync(); } catch { } throw; }
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
        try {
            await InvokeAsync(stop, false);
            if (!IsQuiescent) throw new InvalidOperationException("A previous jog callback is still running.");
            lease = null;
        }
        catch (Exception ex) { Error = "Stop could not be sent; inspect the RPA connection."; NINA.Core.Utility.Logger.Error(ex); throw; }
    }
    private async Task InvokeAsync(Func<string, int, CancellationToken, Task> operation, bool motion)
    {
        CancellationTokenSource deadline;
        string capturedAxis = axis;
        int capturedDirection = direction;
        lock (callbackLock) {
            if (motion && fenced) throw new InvalidOperationException("Jog is inhibited.");
            deadline = motion ? CancellationTokenSource.CreateLinkedTokenSource(motionFence.Token) : new CancellationTokenSource();
            callbacks++;
        }
        // Run even synchronous callback initiation away from the caller. The callback stays
        // counted until it actually returns, even when the caller's deadline has elapsed.
        var pending = Task.Run(async () => {
            try {
                deadline.Token.ThrowIfCancellationRequested();
                await operation(capturedAxis, capturedDirection, deadline.Token).ConfigureAwait(false);
            } finally {
                lock (callbackLock) callbacks--;
            }
        });
        try {
            await pending.WaitAsync(TimeSpan.FromMilliseconds(350)).ConfigureAwait(false);
            if (!deadline.IsCancellationRequested) deadline.Dispose();
        }
        catch (Exception ex) {
            lock (callbackLock) fencing++;
            try {
                _ = deadline.CancelAsync().ContinueWith(t => {
                    if (t.IsFaulted) { try { Faulted?.Invoke(t.Exception?.Message); } catch { } }
                    lock (callbackLock) fencing--;
                }, TaskScheduler.Default);
            } catch { lock (callbackLock) fencing--; }
            try { Faulted?.Invoke(ex.Message); } catch { }
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
                    // A global stop owns release/recovery once fenced. Repeated
                    // watchdog retries must not invalidate every reconnect attempt.
                    lock (callbackLock) if (fenced) continue;
                    try {
                        if (Stopwatch.GetElapsedTime(renewed).TotalMilliseconds >= 900) await StopLockedAsync();
                        else if (Stopwatch.GetElapsedTime(refreshed).TotalMilliseconds >= 250) {
                            await InvokeAsync(refresh, true); refreshed = Stopwatch.GetTimestamp();
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
