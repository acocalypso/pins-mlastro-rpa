using NINA.Plugins.MLAstroRPA;

partial class PluginHarness
{
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private int stopGeneration;
    private int activeStops;
    private readonly MotionSafety motionSafety = new();
    private readonly FakeSerial Serial = new();
    private readonly FakeRunner Runner = new();
    private readonly FakeBridge Bridge = new();
    private readonly FakeSettings Settings = new();
    private readonly FakeHardware hardware;
    private readonly object selectionGate = new();
    private readonly object motionGate = new();
    private CancellationTokenSource pendingSession, manualCancellation;
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> manualCancellations = new();
    private bool manualMoveActive;
    private string manualMoveStatus;
    private Task manualTask = Task.CompletedTask;
    private DateTime connectedAt = DateTime.UtcNow;
    private bool IsHardwareConnected => true;
    public PluginHarness() { hardware = new FakeHardware(Serial); }
    public bool FailGeneralStop { set => hardware.Fail = value; }
    public bool SingleReadyFrame { set => hardware.SingleReadyFrame = value; }
    public int GeneralStops => hardware.Stops;
    public int Aborts => Runner.Aborts;
    public int RunnerStops => Runner.Stops;
    public bool BridgeDisabled => !Bridge.Enabled;
    public bool ReleasedSelection { get; private set; }
    public bool Faulted => motionSafety.Inhibited && motionSafety.State == "Faulted";
    public void SetIdle() { Runner.IsSessionRunning = false; }
    public Task HoldAdmissions() => controlGate.WaitAsync();
    public void ReleaseAdmissions() => controlGate.Release();
    public void OnManualCancellation(Action callback) { manualCancellation = new(); manualCancellation.Token.Register(callback); }
    public void SetManualObserver(Task task) => manualTask = task;
    public Task RecoverForTest() { hardware.StartTelemetry(); return RecoverConnectionAsync(stopGeneration, motionSafety.Epoch, CancellationToken.None); }
    private void RecordEvent(DiagnosticEventCode code) { }
    private void CancelManagement() { }
    private void ReleaseTppaSelection() => ReleasedSelection = true;
    private void SelectTppaForOneRun() { }
    private Task ExpirePendingStartAsync(CancellationTokenSource token) => Task.CompletedTask;
    private Task ObserveManualMoveAsync(string axis, int direction, int arcseconds, CancellationToken token) => Task.CompletedTask;
}

class FakeSerial
{
    public bool MotionWritesBlocked { get; private set; }
    public bool WritesQuiescent => true;
    public object LastTelemetry { get; set; } = new();
    public DateTime LastTelemetryAt { get; set; } = DateTime.UtcNow;
    public bool IsDeviceIdle => true;
    public void CancelPendingMotion() => MotionWritesBlocked = true;
    public void AllowMotion() => MotionWritesBlocked = false;
}
class FakeHardware(FakeSerial serial)
{
    public bool Fail { get; set; }
    public bool SingleReadyFrame { get; set; }
    public int Stops { get; private set; }
    public void AbortPendingMove() { }
    public void StartTelemetry()
    {
        _ = Task.Run(async () => {
            for (var i = 0; i < (SingleReadyFrame ? 1 : 12); i++) {
                await Task.Delay(100);
                serial.LastTelemetry = new();
                serial.LastTelemetryAt = DateTime.UtcNow;
            }
        });
    }
    public Task<bool> StopAsync()
    {
        Stops++;
        if (!Fail) StartTelemetry();
        return Task.FromResult(!Fail);
    }
}
enum BridgeReason { UserStop, BrokerDisabled, ControllerFault }
class FakeRunner
{
    public bool IsSessionRunning { get; set; } = true;
    public int Aborts { get; private set; }
    public int Stops { get; private set; }
    public Task AbortSessionAsync(BridgeReason reason) { Aborts++; return Task.CompletedTask; }
    public void Stop() => Stops++;
    public void Start() { }
}
class FakeBridge { public bool Enabled { get; set; } = true; }
class FakeSettings { public bool TppaBrokerEnabled { get; set; } }

enum DiagnosticEventCode { Connected, Stopped, Fault }
