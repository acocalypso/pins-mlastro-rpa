using System;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;
using MLAstroRPA.Broker;
using MLAstroRPA.Services;
using MLAstroRPA.Settings;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;

[assembly: Guid("702ac5ca-9098-41ed-bf24-597dadcba00e")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.1.2.9001")]
[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/MPL/2.0/")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace NINA.Plugins.MLAstroRPA;

/// <summary>Headless composition root. Loading never opens a device or starts automation.</summary>
[Export(typeof(IPluginManifest))]
public sealed partial class MLAstroPlugin : PluginBase
{
    public static string PluginId { get; private set; }
    public PluginSettings Settings { get; }
    public SerialConnectionService Serial { get; }
    public MlastroWebSocketService Wireless { get; }
    public BridgeClient Bridge { get; }
    public BridgeRunner Runner { get; }
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private int stopGeneration;
    private readonly MotionSafety motionSafety = new();
    private int handlingMotionFault;
    private int activeStops;
    private readonly object motionGate = new();
    private DateTime connectedAt;
    private readonly object selectionGate = new();
    private readonly Action<bool> selectTppa;
    private bool ownsTppaSelection;
    private CancellationTokenSource pendingSession;
    private readonly ControlApi api;
    private readonly HardwareAligner hardware;
    private volatile bool manualMoveActive;
    private string manualMoveStatus = "Idle";
    private CancellationTokenSource manualCancellation;
    private Task manualTask = Task.CompletedTask;
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> manualCancellations = new();
    private bool MotionWorkQuiescent => Serial.WritesQuiescent && manualTask.IsCompleted && manualCancellations.All(t => t.IsCompleted);
    private void CancelManual()
    {
        var cancellation = manualCancellation;
        if (cancellation != null) manualCancellations.Add(cancellation.CancelAsync());
    }

    private bool IsHardwareConnected => Serial.IsConnected && (Wireless.IsConnected || Serial.ControllerIdentityConfirmed);
    public object GetUsbDevices()
    {
        var devices = UsbDiscovery.Enumerate();
        string selectedPath = null;
        try { selectedPath = UsbDiscovery.Select(devices, Settings.VerifiedUsbPath); } catch (InvalidOperationException) { }
        return new { devices, selectedPath };
    }
    public object GetStatus() => new {
        apiVersion = 2,
        motionInhibited = motionSafety.Inhibited, motionState = motionSafety.State, motionFault = motionSafety.Fault,
        faultCode = FaultCode(motionSafety.Fault),
        recoveryAction = motionSafety.Inhibited ? "Reconnect the RPA, wait for READY, then start a new alignment. Movement will not resume automatically." : null,
        connected = IsHardwareConnected,
        usbHeartbeatIsolated = Serial.IsUsbWorkerActive,
        transport = IsHardwareConnected ? (Wireless.IsConnected ? "wifi" : "usb") : null,
        connectionAddress = IsHardwareConnected ? (Wireless.IsConnected ? Wireless.ConfiguredAddress : Settings.ComPort) : null,
        usbDevicePath = IsHardwareConnected && Settings.TransportMode == MlastroTransportMode.Serial ? Settings.ComPort : null,
        firmware = Serial.FirmwareVersion,
        deviceStatus = Serial.DeviceStatus,
        tppaAvailable = Bridge.IsTppaAvailable,
        automationEnabled = Settings.TppaBrokerEnabled,
        sessionRunning = Runner.IsSessionRunning,
        sessionStatus = Runner.StatusText
        ,manualMoveActive, manualMoveStatus, continuousJogSupported = false,
        azimuthPositionDegrees = Serial.LastTelemetry?.AzPositionDegrees,
        altitudePositionDegrees = Serial.LastTelemetry?.AltPositionDegrees
        ,backlash = Serial.IsConnected ? new { enabled = Settings.BacklashEnabled, azSteps = Settings.BacklashAz, altSteps = Settings.BacklashAlt } : null
    };

    public async Task ManualMoveAsync(string axis, int direction, double arcseconds)
    {
        var generation = Volatile.Read(ref stopGeneration);
        if ((axis != "az" && axis != "alt") || (direction != -1 && direction != 1) ||
            !double.IsFinite(arcseconds) || arcseconds < 1 || arcseconds > 120 || arcseconds != Math.Truncate(arcseconds))
            throw new ArgumentException("Choose az/alt, direction -1/+1 and a step of 1–120 arcseconds.");
        await controlGate.WaitAsync();
        try {
            lock (motionGate) {
            RequireMotionSafe();
            if (generation != Volatile.Read(ref stopGeneration)) throw new InvalidOperationException("A stop superseded this move.");
            if (manualMoveActive || Runner.IsSessionRunning || Settings.TppaBrokerEnabled)
                throw new InvalidOperationException("Stop the current operation and disable automation before manual adjustment.");
            if (!IsHardwareConnected || !Serial.IsDeviceIdle)
                throw new InvalidOperationException("Manual adjustment requires connected hardware reporting READY.");
            manualCancellation?.Dispose();
            manualCancellation = new CancellationTokenSource();
            manualMoveActive = true;
            manualMoveStatus = "Moving; waiting for firmware completion";
            // Starts synchronously through command transmission while controlGate is held.
            manualTask = ObserveManualMoveAsync(axis, direction, (int)arcseconds, manualCancellation.Token);
            }
        } finally { controlGate.Release(); }
    }

    private async Task ObserveManualMoveAsync(string axis, int direction, int arcseconds, CancellationToken token)
    {
        try {
            token.ThrowIfCancellationRequested();
            var completed = await ManualRelativeMove.RunAsync(Serial, Wireless, axis, direction, arcseconds, token);
            manualMoveStatus = completed ? "Movement complete" : "Movement not confirmed; inspect device status";
            if (!completed) await HandleMotionFailureAsync("Manual movement completion was not confirmed.");
        } catch (OperationCanceledException) { manualMoveStatus = "Stop requested; inspect device status"; }
        catch (Exception ex) { manualMoveStatus = "Movement failed; inspect device status"; Logger.Error(ex); await HandleMotionFailureAsync("Manual movement failed; stopping could not be assumed."); }
        finally { if (manualCancellation?.Token == token) manualMoveActive = false; }
    }

    /// <summary>Explicit connection operation; never retries a movement or starts alignment.</summary>
    public Task<bool> ConnectAsync(bool wifi, string address, CancellationToken token)
    {
        return ConnectCoreAsync(wifi, address, token, Volatile.Read(ref stopGeneration));
    }
    private async Task<bool> ConnectCoreAsync(bool wifi, string address, CancellationToken token, int generation)
    {
        var recoveryStarted = false;
        long recoveryEpoch = 0;
        await controlGate.WaitAsync(token);
        try {
            if (generation != Volatile.Read(ref stopGeneration)) throw new OperationCanceledException("Connection cancelled by Stop.");
            if (Runner.IsSessionRunning) throw new InvalidOperationException("Stop alignment before changing connection.");
            if (Serial.IsConnected) throw new InvalidOperationException("Disconnect before changing connection.");
            var automaticUsb = !wifi && (string.IsNullOrWhiteSpace(address) || address == "auto");
            if (!automaticUsb && string.IsNullOrWhiteSpace(address)) throw new ArgumentException("A device address is required.");
            if (wifi && Uri.CheckHostName(address) == UriHostNameType.Unknown) throw new ArgumentException("Use a hostname or IP address without a scheme or path.");
            if (!wifi && !automaticUsb && !address.StartsWith("/dev/", StringComparison.Ordinal)) throw new ArgumentException("Choose a Linux /dev/ serial device.");
            var stopDeadline = DateTime.UtcNow.AddSeconds(6);
            while (Volatile.Read(ref activeStops) != 0 && DateTime.UtcNow < stopDeadline) await Task.Delay(10, token);
            if (Volatile.Read(ref activeStops) != 0) throw new InvalidOperationException("Stop cleanup is still running; reconnect later.");
            lock (motionGate) {
                if (generation != Volatile.Read(ref stopGeneration) || Volatile.Read(ref activeStops) != 0) throw new OperationCanceledException("Connection cancelled by Stop.");
                recoveryEpoch = motionSafety.BeginStop();
                recoveryStarted = true;
                Serial.CancelPendingMotion();
            }
            var drainDeadline = DateTime.UtcNow.AddSeconds(2);
            while (!MotionWorkQuiescent && DateTime.UtcNow < drainDeadline) await Task.Delay(10, token);
            if (!MotionWorkQuiescent) throw new InvalidOperationException("Previous transport work has not finished; reconnect later.");
            connectedAt = DateTime.UtcNow;
            watchdogState = null;
            Settings.TransportMode = wifi ? MlastroTransportMode.Wireless : MlastroTransportMode.Serial;
            if (wifi) {
                Settings.MlaHost = address;
                var connected = await Wireless.ConnectAsync(token);
                if (generation != Volatile.Read(ref stopGeneration) || token.IsCancellationRequested) {
                    Wireless.Disconnect();
                    throw new OperationCanceledException("Connection cancelled.");
                }
                if (connected) await RecoverConnectionAsync(generation, recoveryEpoch, token);
                else LatchMotionFailure("Connection was not confirmed; movement remains blocked.");
                return connected;
            }
            if (automaticUsb) {
                await UsbAutoConnector.FindAsync(UsbDiscovery.Enumerate(), Settings.VerifiedUsbPath,
                    (path, ct) => ConnectConfirmedUsbAsync(path, ct, generation), token);
                await RecoverConnectionAsync(generation, recoveryEpoch, token);
                return true;
            }
            var usbConnected = await ConnectConfirmedUsbAsync(address, token, generation);
            if (usbConnected) await RecoverConnectionAsync(generation, recoveryEpoch, token);
            return usbConnected;
        } catch {
            if (recoveryStarted) lock (motionGate) {
                if (IsHardwareConnected) LatchMotionFailure("Connection recovery was not confirmed; movement remains blocked.");
                else motionSafety.CompleteStop(recoveryEpoch); // An unidentified closed port never admitted movement.
            }
            throw;
        } finally { controlGate.Release(); }
    }

    private async Task<bool> ConnectConfirmedUsbAsync(string address, CancellationToken token, int generation)
    {
        if (generation != Volatile.Read(ref stopGeneration)) throw new OperationCanceledException("Connection cancelled by Stop.");
        connectedAt = DateTime.UtcNow;
        Settings.ComPort = address;
        try {
            // Identification owns the port exclusively. Normal polling starts only after identity
            // confirmation, so its failure counter cannot terminate an ESP32 boot/handshake.
            if (!await Serial.ConnectAsync(address, 115200, startChecks: false))
                throw new InvalidOperationException($"Cannot open {address}; it may be busy or inaccessible.");
            await Task.Delay(600, token);
            var until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && Serial.IsConnected) {
                token.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref stopGeneration)) throw new OperationCanceledException("Connection cancelled by Stop.");
                if (!Serial.ControllerIdentityConfirmed) await Serial.SendHandshakeAsync().WaitAsync(token);
                if (Serial.ControllerIdentityConfirmed) {
                    if (!await Serial.SendStatusQueryAsync(token)) throw new InvalidOperationException("Status query was not confirmed during USB identification.");
                    await Task.Delay(150, token);
                    if (Serial.LastTelemetryAt >= connectedAt && Serial.LastTelemetry != null) {
                        if (generation != Volatile.Read(ref stopGeneration)) throw new OperationCanceledException("Connection cancelled by Stop.");
                        Settings.VerifiedUsbPath = address;
                        Serial.BeginConnectionChecks();
                        return true;
                    }
                }
                await Task.Delay(250, token);
            }
            throw new InvalidOperationException($"No MLAstro firmware and telemetry confirmed on {address}.");
        } catch {
            Serial.Disconnect(releaseControl: Serial.ControllerIdentityConfirmed);
            // A failed identification must retire its worker before automatic discovery
            // tries another port; otherwise the next healthy candidate looks busy.
            var retireDeadline = DateTime.UtcNow.AddSeconds(4);
            while (!Serial.WritesQuiescent && DateTime.UtcNow < retireDeadline) await Task.Delay(10);
            throw;
        }
    }

    public object GetCorrectionSettings() => new {
        factor = Settings.CorrectionSafetyFactor,
        maxStepArcMin = Settings.CorrectionMaxStepArcMin,
        settleSeconds = Settings.AutomatedAdjustmentSettleTime,
        pauseBeforeFirstCorrection = Settings.AutoPauseAfterReferenceSweep,
        axisMode = Settings.CorrectionAxisMode.ToString(),
        overshootEnabled = Settings.CorrectionOvershootEnabled,
        overshootUpEnabled = Settings.CorrectionOvershootUpEnabled,
        overshootDownEnabled = Settings.CorrectionOvershootDownEnabled,
        overshootUpArcMin = Settings.CorrectionOvershootUpArcMin,
        overshootDownArcMin = Settings.CorrectionOvershootDownArcMin,
        autoChangeDirection = Settings.CorrectionAutoChangeDirection,
        reverseAzimuth = Settings.SoftwareReverseAzimuth,
        reverseAltitude = Settings.SoftwareReverseAltitude
    };

    public async Task<object> DiscoverTppaAsync(CancellationToken token)
    {
        await controlGate.WaitAsync(token);
        try {
            if (Settings.TppaBrokerEnabled) return new { available = Bridge.IsTppaAvailable, version = Bridge.Capabilities?.TppaVersion };
            Bridge.Start(); // Capability announcements only; the correction runner is not started.
            try {
                var until = DateTime.UtcNow.AddSeconds(3);
                while (!Bridge.IsTppaAvailable && DateTime.UtcNow < until) await Task.Delay(50, token);
                return new { available = Bridge.IsTppaAvailable, version = Bridge.Capabilities?.TppaVersion };
            } finally { Bridge.Stop(); }
        } finally { controlGate.Release(); }
    }

    public async Task SelectForTppaAsync()
    {
        var generation = Volatile.Read(ref stopGeneration);
        await controlGate.WaitAsync();
        try {
            lock (motionGate) {
            RequireMotionSafe();
            if (generation != Volatile.Read(ref stopGeneration)) throw new InvalidOperationException("A stop superseded controller selection.");
            if (Runner.IsSessionRunning || Settings.TppaBrokerEnabled) throw new InvalidOperationException("Disable automation before selecting the controller.");
            SelectTppaForOneRun();
            }
        } finally { controlGate.Release(); }
    }

    public async Task ConfigureCorrectionAsync(double factor, double maxStepArcMin, double settleSeconds, bool pauseBeforeFirstCorrection, CorrectionOptions options = null)
    {
        if (!double.IsFinite(factor) || factor < 0.05 || factor > 1 ||
            !double.IsFinite(maxStepArcMin) || maxStepArcMin < 1 ||
            !double.IsFinite(settleSeconds) || settleSeconds < 0 || settleSeconds > 60)
            throw new ArgumentException("Factor must be 0.05–1, maximum step at least 1 arcmin, and settling 0–60 seconds.");
        await controlGate.WaitAsync();
        try {
            if (Runner.IsSessionRunning || Settings.TppaBrokerEnabled) throw new InvalidOperationException("Disable automation before changing correction settings.");
            options = ValidateOptions(options);
            Settings.CorrectionSafetyFactor = factor;
            Settings.CorrectionMaxStepArcMin = maxStepArcMin;
            Settings.AutomatedAdjustmentSettleTime = settleSeconds;
            Settings.AutoPauseAfterReferenceSweep = pauseBeforeFirstCorrection;
            ApplyOptions(options);
        } finally { controlGate.Release(); }
    }

    public async Task SetAutomationAsync(bool enabled)
    {
        var generation = Volatile.Read(ref stopGeneration);
        await controlGate.WaitAsync();
        try {
            if (enabled) {
                lock (motionGate) {
                RequireMotionSafe();
                if (manualMoveActive) throw new InvalidOperationException("Wait for the manual adjustment to complete.");
                if (generation != Volatile.Read(ref stopGeneration)) throw new InvalidOperationException("A stop superseded this enable request.");
                if (!IsHardwareConnected || !Serial.IsDeviceIdle) throw new InvalidOperationException("Connect idle hardware before enabling automation.");
                SelectTppaForOneRun();
                Runner.Start();
                Bridge.Enabled = true;
                lock (selectionGate) {
                    pendingSession?.Cancel();
                    pendingSession?.Dispose();
                    pendingSession = new CancellationTokenSource();
                    _ = ExpirePendingStartAsync(pendingSession);
                }
                }
            } else {
                try { if (Runner.IsSessionRunning) await Runner.AbortSessionAsync(BridgeReason.BrokerDisabled); }
                finally {
                    try { Runner.Stop(); }
                    finally { try { Bridge.Enabled = false; } finally { ReleaseTppaSelection(); } }
                }
            }
        } finally { controlGate.Release(); }
    }

    private void RequireMotionSafe()
    {
        if (motionSafety.Inhibited || Serial.MotionWritesBlocked)
            throw new InvalidOperationException(motionSafety.Fault ?? "Movement is blocked while stopping or recovering the connection.");
    }

    private async Task RecoverConnectionAsync(int generation, long epoch, CancellationToken token)
    {
        // Reconnection is the only way to clear an uncertain Stop. Old callbacks must
        // actually finish, and READY must come from this newly identified connection.
        if (!await ConfirmFreshReadyAsync(token) || generation != Volatile.Read(ref stopGeneration) ||
            !MotionWorkQuiescent)
            throw new InvalidOperationException("Connection recovery is not confirmed; disconnect and retry when the device reports READY.");
        lock (motionGate) {
            if (generation != Volatile.Read(ref stopGeneration) || motionSafety.Epoch != epoch)
                throw new InvalidOperationException("Recovery superseded by Stop.");
            try {
                Serial.AllowMotion();
                if (!motionSafety.ClearAfterReconnect(epoch)) throw new InvalidOperationException("Recovery superseded by Stop.");
                RecordEvent(DiagnosticEventCode.Connected);
            } catch {
                LatchMotionFailure("Connection recovery did not finish; movement remains blocked.");
                throw;
            }
        }
    }

    private async Task<bool> ConfirmFreshReadyAsync(CancellationToken token = default)
    {
        var before = Serial.LastTelemetry;
        var connection = connectedAt;
        var until = DateTime.UtcNow.AddSeconds(4);
        DateTime? readySince = null;
        object firstReadySample = null;
        while (DateTime.UtcNow < until) {
            token.ThrowIfCancellationRequested();
            if (!IsHardwareConnected || connectedAt != connection) return false;
            if (!ReferenceEquals(before, Serial.LastTelemetry) && Serial.IsDeviceIdle &&
                DateTime.UtcNow - Serial.LastTelemetryAt <= TimeSpan.FromMilliseconds(750)) {
                if (!readySince.HasValue) { readySince = DateTime.UtcNow; firstReadySample = Serial.LastTelemetry; }
                if (!ReferenceEquals(firstReadySample, Serial.LastTelemetry) && DateTime.UtcNow - readySince.Value >= TimeSpan.FromMilliseconds(500)) return true;
            } else { readySince = null; firstReadySample = null; }
            await Task.Delay(50, token);
        }
        return false;
    }

    private void LatchMotionFailure(string reason)
    {
        lock (motionGate) {
            motionSafety.Fail(motionSafety.Epoch, reason);
            RecordEvent(DiagnosticEventCode.Fault);
            Serial.CancelPendingMotion();
        }
    }

    private async Task HandleMotionFailureAsync(string reason)
    {
        LatchMotionFailure(reason);
        if (Interlocked.Exchange(ref handlingMotionFault, 1) != 0) return;
        try { await StopAsync(); } catch (Exception ex) { Logger.Error(ex); }
        finally { Volatile.Write(ref handlingMotionFault, 0); }
    }

    /// <summary>Fence movement first; STOP and session cleanup are independent.</summary>
    public async Task StopAsync()
    {
        Interlocked.Increment(ref activeStops);
        try {
        long epoch;
        bool hadActivity;
        var abortReason = motionSafety.Fault == null ? BridgeReason.UserStop : BridgeReason.ControllerFault;
        lock (motionGate) {
            hadActivity = manualMoveActive || Runner.IsSessionRunning;
            Interlocked.Increment(ref stopGeneration);
            epoch = motionSafety.BeginStop();
            Serial.CancelPendingMotion();
        }
        CancelManual();
        CancelManagement();
        hardware.AbortPendingMove();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task Attempt(Func<Task> action, string name) {
            try { await action(); } catch (Exception ex) { failures.Enqueue(name + ": " + ex.Message); }
        }
        // Never use cached READY to skip STOP. Cleanup must still run if STOP fails.
        var stop = Attempt(async () => {
            if (IsHardwareConnected) {
                if (!await hardware.StopAsync()) throw new InvalidOperationException("STOP was not transmitted.");
                if (!await ConfirmFreshReadyAsync()) throw new InvalidOperationException("Fresh READY after STOP was not confirmed.");
            } else if (hadActivity)
                throw new InvalidOperationException("Link is unavailable; physical stop is unconfirmed.");
        }, "Hardware stop failed");
        try {
            if (Runner.IsSessionRunning) await Attempt(() => Runner.AbortSessionAsync(abortReason), "Alignment abort failed");
        } finally {
            // Every cleanup runs even if another cleanup fails.
            await Attempt(() => { Runner.Stop(); return Task.CompletedTask; }, "Runner cleanup failed");
            await Attempt(() => { Bridge.Enabled = false; return Task.CompletedTask; }, "Automation cleanup failed");
            await Attempt(() => { ReleaseTppaSelection(); return Task.CompletedTask; }, "TPPA cleanup failed");
        }
        await stop;
        var quiescenceDeadline = DateTime.UtcNow.AddMilliseconds(400);
        while (!MotionWorkQuiescent && DateTime.UtcNow < quiescenceDeadline) await Task.Delay(10);
        lock (motionGate) {
            if (!MotionWorkQuiescent) failures.Enqueue("Transport work has not finished.");
            if (!failures.IsEmpty) motionSafety.Fail(epoch, string.Join(" ", failures));
            else {
                motionSafety.CompleteStop(epoch);
                if (!motionSafety.Inhibited) {
                    try { if (IsHardwareConnected) Serial.AllowMotion(); }
                    catch { LatchMotionFailure("Stop cleanup is not quiescent; reconnect before moving."); throw; }
                }
            }
        }
        if (motionSafety.Inhibited) throw new InvalidOperationException(motionSafety.Fault ?? "Stop is still in progress.");
        RecordEvent(DiagnosticEventCode.Stopped);
        } finally { Interlocked.Decrement(ref activeStops); }
    }

    public async Task DisconnectAsync()
    {
        Exception failure = null;
        try { await StopAsync(); } catch (Exception ex) { failure = ex; }
        await controlGate.WaitAsync();
        try {
            try { Wireless.Disconnect(); }
            finally { Serial.Disconnect(); }
            RecordEvent(DiagnosticEventCode.Disconnected);
            // Closing a worker is asynchronous. Finish retiring it before reporting
            // a clean disconnect, so a following Stop cannot mistake idle cleanup for motion.
            var until = DateTime.UtcNow.AddSeconds(4);
            while (!MotionWorkQuiescent && DateTime.UtcNow < until) await Task.Delay(10);
            if (!MotionWorkQuiescent)
                throw new InvalidOperationException("Disconnected; transport cleanup is still finishing. Wait before reconnecting.");
        } finally { controlGate.Release(); }
        if (failure != null) throw new InvalidOperationException("Disconnected; physical stop remains unconfirmed. " + failure.Message, failure);
    }

    private void SendRequired(string text) { if (!Serial.Send(text)) throw new InvalidOperationException("Device command could not be sent."); }
    private void RequireManualReady()
    {
        RequireMotionSafe();
        RequireManagementIdle();
        if (!IsHardwareConnected || !Serial.IsDeviceIdle) throw new InvalidOperationException("Connect an idle RPA reporting READY before changing device settings.");
    }

    public sealed record DeviceSettings(int speedLevel, bool backlashEnabled, int azSteps, int altSteps);
    public DeviceSettings GetDeviceSettings()
    {
        var data = Serial.LastTelemetry;
        if (!IsHardwareConnected || data == null || Serial.LastTelemetryAt < connectedAt || !data.HasSpeedLevel || !data.DeviceBacklashEnabled.HasValue || !data.DeviceBacklashAz.HasValue || !data.DeviceBacklashAlt.HasValue)
            throw new InvalidOperationException("Waiting for device speed and backlash telemetry; reload after connecting.");
        return new(data.SpeedLevel, data.DeviceBacklashEnabled.Value, data.DeviceBacklashAz.Value, data.DeviceBacklashAlt.Value);
    }
    public async Task<DeviceSettings> ConfigureDeviceAsync(DeviceSettings value, CancellationToken token)
    {
        if (value.speedLevel < 1 || value.speedLevel > 5 || value.azSteps < 0 || value.altSteps < 0 || value.azSteps > 10000000 || value.altSteps > 10000000)
            throw new ArgumentException("Speed must be 1–5 and backlash must be 0–10000000 integer steps.");
        await controlGate.WaitAsync(token);
        try {
            RequireManualReady();
            GetDeviceSettings(); // Never write cached host defaults before a device snapshot arrives.
            var before = Serial.LastTelemetry;
            var config = $"Back:{(value.backlashEnabled ? 1 : 0)},AzBl:{value.azSteps},AlBl:{value.altSteps}\n";
            if (Settings.TransportMode == MlastroTransportMode.Wireless) {
                if (!await Wireless.SendCommandAndAwaitOkAsync(config)) throw new InvalidOperationException("Firmware did not acknowledge backlash settings; reload before retrying.");
                if (!await Wireless.SendCommandAndAwaitOkAsync($"SLvl:{value.speedLevel}\n")) throw new InvalidOperationException("Speed command failed; reload settings.");
            } else {
                SendRequired(config); SendRequired($"SLvl:{value.speedLevel}\n");
            }
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until) {
                await Task.Delay(100, token);
                if (ReferenceEquals(before, Serial.LastTelemetry)) continue;
                if (GetDeviceSettings() == value) return value;
            }
            throw new InvalidOperationException("Device settings readback was not confirmed; reload to inspect actual values.");
        } finally { controlGate.Release(); }
    }

    private void SelectTppaForOneRun()
    {
        lock (selectionGate) {
            if (ownsTppaSelection) return;
            // Mark ownership first so a failed selection still gets cleanup through Stop.
            ownsTppaSelection = true;
            try { selectTppa(true); }
            catch { ReleaseTppaSelection(); throw; }
        }
    }
    private void ReleaseTppaSelection()
    {
        lock (selectionGate) {
            pendingSession?.Cancel();
            pendingSession?.Dispose();
            pendingSession = null;
            if (!ownsTppaSelection) return;
            selectTppa(false);
            ownsTppaSelection = false;
        }
    }
    private void OnSessionActiveChanged(object sender, bool active)
    {
        if (active) {
            lock (selectionGate) { pendingSession?.Cancel(); }
            return;
        }
        // Runs synchronously before another queued session can be accepted by the runner.
        Bridge.Enabled = false;
        try { ReleaseTppaSelection(); }
        finally { Runner.Stop(); }
    }
    private async Task ExpirePendingStartAsync(CancellationTokenSource pending)
    {
        try {
            await Task.Delay(TimeSpan.FromSeconds(20), pending.Token);
            await controlGate.WaitAsync();
            try {
                lock (selectionGate) {
                    if (pendingSession != pending || Runner.IsSessionRunning) return;
                    Runner.Stop(); Bridge.Enabled = false;
                    ReleaseTppaSelection();
                }
            } finally { controlGate.Release(); }
        } catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Error(ex); }
    }

    [ImportingConstructor]
    public MLAstroPlugin(PluginSettings settings, SerialConnectionService serial, IMessageBroker broker)
        : this(settings, serial, broker, 1891) { }

    public MLAstroPlugin(PluginSettings settings, SerialConnectionService serial, IMessageBroker broker, int controlPort)
        : this(settings, serial, broker, controlPort, null) { }

    public MLAstroPlugin(PluginSettings settings, SerialConnectionService serial, IMessageBroker broker, int controlPort, Action<bool> selectTppa)
    {
        PluginId = Identifier;
        this.selectTppa = selectTppa ?? (enabled => TppaSelection.SetExternal(enabled, PluginId));
        Settings = settings;
        Settings.TppaBrokerEnabled = false;
        Serial = serial;
        Wireless = new MlastroWebSocketService(settings, serial);
        Bridge = new BridgeClient(broker, settings);
        hardware = new HardwareAligner(serial);
        Runner = new BridgeRunner(Bridge, hardware, settings);
        InitializeDiagnostics();
        Runner.SessionActiveChanged += OnSessionActiveChanged;
        hardware.StopFailed += reason => LatchMotionFailure(reason);
        Serial.MotionFaulted += reason => { _ = HandleMotionFailureAsync(reason); };
        Serial.TelemetryStale += () => {
            // A TPPA session includes mount slews and captures while the RPA is idle.
            // Fresh telemetry still gates every new correction; only an outstanding RPA move
            // needs the immediate stop/fault when telemetry becomes stale.
            if (!motionSafety.Inhibited && (((manualMoveActive || hardware.IsMoving) && !Serial.HasRecentMotionTelemetry)))
                _ = HandleMotionFailureAsync("Telemetry became stale during RPA movement; stopped and inhibited until reconnection.");
        };
        api = new ControlApi(this, controlPort);
        Logger.Info("[MLAstro Linux] Loaded; hardware connection and automation remain disabled until explicitly enabled.");
    }

    public override async Task Teardown()
    {
        api.Dispose();
        try { await StopAsync(); } catch (Exception ex) { Logger.Error(ex); }
        CancelManual();
        try { ReleaseTppaSelection(); } catch (Exception ex) { Logger.Error(ex); }
        Runner.SessionActiveChanged -= OnSessionActiveChanged;
        foreach (var disposable in new IDisposable[] { Runner, Bridge, Wireless, Serial, hardware }) {
            try { disposable.Dispose(); } catch (Exception ex) { Logger.Error(ex); }
        }
        PluginSettings.ClearInstance();
    }
}
