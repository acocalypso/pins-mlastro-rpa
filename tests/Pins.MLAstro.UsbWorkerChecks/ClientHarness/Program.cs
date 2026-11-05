using System.Diagnostics;
using System.Text.Json;
using MLAstroRPA.Services;

if (args.Length != 2 || !OperatingSystem.IsLinux() || !args[1].StartsWith("/dev/pts/", StringComparison.Ordinal))
    throw new ArgumentException("Linux, a worker executable, and a private PTY are required.");
Environment.SetEnvironmentVariable("MLASTRO_USB_WORKER", args[0]);
var client = new UsbWorkerClient();
long lastFrame = 0;
client.LineReceived += (_, stamp) => Interlocked.Exchange(ref lastFrame, stamp);
client.Faulted += reason => Console.WriteLine(JsonSerializer.Serialize(new { type = "fault", reason }));
try {
    if (!client.ConnectAsync(args[1], CancellationToken.None).GetAwaiter().GetResult()) throw new IOException("Worker open failed.");
    Console.WriteLine("{\"type\":\"opened\"}");
    while (Console.ReadLine() is string line) {
        using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
        var op = r.GetProperty("op").GetString(); bool ok = true;
        switch (op) {
            case "send": ok = client.SendAsync(r.GetProperty("text").GetString()!, r.GetProperty("motion").GetBoolean(), CancellationToken.None).GetAwaiter().GetResult(); break;
            case "allow": ok = client.AllowMotion(); break;
            case "fence": client.Fence(); break;
            case "status": break;
            case "close": client.CloseAsync().GetAwaiter().GetResult(); break;
            default: throw new InvalidDataException("Unsupported harness command.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { type = "result", op, ok, connected = client.IsConnected, ready = client.ReadyToMove,
            frameAgeMs = lastFrame == 0 ? -1 : Stopwatch.GetElapsedTime(Volatile.Read(ref lastFrame)).TotalMilliseconds }));
        if (op == "close") break;
    }
} finally { client.CloseAsync().GetAwaiter().GetResult(); }
