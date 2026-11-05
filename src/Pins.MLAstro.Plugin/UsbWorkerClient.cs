using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MLAstroRPA.Services;

/// <summary>Owns one isolated USB transport process; uncertain writes fence the session.</summary>
public sealed class UsbWorkerClient
{
    private sealed class Session
    {
        public readonly Process Process;
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly SemaphoreSlim Pipe = new(1, 1);
        public readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> Pending = new();
        public readonly TaskCompletionSource<bool> Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Shutdown = new();
        public Task Reader = Task.CompletedTask, ErrorReader = Task.CompletedTask, Close = Task.CompletedTask;
        public Thread Heartbeat;
        public int Writes, Faulted, Closing;
        public bool MotionReady;
        public long NextId;
        public Session(Process process) { Process = process; }
    }

    private readonly object gate = new();
    private Session current;
    private long generation;
    private bool fenced = true;
    private Task pendingFence = Task.CompletedTask;
    public event Action<string, long> LineReceived;
    public event Action<string> Faulted;
    public string Path { get; private set; }
    public string PortName => Path;
    public bool ReadyToMove { get { lock (gate) return current != null && current.MotionReady && Volatile.Read(ref current.Faulted) == 0; } }
    public bool IsConnected { get { lock (gate) return current != null && !current.Process.HasExited && current.Opened.Task.IsCompletedSuccessfully && current.Opened.Task.Result && Volatile.Read(ref current.Closing) == 0; } }
    public bool WritesQuiescent { get { lock (gate) return current == null || (Volatile.Read(ref current.Writes) == 0 && current.Pending.IsEmpty && (Volatile.Read(ref current.Closing) == 0 || current.Close.IsCompleted)); } }

