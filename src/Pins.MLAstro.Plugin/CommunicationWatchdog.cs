using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.MLAstroRPA;

public sealed record WatchdogResult(bool Supported, bool? Enabled, string Code);

/// <summary>Uses its own short-lived socket; it does not manage the application's connection.</summary>
public sealed class CommunicationWatchdog
{
    private readonly Func<CancellationToken, Task<WebSocket>> connect;
    private const int MaxMessage = 64 * 1024;

    public CommunicationWatchdog(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host)) host = "MLAstroRPA.local";
        if (port <= 0) port = 80;
        // UriBuilder rejects invalid host/port without accepting a supplied URL or path.
        if (host.Contains('/') || host.Contains('\\') || host.Contains('@') || host.Contains('?') || host.Contains('#') || port > 65535)
            throw new ArgumentException("Invalid device endpoint.");
        Uri uri;
        try { uri = new UriBuilder("ws", host, port, "/ws").Uri; }
        catch (Exception ex) when (ex is UriFormatException || ex is ArgumentException) { throw new ArgumentException("Invalid device endpoint."); }
        connect = async token => { var socket = new ClientWebSocket(); try { await socket.ConnectAsync(uri, token); return socket; } catch { socket.Dispose(); throw; } };
    }

    public CommunicationWatchdog(Func<CancellationToken, Task<WebSocket>> connector) => connect = connector ?? throw new ArgumentNullException(nameof(connector));

    public async Task<WatchdogResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        WebSocket socket;
        try { socket = await connect(deadline.Token); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(false, null, "unavailable"); }
        using (socket)
        {
            try { return await ReadSocket(socket, deadline.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, null, "unsupported"); }
            catch (OperationCanceledException) { throw; }
            catch { return new(false, null, "unavailable"); }
        }
    }

    public async Task<WatchdogResult> SetAsync(bool enabled, CancellationToken cancellationToken = default, Func<bool>? authorize = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(7.5));
        try
        {
            using (var socket = await connect(deadline.Token))
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { cmd = "setCommWatchdog", data = new { enabled } }));
                deadline.Token.ThrowIfCancellationRequested();
                if (authorize != null && !authorize()) return new(false, null, "not_sent");
                deadline.Token.ThrowIfCancellationRequested();
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, deadline.Token);
                using var ackDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                ackDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                while (true)
                {
                    using var doc = await Receive(socket, ackDeadline.Token);
                    if (doc == null) return new(false, null, "unavailable");
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "commWatchdogUpdated")
                    {
                        if (!root.TryGetProperty("enabled", out var actual) || (actual.ValueKind != JsonValueKind.True && actual.ValueKind != JsonValueKind.False) || actual.GetBoolean() != enabled)
                            return new(true, null, "ack_mismatch");
                        break;
                    }
                }
            }
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            readDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            var result = await ReadFresh(readDeadline.Token);
            if (!result.Supported || result.Enabled != enabled) return new(result.Supported, result.Enabled, "verification_mismatch");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, null, "unavailable"); }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, null, "unavailable"); }
    }

    private async Task<WatchdogResult> ReadFresh(CancellationToken token)
    {
        using var socket = await connect(token);
        return await ReadSocket(socket, token);
    }

    private static async Task<WatchdogResult> ReadSocket(WebSocket socket, CancellationToken token)
    {
        while (true)
        {
            using var doc = await Receive(socket, token);
            if (doc == null) return new(false, null, "unsupported");
            if (FindWatchdog(doc.RootElement, out var value)) return new(true, value, "ok");
        }
    }

    private static bool FindWatchdog(JsonElement element, out bool value)
    {
        value = false;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("serial", out var serial) && serial.ValueKind == JsonValueKind.Object && serial.TryGetProperty("watchdog", out var watchdog) && (watchdog.ValueKind == JsonValueKind.True || watchdog.ValueKind == JsonValueKind.False)) { value = watchdog.GetBoolean(); return true; }
            foreach (var child in element.EnumerateObject()) if (FindWatchdog(child.Value, out value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) if (FindWatchdog(child, out value)) return true;
        return false;
    }

    private static async Task<JsonDocument?> Receive(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > MaxMessage) throw new InvalidDataException();
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }
}


