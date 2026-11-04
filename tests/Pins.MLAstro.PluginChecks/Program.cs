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
        UsbChecks.Metadata();
        using (var usbDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            UsbChecks.Serial(plugin, usbDeadline.Token).GetAwaiter().GetResult();
        var fallbackStops = 0;
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new NINA.Plugins.MLAstroRPA.JogLease(
            (_, _, _) => hung.Task,
            (_, _, _) => Task.CompletedTask,
            (_, _, _) => { Interlocked.Increment(ref fallbackStops); return Task.CompletedTask; });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { stalled.StartAsync("az", 1).GetAwaiter().GetResult(); throw new Exception("Stalled transport accepted"); }
        catch (TimeoutException) { }
        if (watch.ElapsedMilliseconds > 2000 || fallbackStops != 1 || stalled.Active) throw new Exception("Stalled send trapped stop");
        stalled.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("PASS stalled transport cannot trap jog stop indefinitely");
        using (var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1893") })
        {
            var status = http.GetStringAsync("/status").GetAwaiter().GetResult();
            if (!status.Contains("\"connected\":false")) throw new Exception("Unexpected control status");
            Console.WriteLine("PASS loopback control status");
            foreach (var item in new[] {
                ("/automation", "{\"enabled\":true}", 409),
                ("/connect", "{\"transport\":\"invalid\",\"address\":\"none\"}", 400),
                ("/connect", "{}", 400),
                ("/settings", "{\"factor\":0.5,\"maxStepArcMin\":0.5,\"settleSeconds\":2,\"pauseBeforeFirstCorrection\":true}", 200),
                ("/settings", "{\"factor\":2,\"maxStepArcMin\":0.5,\"settleSeconds\":2,\"pauseBeforeFirstCorrection\":true}", 400),
                ("/stop", "{}", 200),
                ("/unknown", "{}", 404) })
            {
                using var response = http.PostAsync(item.Item1, new StringContent(item.Item2, System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                if ((int)response.StatusCode != item.Item3) throw new Exception("Unexpected control result for " + item.Item1);
                Console.WriteLine("PASS control route " + item.Item1 + " " + item.Item3);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "/status");
            request.Headers.Add("Origin", "http://untrusted.example");
            using var denied = http.Send(request);
            if ((int)denied.StatusCode != 403) throw new Exception("Cross-origin loopback request accepted");
            Console.WriteLine("PASS loopback origin restriction");
        }
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
        using (var firmware = new VirtualRpa())
        {
            settings.MlaPort = 1892;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            if (!plugin.ConnectAsync(true, "127.0.0.1", deadline.Token).GetAwaiter().GetResult()) throw new Exception("Simulated handshake failed");
            Console.WriteLine("PASS virtual Wi-Fi firmware handshake");
            Task.Delay(250, deadline.Token).GetAwaiter().GetResult();
            var device = plugin.GetDeviceSettings();
            if (device.azSteps != 77917 || device.altSteps != 1000 || device.speedLevel != 1) throw new Exception("Device settings replaced by host defaults");
            plugin.ConfigureDeviceAsync(new(2, true, 78000, 1001), deadline.Token).GetAwaiter().GetResult();
            if (firmware.Speed != 2 || firmware.AzBack != 78000) throw new Exception("Settings not sent to firmware");
            Console.WriteLine("PASS firmware-backed speed/backlash read, apply and readback");
            using (var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1893") }) {
                using var bad = http.PostAsync("/device-settings", new StringContent("{\"speedLevel\":6,\"backlashEnabled\":true,\"azSteps\":1,\"altSteps\":1}", System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                if ((int)bad.StatusCode != 400) throw new Exception("Invalid speed accepted");
                using var started = http.PostAsync("/jog/start", new StringContent("{\"axis\":\"az\",\"direction\":1}", System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                started.EnsureSuccessStatusCode();
                using var body = System.Text.Json.JsonDocument.Parse(started.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                var lease = body.RootElement.GetProperty("leaseId").GetString()!;
                try { plugin.ManualMoveAsync("az", 1, 5).GetAwaiter().GetResult(); throw new Exception("Step accepted during jog"); } catch (InvalidOperationException) { }
                try { plugin.SetAutomationAsync(true).GetAwaiter().GetResult(); throw new Exception("Automation accepted during jog"); } catch (InvalidOperationException) { }
                for (int i = 0; i < 6; i++) { Task.Delay(150, deadline.Token).GetAwaiter().GetResult(); plugin.RenewJogAsync(lease).GetAwaiter().GetResult(); }
                if (firmware.JogCount != 1 || firmware.JogStops != 0) throw new Exception("Wi-Fi jog restarted or stopped while renewing");
                Console.WriteLine("PASS renewed jog stays active and blocks conflicting operations");
                Task.Delay(1200, deadline.Token).GetAwaiter().GetResult();
                if (firmware.JogStops != 1) throw new Exception("Lost browser lease did not stop firmware");
                try { plugin.RenewJogAsync(lease).GetAwaiter().GetResult(); throw new Exception("Expired lease revived"); } catch (InvalidOperationException) { }
                Console.WriteLine("PASS lost browser heartbeat stops jog and cannot be revived");
                Task.Delay(200, deadline.Token).GetAwaiter().GetResult();
                var next = plugin.StartJogAsync("alt", -1).GetAwaiter().GetResult();
                plugin.StopJogAsync(lease).GetAwaiter().GetResult();
                if (firmware.JogStops != 1) throw new Exception("Old lease stopped new jog");
                plugin.StopJogAsync(next).GetAwaiter().GetResult();
                Task.Delay(250, deadline.Token).GetAwaiter().GetResult();
                if (firmware.JogStops != 2) throw new Exception("Button release did not stop jog");
                Console.WriteLine("PASS explicit release stops jog; old leases cannot stop a newer jog");
            }
            using var aligner = new MLAstroRPA.Broker.HardwareAligner(serial);
            var move = aligner.AlignAsync(new MLAstroRPA.Broker.CorrectionPlan {
                HasMove = true, MoveAzimuth = true, AzimuthMagnitudeArcMin = 0.5, AzimuthRight = true
            }, deadline.Token);
            firmware.AlignmentReceived.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
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
            plugin.DisconnectAsync().GetAwaiter().GetResult();
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
