using System.Net.WebSockets;
using System.Text;
using NINA.Plugins.MLAstroRPA;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static Func<CancellationToken, Task<WebSocket>> Connector(params FakeSocket[] sockets) { var q = new Queue<FakeSocket>(sockets); return _ => Task.FromResult<WebSocket>(q.Dequeue()); }
var fragmented = new FakeSocket(("{\"device\":{\"ser", false), ("ial\":{\"watchdog\":true}}}", true));
var read = await new CommunicationWatchdog(Connector(fragmented)).ReadAsync();
Check(read.Supported && read.Enabled == true, "fragmented state");
var unsupported = await new CommunicationWatchdog(Connector(new FakeSocket(("{\"serial\":{}}", true)))).ReadAsync();
Check(!unsupported.Supported && unsupported.Enabled == null && unsupported.Code == "unsupported", "unsupported must not become off");
var failedConnection = await new CommunicationWatchdog(_ => Task.FromException<WebSocket>(new InvalidOperationException("secret host"))).ReadAsync();
Check(failedConnection.Code == "unavailable" && failedConnection.Enabled == null, "connection failure is unavailable");
var timed = await new CommunicationWatchdog(_ => Task.FromResult<WebSocket>(new SilentSocket())).ReadAsync();
Check(!timed.Supported && timed.Enabled == null && timed.Code == "unsupported", "silent device timeout");
var send = new FakeSocket(("{\"status\":\"commWatchdogUpdated\",\"enabled\":true}", true));
var set = await new CommunicationWatchdog(Connector(send, new FakeSocket(("{\"serial\":{\"watchdog\":true}}", true)))).SetAsync(true);
Check(set.Code == "ok" && send.Sent.Contains("setCommWatchdog"), "ack and fresh read");
var refusedSocket = new FakeSocket();
var refused = await new CommunicationWatchdog(Connector(refusedSocket)).SetAsync(true, CancellationToken.None, () => false);
Check(refused.Code == "not_sent" && refusedSocket.Sent.Length == 0, "authorization refusal sends nothing");
var badAck = await new CommunicationWatchdog(Connector(new FakeSocket(("{\"status\":\"commWatchdogUpdated\",\"enabled\":false}", true)))).SetAsync(true);
Check(badAck.Code == "ack_mismatch" && badAck.Enabled == null, "ack mismatch");
var badRead = await new CommunicationWatchdog(Connector(new FakeSocket(("{\"status\":\"commWatchdogUpdated\",\"enabled\":true}", true)), new FakeSocket(("{\"serial\":{\"watchdog\":false}}", true)))).SetAsync(true);
Check(badRead.Code == "verification_mismatch" && badRead.Enabled == false, "readback mismatch");
var secret = "SSID-private C:\\Users\\alice secret-token";
var input = new DiagnosticsInput(secret, "10.0.0", secret, DiagnosticTransport.Wifi, true, DiagnosticFault.Transport, DiagnosticReadiness.NotReady, DiagnosticAutomation.Idle, false, true, Enumerable.Range(0, 60).Select(_ => new DiagnosticEvent(DateTimeOffset.UnixEpoch, DiagnosticEventCode.Fault)).ToArray());
var json = DiagnosticsReport.Capture(input);
Check(!json.Contains("SSID") && !json.Contains("alice") && !json.Contains("secret") && json.Contains("\"enabled\":null"), "privacy allowlist");
Check(System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("events").GetArrayLength() == 50, "bounded events");
foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 86400001d })
{
    var unsafeInput = input with { FirmwareVersion = secret, PinsVersion = "1.2.3", TppaVersion = secret, RelayVersion = secret, TelemetryAgeMilliseconds = invalid, LongestTelemetryGapMilliseconds = invalid };
    var sanitized = DiagnosticsReport.Capture(unsafeInput);
    using var parsed = System.Text.Json.JsonDocument.Parse(sanitized);
    Check(parsed.RootElement.GetProperty("telemetryAgeMilliseconds").ValueKind == System.Text.Json.JsonValueKind.Null && parsed.RootElement.GetProperty("longestTelemetryGapMilliseconds").ValueKind == System.Text.Json.JsonValueKind.Null, "invalid durations rejected");
    Check(!sanitized.Contains("secret") && parsed.RootElement.GetProperty("pinsVersion").GetString() == "1.2.3", "versions sanitized");
}
Console.WriteLine("Support checks passed");

sealed class FakeSocket : WebSocket
{
    private readonly Queue<(string text, bool end)> frames;
    public string Sent { get; private set; } = "";
    public FakeSocket(params (string, bool)[] frames) => this.frames = new(frames);
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override void Dispose() { }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
    {
        if (frames.Count == 0) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        var (text, end) = frames.Dequeue(); var bytes = Encoding.UTF8.GetBytes(text); bytes.CopyTo(buffer.Array!, buffer.Offset);
        return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, end));
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) { Sent = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count); return Task.CompletedTask; }
}

sealed class SilentSocket : WebSocket
{
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override void Dispose() { }
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => Task.CompletedTask;
}


