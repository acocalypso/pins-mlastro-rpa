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
public sealed class MLAstroPlugin : PluginBase
{
    public static string PluginId { get; private set; }
    public PluginSettings Settings { get; }
    public SerialConnectionService Serial { get; }
    public MlastroWebSocketService Wireless { get; }
    public BridgeClient Bridge { get; }
    public BridgeRunner Runner { get; }
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private int stopGeneration;
    private readonly JogLease jog;
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

    private bool IsHardwareConnected => Serial.IsConnected && (Wireless.IsConnected || Serial.ControllerIdentityConfirmed);
    public object GetUsbDevices()
    {
        var devices = UsbDiscovery.Enumerate();
        string selectedPath = null;
        try { selectedPath = UsbDiscovery.Select(devices, Settings.VerifiedUsbPath); } catch (InvalidOperationException) { }
        return new { devices, selectedPath };
    }
    public object GetStatus() => new {
        connected = IsHardwareConnected,
        usbDevicePath = IsHardwareConnected && Settings.TransportMode == MlastroTransportMode.Serial ? Settings.ComPort : null,
        firmware = Serial.FirmwareVersion,
        deviceStatus = Serial.DeviceStatus,
        tppaAvailable = Bridge.IsTppaAvailable,
        automationEnabled = Settings.TppaBrokerEnabled,
        sessionRunning = Runner.IsSessionRunning,
        sessionStatus = Runner.StatusText
        ,manualMoveActive, manualMoveStatus, jogActive = jog.Active, jogError = jog.Error,
        azimuthPositionDegrees = Serial.LastTelemetry?.AzPositionDegrees,
        altitudePositionDegrees = Serial.LastTelemetry?.AltPositionDegrees
        ,backlash = Serial.IsConnected ? new { enabled = Settings.BacklashEnabled, azSteps = Settings.BacklashAz, altSteps = Settings.BacklashAlt } : null
    };

    public async Task ManualMoveAsync(string axis, int direction, double arcseconds)
    {
        if ((axis != "az" && axis != "alt") || (direction != -1 && direction != 1) ||
            !double.IsFinite(arcseconds) || arcseconds < 1 || arcseconds > 120 || arcseconds != Math.Truncate(arcseconds))
            throw new ArgumentException("Choose az/alt, direction -1/+1 and a step of 1–120 arcseconds.");
        await controlGate.WaitAsync();
        try {
            if (jog.Active || manualMoveActive || Runner.IsSessionRunning || Settings.TppaBrokerEnabled)
                throw new InvalidOperationException("Stop the current operation and disable automation before manual adjustment.");
            if (!IsHardwareConnected || !Serial.IsDeviceIdle)
                throw new InvalidOperationException("Manual adjustment requires connected hardware reporting READY.");
            manualCancellation?.Dispose();
            manualCancellation = new CancellationTokenSource();
            manualMoveActive = true;
            manualMoveStatus = "Moving; waiting for firmware completion";
            // Starts synchronously through command transmission while controlGate is held.
            manualTask = ObserveManualMoveAsync(axis, direction, (int)arcseconds, manualCancellation.Token);
        } finally { controlGate.Release(); }
    }

    private async Task ObserveManualMoveAsync(string axis, int direction, int arcseconds, CancellationToken token)
    {
        try {
            token.ThrowIfCancellationRequested();
            var completed = await ManualRelativeMove.RunAsync(Serial, Wireless, axis, direction, arcseconds, token);
            manualMoveStatus = completed ? "Movement complete" : "Movement not confirmed; inspect device status";
        } catch (OperationCanceledException) { manualMoveStatus = "Stop requested; inspect device status"; }
        catch (Exception ex) { manualMoveStatus = "Movement failed; inspect device status"; Logger.Error(ex); }
        finally { manualMoveActive = false; }
    }

