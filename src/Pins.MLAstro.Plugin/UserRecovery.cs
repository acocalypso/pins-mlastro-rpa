using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MLAstroRPA.Broker;
using MLAstroRPA.Settings;

namespace NINA.Plugins.MLAstroRPA;

public sealed record CorrectionOptions(string AxisMode = null, bool? OvershootEnabled = null,
    bool? OvershootUpEnabled = null, bool? OvershootDownEnabled = null, double? OvershootUpArcMin = null,
    double? OvershootDownArcMin = null, bool? AutoChangeDirection = null, bool? ReverseAzimuth = null, bool? ReverseAltitude = null);

public sealed partial class MLAstroPlugin
{
    private WatchdogResult watchdogState;
    private CancellationTokenSource managementCancellation;
    private readonly object diagnosticsGate = new();
    private readonly Queue<DiagnosticEvent> diagnosticEvents = new();
    private long diagnosticLastTelemetry, diagnosticLongestGap;

    private void RecordEvent(DiagnosticEventCode code)
    {
        lock (diagnosticsGate) {
            diagnosticEvents.Enqueue(new(DateTimeOffset.UtcNow, code));
            while (diagnosticEvents.Count > 50) diagnosticEvents.Dequeue();
        }
    }
    private void InitializeDiagnostics()
    {
        RecordEvent(DiagnosticEventCode.Started);
        Serial.TelemetryDataReceived += (_, _) => {
            var now = Stopwatch.GetTimestamp();
            var old = Interlocked.Exchange(ref diagnosticLastTelemetry, now);
            if (old == 0) return;
            var gap = now - old;
            long longest;
            do { longest = Volatile.Read(ref diagnosticLongestGap); }
            while (gap > longest && Interlocked.CompareExchange(ref diagnosticLongestGap, gap, longest) != longest);
        };
    }
    private void RequireManagementIdle()
    {
        if (manualMoveActive || hardware.IsMoving || Runner.IsSessionRunning || Settings.TppaBrokerEnabled || Volatile.Read(ref activeStops) != 0)
            throw new InvalidOperationException("Stop alignment or movement before changing this setting or reconnecting.");
    }
    public async Task<object> ReconnectAsync(CancellationToken token)
    {
        bool wifi;
        string address;
        int generation;
        await controlGate.WaitAsync(token);
        try {
            RequireManagementIdle();
            if (Serial.IsConnected && !Serial.IsDeviceIdle)
                throw new InvalidOperationException("The connected RPA is not reporting fresh READY. Use Disconnect to attempt Stop and close the old link, then Connect to recover.");
            wifi = Settings.TransportMode == MlastroTransportMode.Wireless;
            address = wifi ? Settings.MlaHost : string.IsNullOrWhiteSpace(Settings.VerifiedUsbPath) ? "auto" : Settings.VerifiedUsbPath;
            // Invalidate queued admissions, then close only this idle connection. No motion is replayed.
            generation = Interlocked.Increment(ref stopGeneration);
            Serial.CancelPendingMotion();
            try { Wireless.Disconnect(); } finally { Serial.Disconnect(); }
            RecordEvent(DiagnosticEventCode.Disconnected);
        } finally { controlGate.Release(); }
        if (!await ConnectCoreAsync(wifi, address, token, generation)) throw new InvalidOperationException("Reconnection was not confirmed. Check power and the selected connection, then retry.");
        return GetStatus();
    }
    public async Task<object> GetWatchdogAsync(CancellationToken token)
    {
        await controlGate.WaitAsync(token);
        try {
            if (!Wireless.IsConnected) return new { supported = false, enabled = (bool?)null, code = "wifi_required" };
            var generation = Volatile.Read(ref stopGeneration);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (motionGate) managementCancellation = operation;
            try {
                var result = await new CommunicationWatchdog(Wireless.ConfiguredAddress, Settings.MlaPort).ReadAsync(operation.Token);
                lock (motionGate) {
                    if (generation != Volatile.Read(ref stopGeneration)) throw new OperationCanceledException("Watchdog read cancelled by Stop.");
                    watchdogState = result;
                }
                RecordEvent(DiagnosticEventCode.WatchdogRead);
                return new { supported = result.Supported, enabled = result.Enabled, code = result.Code };
            } finally { lock (motionGate) if (ReferenceEquals(managementCancellation, operation)) managementCancellation = null; }
        } finally { controlGate.Release(); }
    }
    public async Task<object> ConfigureWatchdogAsync(bool enabled, bool confirmed, CancellationToken token)
    {
        if (!enabled && !confirmed) throw new ArgumentException("Confirm that disabling the communication watchdog removes automatic stopping on lost host heartbeats.");
        await controlGate.WaitAsync(token);
        try {
            RequireManagementIdle();
            if (!Wireless.IsConnected || !IsHardwareConnected || !Serial.IsDeviceIdle) throw new InvalidOperationException("Connect to this RPA over Wi-Fi and wait for READY before changing its communication watchdog. You can switch back to USB afterward.");
            var generation = Volatile.Read(ref stopGeneration);
            var address = Wireless.ConfiguredAddress;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (motionGate) managementCancellation = operation;
            bool Authorized() {
                lock (motionGate) return generation == Volatile.Read(ref stopGeneration) && Wireless.IsConnected && Wireless.ConfiguredAddress == address &&
                    Serial.IsDeviceIdle && !hardware.IsMoving && !manualMoveActive && !Runner.IsSessionRunning && !Settings.TppaBrokerEnabled;
            }
            WatchdogResult result;
            try { result = await new CommunicationWatchdog(address, Settings.MlaPort).SetAsync(enabled, operation.Token, Authorized); }
            finally { lock (motionGate) if (ReferenceEquals(managementCancellation, operation)) managementCancellation = null; }
            lock (motionGate) watchdogState = generation == Volatile.Read(ref stopGeneration) && result.Code == "ok" ? result : null;
            if (result.Code != "ok") throw new InvalidOperationException("Communication watchdog change could not be verified. Its state is unknown; check the RPA network connection and read it again.");
            RecordEvent(DiagnosticEventCode.WatchdogUpdated);
            return new { supported = result.Supported, enabled = result.Enabled, code = result.Code };
        } finally { controlGate.Release(); }
    }
    private void CancelManagement()
    {
        CancellationTokenSource operation;
        lock (motionGate) operation = managementCancellation;
        try { if (operation != null) manualCancellations.Add(operation.CancelAsync()); }
        catch (ObjectDisposedException) { }
    }
    private CorrectionOptions ValidateOptions(CorrectionOptions value)
    {
        if (value == null) return null;
        if (value.AxisMode != null && (!Enum.TryParse<BridgeAxisMode>(value.AxisMode, true, out var mode) || !Enum.IsDefined(mode)))
            throw new ArgumentException("Choose Both or Auto for the correction axes.");
        foreach (var amount in new[] { value.OvershootUpArcMin, value.OvershootDownArcMin })
            if (amount.HasValue && (!double.IsFinite(amount.Value) || amount < 0)) throw new ArgumentException("Overshoot amounts must be finite and nonnegative.");
        if ((value.OvershootUpEnabled ?? Settings.CorrectionOvershootUpEnabled) && (value.OvershootDownEnabled ?? Settings.CorrectionOvershootDownEnabled))
            throw new ArgumentException("Select one altitude overshoot direction, or neither.");
        if ((value.AutoChangeDirection ?? Settings.CorrectionAutoChangeDirection) &&
            ((value.ReverseAzimuth.HasValue && value.ReverseAzimuth != Settings.SoftwareReverseAzimuth) ||
             (value.ReverseAltitude.HasValue && value.ReverseAltitude != Settings.SoftwareReverseAltitude)))
            throw new ArgumentException("Disable automatic direction changes before changing the software reversal settings.");
        return value;
    }
    private void ApplyOptions(CorrectionOptions value)
    {
        if (value == null) return;
        if (value.AxisMode != null) Settings.CorrectionAxisMode = Enum.Parse<BridgeAxisMode>(value.AxisMode, true);
        if (value.OvershootEnabled.HasValue) Settings.CorrectionOvershootEnabled = value.OvershootEnabled.Value;
        // These setters are exclusive. Apply false first so switching direction is deterministic.
        if (value.OvershootUpEnabled == false) Settings.CorrectionOvershootUpEnabled = false;
        if (value.OvershootDownEnabled == false) Settings.CorrectionOvershootDownEnabled = false;
        if (value.OvershootUpEnabled == true) Settings.CorrectionOvershootUpEnabled = true;
        if (value.OvershootDownEnabled == true) Settings.CorrectionOvershootDownEnabled = true;
        if (value.OvershootUpArcMin.HasValue) Settings.CorrectionOvershootUpArcMin = value.OvershootUpArcMin.Value;
        if (value.OvershootDownArcMin.HasValue) Settings.CorrectionOvershootDownArcMin = value.OvershootDownArcMin.Value;
        if (value.AutoChangeDirection.HasValue) Settings.CorrectionAutoChangeDirection = value.AutoChangeDirection.Value;
        if (value.ReverseAzimuth.HasValue) Settings.SoftwareReverseAzimuth = value.ReverseAzimuth.Value;
        if (value.ReverseAltitude.HasValue) Settings.SoftwareReverseAltitude = value.ReverseAltitude.Value;
    }
    internal static string FaultCode(string fault)
    {
        if (string.IsNullOrWhiteSpace(fault)) return "none";
        if (fault.Contains("heartbeat ESTOP", StringComparison.OrdinalIgnoreCase)) return "firmware_watchdog";
        if (fault.Contains("stale", StringComparison.OrdinalIgnoreCase) || fault.Contains("silence", StringComparison.OrdinalIgnoreCase)) return "telemetry_timeout";
        if (fault.Contains("write", StringComparison.OrdinalIgnoreCase)) return "write_unconfirmed";
        if (fault.Contains("stop", StringComparison.OrdinalIgnoreCase)) return "stop_unconfirmed";
        if (fault.Contains("connection", StringComparison.OrdinalIgnoreCase) || fault.Contains("disconnected", StringComparison.OrdinalIgnoreCase)) return "connection_lost";
        return "controller_fault";
    }
    public async Task<JsonElement> GetDiagnosticsAsync(CancellationToken token)
    {
        DiagnosticEvent[] events;
        lock (diagnosticsGate) events = diagnosticEvents.ToArray();
        var transport = !IsHardwareConnected ? DiagnosticTransport.Unknown : Wireless.IsConnected ? DiagnosticTransport.Wifi : DiagnosticTransport.Usb;
        var readiness = motionSafety.Inhibited ? DiagnosticReadiness.NotReady : Serial.IsDeviceIdle ? DiagnosticReadiness.Ready : DiagnosticReadiness.Unknown;
        var fault = FaultCode(motionSafety.Fault) switch {
            "none" => DiagnosticFault.None, "firmware_watchdog" => DiagnosticFault.FirmwareWatchdog,
            "telemetry_timeout" => DiagnosticFault.TelemetryTimeout, "write_unconfirmed" => DiagnosticFault.WriteUnconfirmed,
            "stop_unconfirmed" => DiagnosticFault.StopUnconfirmed, "connection_lost" => DiagnosticFault.Connection,
            _ => DiagnosticFault.Safety
        };
        var firmware = Regex.Match(Serial.FirmwareVersion ?? "", @"^(?:firmware\s+)?(\d+\.\d+\.\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase);
        string VersionOf(string name) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name)?.GetName().Version?.ToString();
        var stamp = Volatile.Read(ref diagnosticLastTelemetry);
        var input = new DiagnosticsInput(GetType().Assembly.GetName().Version?.ToString(), Environment.Version.ToString(), Environment.OSVersion.Version.ToString(),
            transport, Serial.IsUsbWorkerActive, fault, readiness,
            Runner.IsSessionRunning ? DiagnosticAutomation.Running : DiagnosticAutomation.Idle,
            watchdogState?.Supported ?? false, watchdogState?.Enabled, events,
            firmware.Success ? firmware.Groups[1].Value : null,
            typeof(NINA.Plugin.PluginBase).Assembly.GetName().Version?.ToString(), VersionOf("NINA.Plugins.PolarAlignment"), VersionOf("ninaAPI"),
            stamp == 0 ? null : Stopwatch.GetElapsedTime(stamp).TotalMilliseconds,
            Volatile.Read(ref diagnosticLongestGap) * 1000.0 / Stopwatch.Frequency);
        using var report = JsonDocument.Parse(DiagnosticsReport.Capture(input, await DiagnosticsReport.ReadPiPowerAsync(token)));
        return report.RootElement.Clone();
    }
}
