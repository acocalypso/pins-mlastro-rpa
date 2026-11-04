using System.Diagnostics;
using MLAstroRPA.Services;

static class WriterChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition, string message)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Check(condition(), message);
    }

    public static async Task RunAsync()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var aborts = 0;
        var writer = new BoundedCommandWriter((bytes, token) => {
            Interlocked.Increment(ref calls); entered.Set(); release.Wait(); return Task.CompletedTask;
        }, () => true, () => Interlocked.Increment(ref aborts), TimeSpan.FromMilliseconds(250));
        try {
            var watch = Stopwatch.StartNew();
            var first = writer.SendAsync(new byte[] { 1 }, CancellationToken.None);
            Check(entered.Wait(TimeSpan.FromSeconds(2)), "stalled writer did not start");
            using var cancelQueued = new CancellationTokenSource();
            var queued = writer.SendAsync(new byte[] { 2 }, cancelQueued.Token);
            cancelQueued.Cancel();
            Check(!await queued, "canceled queued write succeeded");
            Check(!await first && watch.Elapsed < TimeSpan.FromSeconds(2), "stalled write was not bounded");
            Check(writer.Faulted && writer.InFlight > 0, "uncertain write was not retained and faulted");
            Check(!await writer.SendAsync(new byte[] { 3 }, CancellationToken.None), "faulted writer admitted a new command");
            Check(calls == 1, "queued or post-fault command reached the transport");
        } finally { release.Set(); }
        await Until(() => writer.InFlight == 0 && Volatile.Read(ref aborts) == 1, "writer did not become quiescent");
        Check(calls == 1, "late command reached the transport");

        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new BoundedCommandWriter((bytes, token) => {
            token.Register(() => { callbackEntered.Set(); releaseCallback.Wait(); });
            registered.TrySetResult();
            return pending.Task;
        }, () => true, () => { }, TimeSpan.FromSeconds(5));
        try {
            var send = blocked.SendAsync(new byte[] { 4 }, CancellationToken.None);
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var watch = Stopwatch.StartNew();
            blocked.Cancel();
            Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Cancel blocked on a transport callback");
            Check(callbackEntered.Wait(TimeSpan.FromSeconds(2)), "cancellation callback did not run");
            Check(!await send.WaitAsync(TimeSpan.FromSeconds(2)), "canceled in-flight write succeeded");
            Check(blocked.Faulted && blocked.InFlight > 0, "blocked cancellation did not prevent reconnect");
            Check(!await blocked.SendAsync(new byte[] { 5 }, CancellationToken.None), "canceled writer admitted a command");
        } finally { releaseCallback.Set(); pending.TrySetResult(); }
        await Until(() => blocked.InFlight == 0, "blocked cancellation did not eventually drain");
    }
}
