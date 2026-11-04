using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>Loopback-only firmware simulator. No real device address can be supplied.</summary>
sealed class VirtualRpa : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly Task worker;
    private WebSocket? socket;
    public volatile string Status = "READY";
    public int AlignmentCount;
    public int JogCount, JogStops;
    public int Speed = 1, AzBack = 77917, AltBack = 1000;
    public bool Backlash = true;
    public double LastManualArcseconds;
    public TaskCompletionSource AlignmentReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StopReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public VirtualRpa()
    {
        listener.Prefixes.Add("http://127.0.0.1:1892/");
        listener.Start();
        worker = RunAsync();
    }
    private async Task SendAsync(object message)
    {
        await sendGate.WaitAsync(shutdown.Token);
        try { await socket!.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, shutdown.Token); }
        finally { sendGate.Release(); }
    }
    private async Task RunAsync()
    {
        Task? telemetry = null;
        try
        {
            var context = await listener.GetContextAsync().WaitAsync(shutdown.Token);
            socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await SendAsync(new { fw_ver = "firmware 1.13.0", speedLevel = Speed, backlash = new { enable = Backlash, az_steps = AzBack, alt_steps = AltBack }, role = "master", serial_locked = false });
            telemetry = Task.Run(async () => {
                while (!shutdown.IsCancellationRequested) {
                    await SendAsync(new { sys_status = Status, pos_az = 0.0, pos_alt = 0.0 });
                    await Task.Delay(100, shutdown.Token);
                }
            });
            var buffer = new byte[8192];
            while (!shutdown.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, shutdown.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                var command = doc.RootElement.GetProperty("cmd").GetString();
                switch (command)
                {
                    case "handshake": await SendAsync(new { cmd = "handshakeResult", result = true, fw_ver = "firmware 1.13.0", link = "STA" }); break;
                    case "align": Status = "MOVING"; Interlocked.Increment(ref AlignmentCount); AlignmentReceived.TrySetResult(); break;
                    case "moveRelative": LastManualArcseconds = doc.RootElement.GetProperty("data").GetProperty("angle").GetDouble() * 3600; Status = "MOVING"; break;
                    case "stop": case "forceStop": Status = "READY"; StopReceived.TrySetResult(); break;
                    case "move": Status = "MOVING"; Interlocked.Increment(ref JogCount); break;
                    case "stopMove": Status = "READY"; Interlocked.Increment(ref JogStops); break;
                    case "speedLevel": Speed = doc.RootElement.GetProperty("data").GetProperty("level").GetInt32(); await SendAsync(new { speedLevel = Speed }); break;
                    case "applyConfig":
                        var back = doc.RootElement.GetProperty("data").GetProperty("backlash");
                        Backlash = back.GetProperty("enable").GetBoolean(); AzBack = back.GetProperty("az_steps").GetInt32(); AltBack = back.GetProperty("alt_steps").GetInt32();
                        await SendAsync(new { status = "configApplied" });
                        await SendAsync(new { backlash = new { enable = Backlash, az_steps = AzBack, alt_steps = AltBack } }); break;
                    case "saveConfig": await SendAsync(new { status = "configSaved" }); break;
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (WebSocketException) { }
        finally {
            shutdown.Cancel();
            if (telemetry != null) { try { await telemetry; } catch (OperationCanceledException) { } catch (WebSocketException) { } }
        }
    }
    public void Dispose()
    {
        shutdown.Cancel();
        socket?.Abort();
        listener.Close();
        worker.GetAwaiter().GetResult();
        socket?.Dispose();
        shutdown.Dispose();
    }
}