    /// <summary>Explicit connection operation; never retries a movement or starts alignment.</summary>
    public async Task<bool> ConnectAsync(bool wifi, string address, CancellationToken token)
    {
        var generation = Volatile.Read(ref stopGeneration);
        await controlGate.WaitAsync(token);
        try {
            if (generation != Volatile.Read(ref stopGeneration)) throw new OperationCanceledException("Connection cancelled by Stop.");
            if (Runner.IsSessionRunning) throw new InvalidOperationException("Stop alignment before changing connection.");
            if (Serial.IsConnected) throw new InvalidOperationException("Disconnect before changing connection.");
            var automaticUsb = !wifi && (string.IsNullOrWhiteSpace(address) || address == "auto");
            if (!automaticUsb && string.IsNullOrWhiteSpace(address)) throw new ArgumentException("A device address is required.");
            if (wifi && Uri.CheckHostName(address) == UriHostNameType.Unknown) throw new ArgumentException("Use a hostname or IP address without a scheme or path.");
            if (!wifi && !automaticUsb && !address.StartsWith("/dev/", StringComparison.Ordinal)) throw new ArgumentException("Choose a Linux /dev/ serial device.");
            connectedAt = DateTime.UtcNow;
            Settings.TransportMode = wifi ? MlastroTransportMode.Wireless : MlastroTransportMode.Serial;
            if (wifi) {
                Settings.MlaHost = address;
                var connected = await Wireless.ConnectAsync(token);
                if (generation != Volatile.Read(ref stopGeneration) || token.IsCancellationRequested) {
                    Wireless.Disconnect();
                    throw new OperationCanceledException("Connection cancelled.");
                }
                return connected;
            }
            if (automaticUsb) {
                await UsbAutoConnector.FindAsync(UsbDiscovery.Enumerate(), Settings.VerifiedUsbPath,
                    (path, ct) => ConnectConfirmedUsbAsync(path, ct, generation), token);
                return true;
            }
            return await ConnectConfirmedUsbAsync(address, token, generation);
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
                    Serial.Send("?\n");
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
        } catch { Serial.Disconnect(releaseControl: Serial.ControllerIdentityConfirmed); throw; }
    }

    public object GetCorrectionSettings() => new {
        factor = Settings.CorrectionSafetyFactor,
        maxStepArcMin = Settings.CorrectionMaxStepArcMin,
        settleSeconds = Settings.AutomatedAdjustmentSettleTime,
        pauseBeforeFirstCorrection = Settings.AutoPauseAfterReferenceSweep
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
        await controlGate.WaitAsync();
        try {
            if (Runner.IsSessionRunning || Settings.TppaBrokerEnabled) throw new InvalidOperationException("Disable automation before selecting the controller.");
            SelectTppaForOneRun();
        } finally { controlGate.Release(); }
    }

    public async Task ConfigureCorrectionAsync(double factor, double maxStepArcMin, double settleSeconds, bool pauseBeforeFirstCorrection)
    {
        if (!double.IsFinite(factor) || factor < 0.1 || factor > 1 ||
            !double.IsFinite(maxStepArcMin) || maxStepArcMin < 0.1 || maxStepArcMin > 10 ||
            !double.IsFinite(settleSeconds) || settleSeconds < 1 || settleSeconds > 60)
            throw new ArgumentException("Factor must be 0.1–1, maximum step 0.1–10 arcmin, and settling 1–60 seconds.");
        await controlGate.WaitAsync();
        try {
            if (Runner.IsSessionRunning || Settings.TppaBrokerEnabled) throw new InvalidOperationException("Disable automation before changing correction settings.");
            Settings.CorrectionSafetyFactor = factor;
            Settings.CorrectionMaxStepArcMin = maxStepArcMin;
            Settings.AutomatedAdjustmentSettleTime = settleSeconds;
            Settings.AutoPauseAfterReferenceSweep = pauseBeforeFirstCorrection;
        } finally { controlGate.Release(); }
    }

    public async Task SetAutomationAsync(bool enabled)
    {
        var generation = Volatile.Read(ref stopGeneration);
        await controlGate.WaitAsync();
        try {
            if (enabled) {
                if (jog.Active || manualMoveActive) throw new InvalidOperationException("Wait for the manual adjustment to complete.");
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
            } else {
                if (Runner.IsSessionRunning) await Runner.AbortSessionAsync(BridgeReason.BrokerDisabled);
                Runner.Stop();
                Bridge.Enabled = false;
                ReleaseTppaSelection();
            }
        } finally { controlGate.Release(); }
    }

