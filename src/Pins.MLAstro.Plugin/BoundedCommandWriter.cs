using System;
using System.Threading;
using System.Threading.Tasks;

namespace MLAstroRPA.Services;

/// <summary>Serializes writes for one transport session and permanently fences uncertain writes.</summary>
public sealed class BoundedCommandWriter
{
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> write;
    private readonly Func<bool> isCurrentSession;
    private readonly Action onUncertainWrite;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource closed = new();
    private int inFlight;
    private int faulted;

    public BoundedCommandWriter(Func<ReadOnlyMemory<byte>, CancellationToken, Task> write,
        Func<bool> isCurrentSession, Action onUncertainWrite, TimeSpan? timeout = null)
    {
        this.write = write ?? throw new ArgumentNullException(nameof(write));
        this.isCurrentSession = isCurrentSession ?? throw new ArgumentNullException(nameof(isCurrentSession));
        this.onUncertainWrite = onUncertainWrite ?? throw new ArgumentNullException(nameof(onUncertainWrite));
        this.timeout = timeout ?? TimeSpan.FromMilliseconds(250);
        if (this.timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <summary>Includes dispatched work and asynchronous uncertainty cleanup callbacks.</summary>
    public int InFlight => Volatile.Read(ref inFlight);
    public bool Faulted => Volatile.Read(ref faulted) != 0;

    /// <summary>Fences this session and cancels queued or in-progress operations.</summary>
    public void Cancel()
    {
        Interlocked.Increment(ref inFlight);
        var cancellation = closed.CancelAsync();
        _ = cancellation.ContinueWith(t => { _ = t.Exception; Interlocked.Decrement(ref inFlight); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token, Func<bool> allowed = null, TimeSpan? writeTimeout = null, TimeSpan? queueTimeout = null)
    {
        var budget = writeTimeout ?? timeout;
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(writeTimeout));
        if (queueTimeout.HasValue && queueTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(queueTimeout));
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, closed.Token);
        try { return await SendCoreAsync(bytes, deadline, allowed, budget, queueTimeout ?? budget).ConfigureAwait(false); }
        finally {
            // A driver's cancellation callback can block disposal of linked registrations.
            // Keep that cleanup visible to reconnect checks without trapping the caller.
            Interlocked.Increment(ref inFlight);
            _ = Task.Run(() => { try { deadline.Dispose(); } finally { Interlocked.Decrement(ref inFlight); } });
        }
    }

    private async Task<bool> SendCoreAsync(ReadOnlyMemory<byte> bytes, CancellationTokenSource deadline, Func<bool> allowed, TimeSpan budget, TimeSpan queueBudget)
    {
        deadline.CancelAfter(queueBudget);
        var operationToken = deadline.Token;
        while (!gate.Wait(0))
        {
            if (operationToken.IsCancellationRequested) return false;
            await Task.Delay(10).ConfigureAwait(false);
        }

        var releaseHere = true;
        try
        {
            // STOP can wait for a bounded status write to drain without extending
            // its own I/O deadline. Motion uses its original short total budget.
            if (queueBudget != budget) deadline.CancelAfter(budget);
            if (!CanStart(operationToken, allowed)) return false;
            // This lock makes timeout and dispatch mutually exclusive: if timeout wins,
            // a delayed thread-pool worker is forbidden from starting the write.
            var dispatchLock = new object();
            var abandoned = false;
            var started = false;
            Interlocked.Increment(ref inFlight);
            var io = Task.Run(async () =>
            {
                lock (dispatchLock)
                {
                    if (abandoned || !CanStart(operationToken, allowed)) return false;
                    started = true;
                }
                await write(bytes, operationToken).ConfigureAwait(false);
                return true;
            });
            _ = io.ContinueWith(t =>
            {
                _ = t.Exception;
                Interlocked.Decrement(ref inFlight);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            try
            {
                while (!io.IsCompleted && !operationToken.IsCancellationRequested)
                    await Task.WhenAny(io, Task.Delay(10)).ConfigureAwait(false);
                if (operationToken.IsCancellationRequested) throw new OperationCanceledException();
                var dispatched = await io.ConfigureAwait(false);
                return dispatched && CanStart(operationToken, allowed);
            }
            catch
            {
                bool uncertain;
                lock (dispatchLock) { abandoned = true; uncertain = started; }
                if (uncertain)
                {
                    Interlocked.Exchange(ref faulted, 1);
                    // The gate belongs to this write until the actual operation has ended.
                    releaseHere = false;
                    _ = io.ContinueWith(_ => gate.Release(), CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    Interlocked.Increment(ref inFlight);
                    _ = Task.Run(() =>
                    {
                        try { onUncertainWrite(); }
                        catch { /* The caller still observes the permanently faulted writer. */ }
                        finally { Interlocked.Decrement(ref inFlight); }
                    });
                }
                return false;
            }
        }
        finally { if (releaseHere) gate.Release(); }
    }

    private bool CanStart(CancellationToken token, Func<bool> allowed) =>
        !token.IsCancellationRequested && !Faulted && isCurrentSession() && (allowed == null || allowed());
}

