using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Pins.MLAstro.Connection;

static void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
static async Task Fails<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); } catch (T) { Console.WriteLine("PASS " + name); return; }
    throw new Exception("Expected failure: " + name);
}
Check(DeviceDiagnostics.ParseFirmware("{\"fw_ver\":\"firmware 1.13.0\"}", "Wi-Fi")?.Firmware == "firmware 1.13.0", "real firmware shape");
Check(DeviceDiagnostics.ParseFirmware("{\"status\":\"READY\"}", "Wi-Fi") is null, "partial telemetry does not erase version");
await Fails<JsonException>(() => Task.Run(() => DeviceDiagnostics.ParseFirmware("invalid", "Wi-Fi")), "malformed telemetry");
await Fails<IOException>(() => Task.Run(() => DeviceDiagnostics.ParseFirmware("{\"cmd\":\"connectionRejected\"}", "Wi-Fi")), "occupied device rejected");
using (var fragmented = new ScriptedSocket([("{\"fw_", false), ("ver\":\"1.13.0\"}", true)]))
    Check(DeviceDiagnostics.ParseFirmware(await DeviceDiagnostics.ReceiveMessageAsync(fragmented, default), "Wi-Fi")?.Firmware == "1.13.0", "fragment assembly");
using (var oversized = new ScriptedSocket(Enumerable.Repeat((new string('a', 4096), false), 17).ToArray()))
    await Fails<IOException>(() => DeviceDiagnostics.ReceiveMessageAsync(oversized, default), "bounded message size");
using (var closed = new ScriptedSocket([]))
    await Fails<IOException>(() => DeviceDiagnostics.ReceiveMessageAsync(closed, default), "disconnect does not hang");
using (var cancelled = new ScriptedSocket([]))
    await Fails<OperationCanceledException>(() => DeviceDiagnostics.ReceiveMessageAsync(cancelled, new CancellationToken(true)), "cancellation");

sealed class ScriptedSocket((string Text, bool End)[] frames) : WebSocket
{
    private int position;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => WebSocketState.Open;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override void Dispose() { }
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => throw new Exception("Diagnostic must not send commands");
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (position == frames.Length) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        var frame = frames[position++];
        var bytes = Encoding.UTF8.GetBytes(frame.Text);
        bytes.AsSpan().CopyTo(buffer.AsSpan());
        return Task.FromResult(new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, frame.End));
    }
}
