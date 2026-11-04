using NINA.Plugins.MLAstroRPA;

static class Program
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task MustFail(Func<Task> action, string message)
    {
        try { await action(); } catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }
    static async Task Main()
    {
        var safety = new MotionSafety();
        var first = safety.BeginStop();
        var second = safety.BeginStop();
        safety.CompleteStop(first);
        Check(safety.Inhibited && safety.State == "Stopping", "stale completion cleared newer stop");
        safety.CompleteStop(second);
        safety.Fail(first, "late failure");
        var third = safety.BeginStop();
        safety.CompleteStop(third);
        Check(safety.Inhibited && safety.Fault == "late failure", "failure did not persist");
        Check(!safety.ClearAfterReconnect(second) && safety.ClearAfterReconnect(safety.Epoch), "reconnect epoch check failed");
        var recoveryEpoch = safety.Epoch;
        safety.Fail(recoveryEpoch, "failure during recovery");
        Check(!safety.ClearAfterReconnect(recoveryEpoch) && safety.Inhibited, "recovery cleared a newer failure");
        var concurrent = new MotionSafety();
        var epochs = new long[1000];
        Parallel.For(0, epochs.Length, i => epochs[i] = concurrent.BeginStop());
        Check(epochs.Distinct().Count() == epochs.Length, "concurrent epochs were reused");
        Parallel.ForEach(epochs, concurrent.FailFromTest);
        Check(concurrent.Inhibited && concurrent.State == "Faulted", "concurrent failure did not latch");

        var releaseFailure = new PluginHarness { FailRelease = true };
        await MustFail(releaseFailure.StopAsync, "failed release should report stop failure");
        Check(releaseFailure.GeneralStops == 1, "general STOP not attempted after failed release");
        Check(releaseFailure.Aborts == 1 && releaseFailure.RunnerStops == 1 && releaseFailure.BridgeDisabled && releaseFailure.ReleasedSelection, "cleanup did not all run");
        Check(releaseFailure.Faulted, "failed release did not latch fault");
        await MustFail(() => releaseFailure.ManualMoveAsync("az", 1, 30), "fault allowed manual movement");
        await MustFail(() => releaseFailure.SetAutomationAsync(true), "fault allowed automation");

        var stopFailure = new PluginHarness { FailGeneralStop = true };
        await MustFail(stopFailure.StopAsync, "failed STOP should report failure");
        Check(stopFailure.GeneralStops == 1 && stopFailure.Faulted, "failed STOP did not latch");
        await MustFail(() => stopFailure.ManualMoveAsync("az", 1, 30), "failed STOP allowed manual movement");
        await MustFail(() => stopFailure.SetAutomationAsync(true), "failed STOP allowed automation");

        var admission = new PluginHarness();
        admission.SetIdle();
        await admission.HoldAdmissions();
        var pendingMove = admission.ManualMoveAsync("az", 1, 30);
        var pendingAutomation = admission.SetAutomationAsync(true);
        await admission.StopAsync();
        admission.ReleaseAdmissions();
        await MustFail(() => pendingMove, "pre-stop queued manual move was admitted");
        await MustFail(() => pendingAutomation, "pre-stop queued automation was admitted");

        var blockedCancellation = new PluginHarness();
        blockedCancellation.SetIdle();
        using var releaseCallback = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        blockedCancellation.OnManualCancellation(() => { callbackEntered.Set(); releaseCallback.Wait(); });
        try {
            await MustFail(blockedCancellation.StopAsync, "unfinished cancellation should fail stop");
            Check(callbackEntered.IsSet && blockedCancellation.GeneralStops == 1 && blockedCancellation.Faulted,
                "blocked cancellation did not preserve general STOP and latch fault");
        } finally { releaseCallback.Set(); }

        var staleReady = new PluginHarness { SingleReadyFrame = true };
        staleReady.SetIdle();
        await MustFail(staleReady.StopAsync, "one READY frame and silence must not confirm STOP");
        Check(staleReady.Faulted, "stale READY did not latch fault");

        var pendingObserver = new PluginHarness();
        pendingObserver.SetIdle();
        var observer = new TaskCompletionSource();
        pendingObserver.SetManualObserver(observer.Task);
        await MustFail(pendingObserver.RecoverForTest, "unfinished manual observer must block recovery");
        observer.SetResult();
        await WriterChecks.RunAsync();
        Console.WriteLine("Safety checks passed against extracted plugin methods and writer.");
    }
    static void FailFromTest(this MotionSafety safety, long epoch) => safety.Fail(epoch, "failure");
}

