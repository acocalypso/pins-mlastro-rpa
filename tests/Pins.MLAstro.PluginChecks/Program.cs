using System.Reflection;
using System.Runtime.Loader;

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(Environment.GetEnvironmentVariable("PINS_HOME") ?? throw new InvalidOperationException("Set PINS_HOME to the installed PINS application directory."), name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
return PluginChecks.Run();

static class PluginChecks
{
    // Keep PINS references outside the entry method so the resolver is installed before JIT resolution.
    public static int Run()
    {
        var storage = new NINA.Profile.PluginSettings();
        var profile = DispatchProxy.Create<NINA.Profile.Interfaces.IProfile, ValueProxy>();
        ((ValueProxy)(object)profile).Values["get_PluginSettings"] = storage;
        var profiles = DispatchProxy.Create<NINA.Profile.Interfaces.IProfileService, ValueProxy>();
        ((ValueProxy)(object)profiles).Values["get_ActiveProfile"] = profile;
        var settings = new MLAstroRPA.Settings.PluginSettings(profiles);
        var broker = new RecordingBroker();
        var serial = new MLAstroRPA.Services.SerialConnectionService(settings);
        bool externalSelected = false;
        var plugin = new NINA.Plugins.MLAstroRPA.MLAstroPlugin(settings, serial, broker, 1893, enabled => externalSelected = enabled);
        if (serial.IsConnected || plugin.Runner.IsSessionRunning || broker.Messages.Count != 0)
            throw new Exception("Loading must not connect, move or announce an enabled controller.");
        Console.WriteLine("PASS headless plugin construction without hardware or broker activity");
        if (serial.IsDeviceIdle) throw new Exception("Missing telemetry must not be treated as idle.");
        Console.WriteLine("PASS unknown hardware state blocks readiness");
        try {
            plugin.SetAutomationAsync(true).GetAwaiter().GetResult();
            throw new Exception("Automation accepted disconnected hardware.");
        } catch (InvalidOperationException) { Console.WriteLine("PASS automation rejects disconnected hardware"); }
        settings.MlaHost = "mlastrorpa.local";
        if (settings.MlaHost != "mlastrorpa.local") throw new Exception("Settings did not round-trip.");
        Console.WriteLine("PASS settings round-trip in isolated in-memory profile");
        if (settings.CorrectionMaxStepArcMin != 120) throw new Exception("Correction cap default differs from upstream 120 arcmin");
        settings.CorrectionMaxStepArcMin = 0.5;
        if (settings.CorrectionMaxStepArcMin != 120) throw new Exception("Legacy invalid cap did not fall back to restored default");
        plugin.ConfigureCorrectionAsync(0.05, 240, 0, true).GetAwaiter().GetResult();
        if (settings.CorrectionSafetyFactor != 0.05 || settings.CorrectionMaxStepArcMin != 240 || settings.AutomatedAdjustmentSettleTime != 0)
            throw new Exception("Upstream correction setting ranges rejected or changed");
        foreach (var invalid in new[] { (0.04, 120.0, 0.0), (0.75, 0.5, 0.0), (0.75, double.PositiveInfinity, 0.0), (0.75, 120.0, -1.0) }) {
            try {
                plugin.ConfigureCorrectionAsync(invalid.Item1, invalid.Item2, invalid.Item3, true).GetAwaiter().GetResult();
                throw new Exception("Invalid correction settings accepted");
            } catch (ArgumentException) { }
        }
        plugin.ConfigureCorrectionAsync(0.75, 120, 3, true).GetAwaiter().GetResult();
        Console.WriteLine("PASS upstream correction default, legacy-cap migration and correction ranges");
        plugin.ConfigureCorrectionAsync(0.75, 120, 3, true, new NINA.Plugins.MLAstroRPA.CorrectionOptions(
            AxisMode: "Auto", OvershootEnabled: false, OvershootUpEnabled: true, OvershootDownEnabled: false,
            OvershootUpArcMin: 7, OvershootDownArcMin: 9, AutoChangeDirection: false, ReverseAzimuth: true)).GetAwaiter().GetResult();
        if (settings.CorrectionAxisMode != MLAstroRPA.Broker.BridgeAxisMode.Auto || settings.CorrectionOvershootEnabled || !settings.CorrectionOvershootUpEnabled || settings.CorrectionOvershootDownEnabled || settings.CorrectionOvershootUpArcMin != 7 || !settings.SoftwareReverseAzimuth)
            throw new Exception("Advanced correction settings did not round-trip");
        try {
            plugin.ConfigureCorrectionAsync(0.75, 120, 3, true, new NINA.Plugins.MLAstroRPA.CorrectionOptions(OvershootUpEnabled: true, OvershootDownEnabled: true)).GetAwaiter().GetResult();
            throw new Exception("Conflicting overshoot directions accepted");
        } catch (ArgumentException) { }
        plugin.ConfigureCorrectionAsync(0.75, 120, 3, true, new NINA.Plugins.MLAstroRPA.CorrectionOptions(
            AxisMode: "Both", OvershootEnabled: true, OvershootUpEnabled: false, OvershootDownEnabled: true,
            OvershootUpArcMin: 5, OvershootDownArcMin: 30, ReverseAzimuth: false)).GetAwaiter().GetResult();
        Console.WriteLine("PASS advanced correction options and conflicting direction validation");
        if (Environment.GetEnvironmentVariable("MLASTRO_SETTINGS_ONLY") == "1") {
            plugin.Teardown().GetAwaiter().GetResult();
            return 0;
        }
        if (Environment.GetEnvironmentVariable("MLASTRO_RECONNECT_ONLY") == "1") {
            using var firmware = new VirtualRpa();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            settings.MlaPort = 1892;
            if (!plugin.ConnectAsync(true, "127.0.0.1", deadline.Token).GetAwaiter().GetResult()) throw new Exception("Initial virtual connection failed");
            plugin.ReconnectAsync(deadline.Token).GetAwaiter().GetResult();
            if (!serial.IsConnected || !serial.IsDeviceIdle || !serial.HasFreshTelemetry || plugin.Runner.IsSessionRunning || settings.TppaBrokerEnabled || firmware.ManualCount != 0 || firmware.JogCount != 0 || firmware.AlignmentCount != 0)
                throw new Exception("Direct reconnect was not fresh, idle and motion-free");
            using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1893") };
            using var response = http.PostAsync("/reconnect", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), deadline.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode || !serial.IsConnected || !serial.IsDeviceIdle || !serial.HasFreshTelemetry || plugin.Runner.IsSessionRunning || settings.TppaBrokerEnabled || firmware.ManualCount != 0 || firmware.JogCount != 0 || firmware.AlignmentCount != 0)
                throw new Exception("Reconnect route was not fresh, idle and motion-free: " + (int)response.StatusCode);
            plugin.DisconnectAsync().GetAwaiter().GetResult();
            plugin.Teardown().GetAwaiter().GetResult();
            Console.WriteLine("PASS direct and HTTP reconnect against one virtual listener; no motion or automation");
            return 0;
        }
        UsbChecks.Metadata();
        using (var usbDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            UsbChecks.Serial(plugin, usbDeadline.Token).GetAwaiter().GetResult();
        using (var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1893") })
        {
            var status = http.GetStringAsync("/status").GetAwaiter().GetResult();
            if (!status.Contains("\"connected\":false")) throw new Exception("Unexpected control status");
            Console.WriteLine("PASS loopback control status");
            foreach (var item in new[] {
                ("/automation", "{\"enabled\":true}", 409),
                ("/connect", "{\"transport\":\"invalid\",\"address\":\"none\"}", 400),
                ("/connect", "{}", 400),
                ("/settings", "{\"factor\":0.5,\"maxStepArcMin\":120,\"settleSeconds\":2,\"pauseBeforeFirstCorrection\":true}", 200),
                ("/settings", "{\"factor\":2,\"maxStepArcMin\":0.5,\"settleSeconds\":2,\"pauseBeforeFirstCorrection\":true}", 400),
                ("/stop", "{}", 200),
                ("/unknown", "{}", 404) })
            {
                using var response = http.PostAsync(item.Item1, new StringContent(item.Item2, System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                if ((int)response.StatusCode != item.Item3) throw new Exception("Unexpected control result for " + item.Item1 + ": " + (int)response.StatusCode + " " + response.Content.ReadAsStringAsync().GetAwaiter().GetResult() + "; status=" + http.GetStringAsync("/status").GetAwaiter().GetResult());
                Console.WriteLine("PASS control route " + item.Item1 + " " + item.Item3);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "/status");
            request.Headers.Add("Origin", "http://untrusted.example");
            using var denied = http.Send(request);
            if ((int)denied.StatusCode != 403) throw new Exception("Cross-origin loopback request accepted");
            Console.WriteLine("PASS loopback origin restriction");
        }
        if (Environment.GetEnvironmentVariable("MLASTRO_SKIP_TPPA_CONTRACT") != "1") {
        var tppa = AssemblyLoadContext.Default.LoadFromAssemblyPath(Environment.GetEnvironmentVariable("TPPA_ASSEMBLY") ?? throw new InvalidOperationException("Set TPPA_ASSEMBLY to the installed TPPA DLL path."));
        foreach (var pair in new (string Tppa, Type Local)[] {
            ("BridgeCapabilitiesPayload", typeof(MLAstroRPA.Broker.BridgeCapabilities)),
            ("BridgeMeasurementPayload", typeof(MLAstroRPA.Broker.BridgeMeasurement)),
            ("BridgeAdjustmentRequestPayload", typeof(MLAstroRPA.Broker.BridgeAdjustmentRequest)),
            ("BridgeAdjustmentGrantPayload", typeof(MLAstroRPA.Broker.BridgeAdjustmentGrant)),
            ("BridgeMeasurementRequestPayload", typeof(MLAstroRPA.Broker.BridgeMeasurementRequest)),
            ("BridgeCompletionRequestPayload", typeof(MLAstroRPA.Broker.BridgeCompletionRequest)),
            ("BridgeSessionEndedPayload", typeof(MLAstroRPA.Broker.BridgeSessionEnded)) })
        {
            var remote = tppa.GetType("NINA.Plugins.PolarAlignment.Bridge." + pair.Tppa, true)!;
            foreach (var property in remote.GetProperties().Where(p => p.CanWrite))
            {
                var local = pair.Local.GetProperty(property.Name);
                if (local is null || local.PropertyType != property.PropertyType)
                    throw new Exception($"Bridge mismatch: {pair.Tppa}.{property.Name}");
            }
            Console.WriteLine("PASS installed TPPA payload " + pair.Tppa);
        }
        } else Console.WriteLine("SKIP installed TPPA reflection contract (MLASTRO_SKIP_TPPA_CONTRACT=1)");
        using (var firmware = new VirtualRpa())
        {
            settings.MlaPort = 1892;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (!plugin.ConnectAsync(true, "127.0.0.1", deadline.Token).GetAwaiter().GetResult()) throw new Exception("Simulated handshake failed");
            Console.WriteLine("PASS virtual Wi-Fi firmware handshake");
            Task.Delay(250, deadline.Token).GetAwaiter().GetResult();
            var device = plugin.GetDeviceSettings();
            if (device.azSteps != 77917 || device.altSteps != 1000 || device.speedLevel != 1) throw new Exception("Device settings replaced by host defaults");
            plugin.ConfigureDeviceAsync(new(2, true, 78000, 1001), deadline.Token).GetAwaiter().GetResult();
            if (firmware.Speed != 2 || firmware.AzBack != 78000) throw new Exception("Settings not sent to firmware");
            Console.WriteLine("PASS firmware-backed speed/backlash read, apply and readback");
            using (var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1893") }) {
                using var retired = http.PostAsync("/jog/start", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                if ((int)retired.StatusCode != 410 || firmware.JogCount != 0) throw new Exception("Retired jog route remained active");
                var diagnostics = http.GetStringAsync("/diagnostics").GetAwaiter().GetResult();
                if (diagnostics.Contains("127.0.0.1") || diagnostics.Contains("77917")) throw new Exception("Diagnostics leaked endpoint or device configuration");
                Console.WriteLine("PASS continuous jogging unavailable and redacted diagnostics route");
            }
            using var aligner = new MLAstroRPA.Broker.HardwareAligner(serial);
            firmware.PauseTelemetry = true;
            Task.Delay(2000, deadline.Token).GetAwaiter().GetResult();
            var beforeDispatch = firmware.AlignmentCount;
            var move = aligner.AlignAsync(new MLAstroRPA.Broker.CorrectionPlan {
                HasMove = true, MoveAzimuth = true, AzimuthMagnitudeArcMin = 0.5, AzimuthRight = true
            }, deadline.Token);
            Task.Delay(300, deadline.Token).GetAwaiter().GetResult();
            if (move.IsCompleted || firmware.AlignmentCount != beforeDispatch) throw new Exception("Correction dispatched without fresh telemetry");
            firmware.PauseTelemetry = false;
            firmware.AlignmentReceived.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            Task.Delay(250, deadline.Token).GetAwaiter().GetResult();
            if (firmware.AlignmentCount != beforeDispatch + 1) throw new Exception("Correction was replayed after telemetry resumed");
            Console.WriteLine("PASS stale dispatch waits for fresh telemetry and sends exactly one correction");
            Task.Delay(300, deadline.Token).GetAwaiter().GetResult();
            firmware.Status = "STOPPING";
            Task.Delay(1800, deadline.Token).GetAwaiter().GetResult();
            if (move.IsCompleted) throw new Exception("Backlash delay was mistaken for completion");
            Console.WriteLine("PASS delayed backlash/STOPPING does not complete correction");
            firmware.Status = "READY";
            if (!move.WaitAsync(deadline.Token).GetAwaiter().GetResult()) throw new Exception("Virtual move did not complete");
            Console.WriteLine("PASS ready telemetry completes correction");
            try { plugin.ManualMoveAsync("az", 1, 121).GetAwaiter().GetResult(); throw new Exception("Oversized manual step accepted"); }
            catch (ArgumentException) { Console.WriteLine("PASS oversized manual step rejected"); }
            plugin.ManualMoveAsync("alt", 1, 5).GetAwaiter().GetResult();
            try { plugin.ManualMoveAsync("az", 1, 10).GetAwaiter().GetResult(); throw new Exception("Concurrent manual step accepted"); }
            catch (InvalidOperationException) { Console.WriteLine("PASS concurrent manual step rejected"); }
            Task.Delay(250, deadline.Token).GetAwaiter().GetResult();
            if (Math.Abs(firmware.LastManualArcseconds - 5) > 1e-6) throw new Exception("Manual relative command did not preserve the five-arcsecond step");
            firmware.Status = "READY";
            while ((bool)plugin.GetStatus().GetType().GetProperty("manualMoveActive")!.GetValue(plugin.GetStatus())!) Task.Delay(20, deadline.Token).GetAwaiter().GetResult();
            Console.WriteLine("PASS bounded manual step completes on virtual firmware");
            RunBrokerCycle(plugin, broker, settings, firmware, deadline.Token).GetAwaiter().GetResult();
            if (externalSelected || settings.TppaBrokerEnabled) throw new Exception("Completed automated run left RPA selected or armed");
            var previousMoves = firmware.AlignmentCount;
            broker.Publish(new MLAstroRPA.Broker.BridgeEventMessage(MLAstroRPA.Broker.BridgeEnvelope.Create(
                "SessionState", "later-normal-tppa", Guid.NewGuid().ToString(), null, 1, "MLAstroRPA",
                new MLAstroRPA.Broker.BridgeSessionState { State = "Preparing" }))).GetAwaiter().GetResult();
            Task.Delay(250, deadline.Token).GetAwaiter().GetResult();
            if (plugin.Runner.IsSessionRunning || firmware.AlignmentCount != previousMoves) throw new Exception("Later normal TPPA session armed RPA");
            Console.WriteLine("PASS completed run disarms RPA and later normal TPPA cannot reuse it");
            plugin.SelectForTppaAsync().GetAwaiter().GetResult();
            plugin.StopAsync().GetAwaiter().GetResult();
            if (externalSelected || settings.TppaBrokerEnabled) throw new Exception("Failed start cleanup left selection enabled");
            Console.WriteLine("PASS stopped or failed start restores ordinary TPPA selection");
            firmware.Status = "MOVING";
            Task.Delay(250, deadline.Token).GetAwaiter().GetResult();
            plugin.StopAsync().GetAwaiter().GetResult();
            firmware.StopReceived.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            Console.WriteLine("PASS stop reaches virtual busy hardware");
            Task.Delay(250, deadline.Token).GetAwaiter().GetResult();
            plugin.DisconnectAsync().GetAwaiter().GetResult();
        }
        using (var recoveryFirmware = new VirtualRpa()) {
            using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!plugin.ConnectAsync(true, "127.0.0.1", recoveryDeadline.Token).GetAwaiter().GetResult() || serial.MotionWritesBlocked || !serial.IsDeviceIdle)
                throw new Exception("Wi-Fi stale-telemetry verified reconnect failed");
            plugin.DisconnectAsync().GetAwaiter().GetResult();
            Console.WriteLine("PASS verified Wi-Fi reconnect clears stale-telemetry fence");
        }
        plugin.Teardown().GetAwaiter().GetResult();
        Console.WriteLine("PASS plugin teardown");
        return 0;
    }

    private static async Task RunBrokerCycle(NINA.Plugins.MLAstroRPA.MLAstroPlugin plugin, RecordingBroker broker,
        MLAstroRPA.Settings.PluginSettings settings, VirtualRpa firmware, CancellationToken token)
    {
        const string session = "virtual-session";
        long sequence = 0;
        async Task Send(string kind, object payload) => await broker.Publish(new MLAstroRPA.Broker.BridgeEventMessage(
            MLAstroRPA.Broker.BridgeEnvelope.Create(kind, session, Guid.NewGuid().ToString(), null, Interlocked.Increment(ref sequence), "MLAstroRPA", payload)));
        async Task Wait(Func<bool> condition) { while (!condition()) await Task.Delay(20, token); }
        bool Has(string kind) => broker.Messages.Any(m => m.Topic == MLAstroRPA.Broker.BridgeContract.CommandTopic &&
            MLAstroRPA.Broker.BridgeEnvelope.FromJson(m.Content.ToString())?.Kind == kind);
        broker.OnPublished = async message => {
            if (message.Topic != MLAstroRPA.Broker.BridgeContract.CommandTopic) return;
            var envelope = MLAstroRPA.Broker.BridgeEnvelope.FromJson(message.Content.ToString());
            if (envelope?.Kind == "BeginAdjustment") await Send("AdjustmentGranted", new MLAstroRPA.Broker.BridgeAdjustmentGrant {
                MeasurementId = "sample-1", WindowId = "window-1", MaxWindowMs = 30000, SilenceTimeoutMs = 10000, GrantedAtUtc = DateTimeOffset.UtcNow
            });
        };
        settings.AutoPauseAfterReferenceSweep = false;
        settings.CorrectionOvershootEnabled = false;
        await plugin.SetAutomationAsync(true);
        await Send("Capabilities", new MLAstroRPA.Broker.BridgeCapabilities { InterfaceVersion = 1, SilenceTimeoutMs = 10000, SessionTimeoutSec = 60, HeartbeatMs = 2000 });
        await Send("SessionState", new MLAstroRPA.Broker.BridgeSessionState { State = "Preparing" });
        await Wait(() => plugin.Runner.IsSessionRunning);
        // Reference-sweep/camera work is an active TPPA session, but no RPA move.
        var stopsBeforeIdleGap = firmware.StopCount;
        firmware.PauseTelemetry = true;
        await Task.Delay(2400, token);
        if (!plugin.Runner.IsSessionRunning || plugin.Serial.MotionWritesBlocked || firmware.StopCount != stopsBeforeIdleGap)
            throw new Exception("Idle RPA telemetry gap aborted the TPPA reference sweep");
        try {
            plugin.ManualMoveAsync("az", 1, 5).GetAwaiter().GetResult();
            throw new Exception("Movement admitted during armed/stale reference sweep");
        } catch (InvalidOperationException) { }
        firmware.PauseTelemetry = false;
        await Wait(() => plugin.Serial.HasFreshTelemetry);
        Console.WriteLine("PASS idle telemetry gap preserves TPPA session and gates new movement");
        var initialMoves = firmware.AlignmentCount;
        await Send("Measurement", new MLAstroRPA.Broker.BridgeMeasurement {
            MeasurementId = "sample-1", SessionId = session, Status = "Valid", Northern = true,
            AzimuthErrorArcMin = 1, AltitudeErrorArcMin = 0, TotalErrorArcMin = 1, ToleranceArcMin = 0.2, TimestampUtc = DateTimeOffset.UtcNow
        });
        await Wait(() => firmware.AlignmentCount > initialMoves);
        await Task.Delay(250, token);
        firmware.Status = "STOPPING";
        await Task.Delay(1800, token);
        if (Has("RequestMeasurement")) throw new Exception("Capture requested before correction completed");
        Console.WriteLine("PASS full broker cycle holds capture during simulated backlash");
        firmware.Status = "READY";
        await Wait(() => Has("RequestMeasurement"));
        var measurement = broker.Messages.Where(m => m.Topic == MLAstroRPA.Broker.BridgeContract.CommandTopic)
            .Select(m => MLAstroRPA.Broker.BridgeEnvelope.FromJson(m.Content.ToString())).Last(e => e?.Kind == "RequestMeasurement")!
            .PayloadAs<MLAstroRPA.Broker.BridgeMeasurementRequest>();
        if (measurement.SettleSeconds != 2 || measurement.WindowId != "window-1") throw new Exception("Capture request lost settling/window information");
        Console.WriteLine("PASS full broker cycle requests settled capture after completion");
        await Send("Measurement", new MLAstroRPA.Broker.BridgeMeasurement {
            MeasurementId = "sample-2", SessionId = session, Status = "Valid", Northern = true,
            TotalErrorArcMin = 0.1, ToleranceArcMin = 0.2, ToleranceReached = true,
            AutoFinishConditionMet = true, ConsecutiveBelowTolerance = 2, TimestampUtc = DateTimeOffset.UtcNow
        });
        await Wait(() => Has("RequestCompletion"));
        Console.WriteLine("PASS verified in-tolerance measurement requests session completion");
        await Send("SessionEnded", new MLAstroRPA.Broker.BridgeSessionEnded { Reason = "Completed" });
        await Wait(() => !plugin.Runner.IsSessionRunning);
        if (settings.TppaBrokerEnabled) throw new Exception("Session completion did not disarm controller");
        broker.OnPublished = null;
    }
}

public class ValueProxy : DispatchProxy
{
    public Dictionary<string, object> Values { get; } = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (Values.TryGetValue(method!.Name, out var value)) return value;
        if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
        throw new NotSupportedException(method.Name);
    }
}

sealed class RecordingBroker : NINA.Plugin.Interfaces.IMessageBroker
{
    public System.Collections.Concurrent.ConcurrentQueue<NINA.Plugin.Interfaces.IMessage> Messages { get; } = new();
    private readonly Dictionary<string, List<NINA.Plugin.Interfaces.ISubscriber>> subscriptions = new();
    public Func<NINA.Plugin.Interfaces.IMessage, Task>? OnPublished;
    public async Task Publish(NINA.Plugin.Interfaces.IMessage message) {
        Messages.Enqueue(message);
        NINA.Plugin.Interfaces.ISubscriber[] receivers;
        lock (subscriptions) receivers = subscriptions.TryGetValue(message.Topic, out var entries) ? entries.ToArray() : [];
        foreach (var receiver in receivers) await receiver.OnMessageReceived(message);
        if (OnPublished != null) await OnPublished(message);
    }
    public Task Publish(NINA.Plugin.Interfaces.IMessage message, CancellationToken token) => Publish(message);
    public void Subscribe(string topic, NINA.Plugin.Interfaces.ISubscriber subscriber) { lock (subscriptions) { if (!subscriptions.TryGetValue(topic, out var entries)) subscriptions[topic] = entries = new(); entries.Add(subscriber); } }
    public void Unsubscribe(string topic, NINA.Plugin.Interfaces.ISubscriber subscriber) { lock (subscriptions) { if (subscriptions.TryGetValue(topic, out var entries)) entries.Remove(subscriber); } }
}