    public async Task<bool> ConnectAsync(string port, CancellationToken token)
    {
        Session s;
        lock (gate) {
            if (current != null) throw new InvalidOperationException("Previous USB worker has not been closed and drained.");
            var exe = Environment.GetEnvironmentVariable("MLASTRO_USB_WORKER");
            if (string.IsNullOrWhiteSpace(exe)) {
                var pluginDirectory = System.IO.Path.GetDirectoryName(typeof(UsbWorkerClient).Assembly.Location) ?? AppContext.BaseDirectory;
                exe = System.IO.Path.Combine(pluginDirectory, "usb-worker", "Pins.MLAstro.UsbWorker");
                if (OperatingSystem.IsWindows()) exe += ".exe";
            }
            var process = new Process { StartInfo = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            s = new Session(process);
            process.StartInfo.ArgumentList.Add("--port"); process.StartInfo.ArgumentList.Add(port);
            process.StartInfo.ArgumentList.Add("--parent"); process.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            process.StartInfo.ArgumentList.Add("--session"); process.StartInfo.ArgumentList.Add(s.Id);
            if (!process.Start()) throw new IOException("USB worker could not start.");
            current = s; Path = port; fenced = true; generation++;
            s.Reader = ReadAsync(s); s.ErrorReader = DrainErrorsAsync(s);
            s.Heartbeat = new Thread(() => Heartbeat(s)) { IsBackground = true, Name = "MLAstro USB heartbeat" };
            s.Heartbeat.Start();
        }
        try {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            if (!await s.Opened.Task.WaitAsync(deadline.Token).ConfigureAwait(false)) throw new IOException("USB worker did not open the port.");
            Fence();
            await pendingFence.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (Volatile.Read(ref s.Faulted) != 0) throw new IOException("USB worker fence failed.");
            return true;
        } catch { await CloseSessionAsync(s).ConfigureAwait(false); throw; }
    }

    public async Task<bool> SendAsync(string text, bool motion, CancellationToken token)
    {
        Session s; long g; Task fenceTask;
        lock (gate) { s = current; g = generation; fenceTask = pendingFence; if (s == null || (motion && fenced)) return false; }
        var expires = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        try { await fenceTask.WaitAsync(TimeSpan.FromMilliseconds(750), token).ConfigureAwait(false); }
        catch { Fail(s, "USB fence did not complete before the command."); return false; }
        lock (gate) if (!ReferenceEquals(s, current) || generation != g || (motion && fenced)) return false;
        return await RequestAsync(s, new { type = "send", session = s.Id, generation = g, text, expiresAt = expires }, token, true).ConfigureAwait(false);
    }

    public void Fence()
    {
        Session s; long g;
        lock (gate) {
            fenced = true; g = ++generation; s = current;
            if (s != null) {
                var previous = pendingFence;
                pendingFence = Task.Run(async () => { await previous.ConfigureAwait(false); await FenceAsync(s, g).ConfigureAwait(false); });
            }
        }
    }
    private async Task FenceAsync(Session s, long g)
    {
        try { if (!await RequestAsync(s, new { type = "fence", session = s.Id, generation = g }, CancellationToken.None, true).ConfigureAwait(false)) Fail(s, "USB fence was not acknowledged."); }
        catch { Fail(s, "USB fence failed."); }
    }

    public bool AllowMotion()
    {
        Session s; long g; Task fenceTask;
        lock (gate) { s = current; g = generation; fenceTask = pendingFence; if (s == null || !fenced) return s != null; }
        bool ok;
        try { fenceTask.WaitAsync(TimeSpan.FromMilliseconds(750)).GetAwaiter().GetResult(); ok = RequestAsync(s, new { type = "allow", session = s.Id, generation = g }, CancellationToken.None, true).GetAwaiter().GetResult(); }
        catch { ok = false; }
        lock (gate) {
            if (!ok || !ReferenceEquals(s, current) || generation != g || Volatile.Read(ref s.Closing) != 0) return false;
            fenced = false; return true;
        }
    }

    private async Task<bool> RequestAsync(Session s, object message, CancellationToken token, bool failOnTimeout)
    {
        var id = Interlocked.Increment(ref s.NextId);
        var payload = JsonSerializer.SerializeToElement(message);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject(); foreach (var p in payload.EnumerateObject()) p.WriteTo(writer);
            writer.WriteNumber("id", id); writer.WriteEndObject();
        }
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Pending[id] = ack;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, s.Shutdown.Token);
        deadline.CancelAfter(500);
        try {
            if (!await WriteAsync(s, json, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false)) throw new IOException("USB worker pipe unavailable.");
            var ok = await ack.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            return ok;
        } catch {
            if (failOnTimeout && !s.Shutdown.IsCancellationRequested) Fail(s, "USB worker request was not confirmed.");
            return false;
        } finally { s.Pending.TryRemove(id, out _); }
    }

    private async Task<bool> WriteAsync(Session s, string json, CancellationToken token)
    {
        Interlocked.Increment(ref s.Writes);
        try {
            await s.Pipe.WaitAsync(token).ConfigureAwait(false);
            try {
                if (token.IsCancellationRequested || Volatile.Read(ref s.Closing) != 0 || s.Process.HasExited) return false;
                await s.Process.StandardInput.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
                await s.Process.StandardInput.FlushAsync(token).ConfigureAwait(false);
                return true;
            } finally { s.Pipe.Release(); }
        } catch { return false; }
        finally { Interlocked.Decrement(ref s.Writes); }
    }

    private void Heartbeat(Session s)
    {
        while (!s.Shutdown.IsCancellationRequested) {
            try {
                // This thread resumes directly after a host GC pause. A delayed pool
                // continuation must not falsely poison an otherwise healthy idle worker.
                if (s.Pipe.Wait(50)) {
                    Interlocked.Increment(ref s.Writes);
                    try {
                        if (s.Shutdown.IsCancellationRequested) return;
                        s.Process.StandardInput.WriteLine(JsonSerializer.Serialize(new { type = "ping", session = s.Id }));
                        s.Process.StandardInput.Flush();
                    } finally { Interlocked.Decrement(ref s.Writes); s.Pipe.Release(); }
                }
                if (s.Shutdown.Token.WaitHandle.WaitOne(250)) return;
            } catch { if (!s.Shutdown.IsCancellationRequested) Fail(s, "USB worker heartbeat failed."); return; }
        }
    }

    private async Task ReadAsync(Session s)
    {
        try {
            while (await s.Process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is string line) {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.GetProperty("session").GetString() != s.Id) throw new InvalidDataException("Invalid worker session.");
                lock (gate) if (!ReferenceEquals(current, s)) return;
                var type = root.GetProperty("type").GetString();
                switch (type) {
                    case "opened": s.Opened.TrySetResult(true); break;
                    case "ack": if (s.Pending.TryGetValue(root.GetProperty("id").GetInt64(), out var ack)) ack.TrySetResult(root.GetProperty("ok").GetBoolean()); break;
                    case "line":
                        s.MotionReady = root.TryGetProperty("motionReady", out var ready) && ready.GetBoolean();
                        LineReceived?.Invoke(root.GetProperty("text").GetString(), root.GetProperty("receivedAt").GetInt64()); break;
                    case "fault": throw new IOException(root.TryGetProperty("reason", out var reason) ? reason.GetString() : "USB worker reported a fault.");
                    default: throw new InvalidDataException("Invalid worker message.");
                }
            }
            if (!s.Shutdown.IsCancellationRequested) throw new EndOfStreamException();
        } catch (Exception ex) { if (!s.Shutdown.IsCancellationRequested) Fail(s, "USB worker connection failed: " + ex.Message[..Math.Min(ex.Message.Length, 300)]); }
        finally { s.Opened.TrySetResult(false); }
    }
    private static async Task DrainErrorsAsync(Session s) { try { var b = new char[1024]; while (await s.Process.StandardError.ReadAsync(b, 0, b.Length).ConfigureAwait(false) > 0) { } } catch { } }

    private void Fail(Session s, string reason)
    {
        if (Interlocked.Exchange(ref s.Faulted, 1) != 0) return;
        lock (gate) { if (!ReferenceEquals(current, s)) return; fenced = true; generation++; }
        _ = Task.Run(() => { try { Faulted?.Invoke(reason); } catch { } });
        _ = CloseSessionAsync(s);
    }
    public Task CloseAsync() { Session s; lock (gate) { fenced = true; generation++; s = current; } return s == null ? Task.CompletedTask : CloseSessionAsync(s); }
    private Task CloseSessionAsync(Session s)
    {
        lock (s) {
            if (Volatile.Read(ref s.Closing) != 0) return s.Close;
            Interlocked.Exchange(ref s.Closing, 1);
            s.Close = CloseCoreAsync(s);
            return s.Close;
        }
    }
    private async Task CloseCoreAsync(Session s)
    {
        s.Shutdown.Cancel();
        foreach (var item in s.Pending) item.Value.TrySetResult(false);
        var inputClose = Task.Run(() => { try { s.Process.StandardInput.Close(); } catch { } });
        try { await s.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { try { s.Process.Kill(true); } catch { } try { await s.Process.WaitForExitAsync().ConfigureAwait(false); } catch { } }
        try { await Task.WhenAll(s.Reader, s.ErrorReader).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        await inputClose.ConfigureAwait(false);
        while (Volatile.Read(ref s.Writes) != 0) await Task.Delay(10).ConfigureAwait(false);
        lock (gate) { if (ReferenceEquals(current, s)) { current = null; Path = null; } }
        s.Process.Dispose();
    }
}