    /// <summary>Stop has priority over pending connection and enable operations.</summary>
    public async Task StopAsync()
    {
        Interlocked.Increment(ref stopGeneration);
        await jog.StopAsync();
        if (manualMoveActive) {
            manualCancellation.Cancel();
            await hardware.StopAsync();
        }
        if (Runner.IsSessionRunning) await Runner.AbortSessionAsync(BridgeReason.UserStop);
        else if (IsHardwareConnected && !Serial.IsDeviceIdle) Serial.Send("STOP:1\n");
        await controlGate.WaitAsync();
        try {
            await jog.StopAsync();
            if (manualMoveActive) { manualCancellation.Cancel(); await hardware.StopAsync(); }
            Runner.Stop(); Bridge.Enabled = false;
            ReleaseTppaSelection();
        }
        finally { controlGate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        if (jog.Active || manualMoveActive) await StopAsync();
        await SetAutomationAsync(false);
        await controlGate.WaitAsync();
        try { await jog.StopAsync(); if (manualMoveActive) { manualCancellation.Cancel(); await hardware.StopAsync(); } Wireless.Disconnect(); Serial.Disconnect(); }
        finally { controlGate.Release(); }
    }


    private void RequireManualReady()
    {
        if (jog.Active || manualMoveActive || Runner.IsSessionRunning || Settings.TppaBrokerEnabled || !IsHardwareConnected || !Serial.IsDeviceIdle)
            throw new InvalidOperationException("Connect READY hardware and stop other operations before adjusting the RPA.");
    }
    public async Task<string> StartJogAsync(string axis, int direction)
    {
        if ((axis != "az" && axis != "alt") || (direction != -1 && direction != 1)) throw new ArgumentException("Invalid jog axis or direction.");
        var generation = Volatile.Read(ref stopGeneration);
        await controlGate.WaitAsync();
        try {
            RequireManualReady();
            if (generation != Volatile.Read(ref stopGeneration)) throw new InvalidOperationException("A stop superseded this jog.");
            return await jog.StartAsync(axis, direction);
        } finally { controlGate.Release(); }
    }
    public Task RenewJogAsync(string leaseId) => jog.RenewAsync(leaseId);
    public Task StopJogAsync(string leaseId) => jog.StopAsync(leaseId ?? throw new ArgumentException("A lease ID is required."));
    private static string Arrow(string axis, int direction) => axis == "az" ? (direction > 0 ? "MAzR" : "MAzL") : (direction > 0 ? "MAlU" : "MAlD");
    private void SendRequired(string text) { if (!Serial.Send(text)) throw new InvalidOperationException("Device command could not be sent."); }
    private async Task StartJogHardwareAsync(string axis, int direction, CancellationToken token)
    {
        if (Settings.TransportMode == MlastroTransportMode.Wireless) await Wireless.SendJogAsync(axis, direction, true, token);
        else { SendRequired("JoRe:0\n"); SendRequired(Arrow(axis, direction) + ":1\n"); }
    }
    private Task RefreshJogHardwareAsync(string axis, int direction, CancellationToken token)
    {
        // USB requires a held-button heartbeat. Wi-Fi move commands are sent only once.
        if (Settings.TransportMode != MlastroTransportMode.Wireless) SendRequired(Arrow(axis, direction) + ":1\n");
        return Task.CompletedTask;
    }
    private async Task StopJogHardwareAsync(string axis, int direction, CancellationToken token)
    {
        if (Settings.TransportMode == MlastroTransportMode.Wireless) await Wireless.SendJogAsync(axis, direction, false, token);
        else SendRequired(Arrow(axis, direction) + ":0\n");
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
        Runner.SessionActiveChanged += OnSessionActiveChanged;
        jog = new JogLease(StartJogHardwareAsync, RefreshJogHardwareAsync, StopJogHardwareAsync);
        api = new ControlApi(this, controlPort);
        Logger.Info("[MLAstro Linux] Loaded; hardware connection and automation remain disabled until explicitly enabled.");
    }

    public override async Task Teardown()
    {
        api.Dispose();
        if (jog.Active || manualMoveActive || Runner.IsSessionRunning) await StopAsync();
        await jog.DisposeAsync();
        manualCancellation?.Cancel();
        ReleaseTppaSelection();
        Runner.SessionActiveChanged -= OnSessionActiveChanged;
        Runner.Dispose();
        Bridge.Dispose();
        Wireless.Dispose();
        Serial.Dispose();
        hardware.Dispose();
        PluginSettings.ClearInstance();
    }
}
