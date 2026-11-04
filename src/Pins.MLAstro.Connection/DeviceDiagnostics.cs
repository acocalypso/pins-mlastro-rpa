using System.IO.Ports;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Pins.MLAstro.Connection;

public sealed record FirmwareInfo(string Firmware, string Transport);

/// <summary>Bounded read-only probes; no handshake, movement or configuration commands.</summary>
public static class DeviceDiagnostics
{
    public const int MaximumMessageBytes = 65536;

    public static FirmwareInfo? ParseFirmware(string message, string transport)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("cmd", out var command) && command.GetString() == "connectionRejected")
            throw new IOException("RPA rejected the diagnostic connection; another client may own it.");
        if (!root.TryGetProperty("fw_ver", out var version) || version.ValueKind != JsonValueKind.String)
            return null;
        var value = version.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : new(value, transport);
    }

    public static async Task<string> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("RPA closed the connection before sending firmware information.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new IOException("Expected text telemetry from RPA.");
            if (message.Length + result.Count > MaximumMessageBytes)
                throw new IOException("RPA telemetry exceeded the message size limit.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return new UTF8Encoding(false, true).GetString(message.ToArray());
        }
    }

    public static async Task<FirmwareInfo> ProbeWifiAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.Scheme != "ws" && endpoint.Scheme != "wss")
            throw new ArgumentException("Use a ws:// or wss:// endpoint.", nameof(endpoint));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(endpoint, cancellationToken);
        while (true)
        {
            var info = ParseFirmware(await ReceiveMessageAsync(socket, cancellationToken), "Wi-Fi");
            if (info is not null) return info;
        }
    }

    public static async Task<FirmwareInfo> ProbeUsbAsync(string device, CancellationToken cancellationToken)
    {
        // A worker plus short read timeout makes cancellation portable across serial implementations.
        return await Task.Run(() =>
        {
            using var port = new SerialPort(device, 115200, Parity.None, 8, StopBits.One)
            { DtrEnable = false, RtsEnable = false, ReadTimeout = 200, WriteTimeout = 1000, NewLine = "\n" };
            cancellationToken.ThrowIfCancellationRequested();
            port.Open();
            port.Write("?\n");
            var line = new StringBuilder();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int next;
                try { next = port.ReadChar(); }
                catch (TimeoutException) { continue; }
                if (next < 0) throw new IOException("Serial device disconnected.");
                if (next == '\n')
                {
                    var text = line.ToString().Trim();
                    line.Clear();
                    // The documented status reply does not include firmware. Do not invent a
                    // version or acquire control merely to discover one.
                    if (text.StartsWith('<') && text.Contains("|Mpos:") && text.Contains("|>"))
                        return new FirmwareInfo("version unavailable in read-only serial telemetry", "USB");
                }
                else
                {
                    if (line.Length >= MaximumMessageBytes) throw new IOException("Serial line exceeded limit.");
                    line.Append((char)next);
                }
            }
        }, cancellationToken);
    }
}
