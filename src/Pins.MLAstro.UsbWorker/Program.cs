using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

if (!OperatingSystem.IsLinux()) { Console.Error.WriteLine("USB worker requires Linux."); return 2; }
try {
    string Arg(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing " + key); }
    var path = Arg("--port");
    if (!path.StartsWith("/dev/", StringComparison.Ordinal)) throw new ArgumentException("A Linux device path is required.");
    var parent = int.Parse(Arg("--parent"));
    var session = Arg("--session");
    _ = Guid.Parse(session);
    await new Worker(path, parent, session).RunAsync();
    return 0;
} catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

sealed class Worker
{
    private static readonly Regex Identity = new(@"^ok,\s*firmware\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Field = new(@"^(JoRe|ReDe|ReAM|ReAS|MAzL|MAzR|MAlU|MAlD|AAll|AzAN|AlAN|AzED|AzEM|AzES|AzDi|AlED|AlEM|AlES|AlDi|SLvl|Back|AzBl|AlBl|STOP|ESTOP):[-+]?[0-9]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly SerialPort port;
    private readonly int parent;
    private readonly string session;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Channel<Command> urgent = Channel.CreateBounded<Command>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Channel<Command> heartbeat = Channel.CreateBounded<Command>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Channel<Command> normal = Channel.CreateBounded<Command>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Channel<object> output = Channel.CreateBounded<object>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Channel<object> controlOutput = Channel.CreateBounded<object>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly object state = new();
    private bool identified, blocked = true, faulted, active, busySeen, stopSeen, isReady, relativeMode;
    private string? initialPosition, activeJog;
    private long generation, lastParent = Stopwatch.GetTimestamp(), lastRx = Stopwatch.GetTimestamp(), lastTelemetry, lastReady, readySince;
    private int faultOnce, stopOnce, disconnectOnce;
    private TaskCompletionSource? writeInFlight;
    private record Command(string Text, long Generation, long ExpiresAt, JsonElement Id, bool Motion, bool Emergency = false);
    public Worker(string path, int parent, string session) {
        this.parent = parent; this.session = session;
        port = new SerialPort(path, 115200, Parity.None, 8, StopBits.One) { Handshake = Handshake.None, Encoding = Encoding.UTF8, ReadTimeout = 1000, WriteTimeout = 250 };
    }
    private static long Now => Stopwatch.GetTimestamp();
    private static double AgeMs(long stamp) => stamp == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
    private bool Emit(object message) {
        if (output.Writer.TryWrite(message)) return true;
        TriggerFault("IPC output queue saturated", true); return false;
    }
    private void Ack(JsonElement id, bool ok, string? error = null) {
        if (!controlOutput.Writer.TryWrite(new { type = "ack", id, ok, error, session })) TriggerFault("IPC control queue saturated", true);
    }
    private void TriggerFault(string reason, bool stop) {
        lock (state) { blocked = true; faulted = true; generation++; }
        if (Interlocked.Exchange(ref faultOnce, 1) == 0 && !controlOutput.Writer.TryWrite(new { type = "fault", reason, session })) shutdown.Cancel();
        if (stop) QueueEmergency();
    }
    private void QueueEmergency() {
        bool known; lock (state) known = identified;
        if (known && Interlocked.Exchange(ref stopOnce, 1) == 0 && !urgent.Writer.TryWrite(new Command("STOP:1\n", 0, 0, default, false, true))) shutdown.Cancel();
    }
    private void QueueDisconnect() {
        bool known; lock (state) known = identified;
        if (known && Interlocked.Exchange(ref disconnectOnce, 1) == 0)
            urgent.Writer.TryWrite(new Command("Disconnect\n", 0, 0, default, false, true));
    }
    private void OutputLoop() {
        try {
            while (true) {
                if (controlOutput.Reader.TryRead(out var item) || output.Reader.TryRead(out item)) {
                    Console.Out.WriteLine(JsonSerializer.Serialize(item)); Console.Out.Flush();
                } else if (shutdown.IsCancellationRequested) return;
                else shutdown.Token.WaitHandle.WaitOne(5);
            }
        } catch (OperationCanceledException) { }
        catch { TriggerFault("IPC output failed", true); }
    }
    private static bool Valid(string text, out bool motion, out bool stop) {
        motion = false; stop = false;
        if (text.Length == 0 || text.Length > 256 || !text.EndsWith('\n') || text[..^1].Contains('\n') || text.Any(c => c > 127 || c == '\r' || c == '\0')) return false;
        var line = text.TrimEnd('\n');
        if (line == "?" || line == "[MLAstroRPA-TC]" || line == "Disconnect") return true;
        foreach (var part in line.Split(',')) {
            if (!Field.IsMatch(part)) return false;
            var split = part.Split(':'); var name = split[0]; var value = split[1];
            var action = name.StartsWith("MA", StringComparison.OrdinalIgnoreCase) || name.Equals("AAll", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AzAN", StringComparison.OrdinalIgnoreCase) || name.Equals("AlAN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("STOP", StringComparison.OrdinalIgnoreCase) || name.Equals("ESTOP", StringComparison.OrdinalIgnoreCase);
            if (action && value != "0" && value != "1") return false;
            if ((name.StartsWith("MA", StringComparison.OrdinalIgnoreCase) || name.Equals("AAll", StringComparison.OrdinalIgnoreCase) || name.Equals("AzAN", StringComparison.OrdinalIgnoreCase) || name.Equals("AlAN", StringComparison.OrdinalIgnoreCase)) && value == "1") motion = true;
            if ((name.Equals("STOP", StringComparison.OrdinalIgnoreCase) || name.Equals("ESTOP", StringComparison.OrdinalIgnoreCase)) && value == "1") stop = true;
        }
        return !(motion && stop);
    }
    private void InputLoop() {
        try {
            while (!shutdown.IsCancellationRequested) {
                var line = Console.In.ReadLine();
                if (line == null) { TriggerFault("Parent IPC closed", true); QueueDisconnect(); Thread.Sleep(1000); shutdown.Cancel(); return; }
                try {
                    using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";
                    if (type == "ping") { if (root.GetProperty("session").GetString() == session) Volatile.Write(ref lastParent, Now); continue; }
                    var id = root.TryGetProperty("id", out var v) ? v.Clone() : default;
                    if (root.TryGetProperty("session", out var suppliedSession) && suppliedSession.GetString() != session) { Ack(id, false, "Invalid session"); continue; }
                    if (type == "fence") {
                        var next = root.GetProperty("generation").GetInt64();
                        Task drain;
                        lock (state) { blocked = true; generation = Math.Max(generation, next); drain = writeInFlight?.Task ?? Task.CompletedTask; }
                        try { if (!drain.Wait(350)) throw new TimeoutException(); }
                        catch { TriggerFault("In-flight write did not drain before fence", true); Ack(id, false, "Write still in flight"); continue; }
                        Ack(id, true); continue;
                    }
                    if (type == "allow") {
                        var next = root.GetProperty("generation").GetInt64(); bool ok;
                        lock (state) { ok = !faulted && identified && !active && isReady && AgeMs(lastReady) <= 1000 && next == generation; if (ok) blocked = false; }
                        Ack(id, ok, ok ? null : "Session is faulted or generation does not match"); continue;
                    }
                    if (type == "close") { Ack(id, true); TriggerFault("Parent requested close", true); QueueDisconnect(); Thread.Sleep(1000); shutdown.Cancel(); return; }
                    if (type != "send" || root.GetProperty("session").GetString() != session) { Ack(id, false, "Invalid session or request"); continue; }
                    var text = root.GetProperty("text").GetString() ?? "";
                    var gen = root.GetProperty("generation").GetInt64(); var expires = root.GetProperty("expiresAt").GetInt64();
                    if (!Valid(text, out var motion, out var stop)) { Ack(id, false, "Command not permitted"); continue; }
                    bool known; lock (state) known = identified;
                    if (!known && text != "?\n" && text != "[MLAstroRPA-TC]\n") { Ack(id, false, "Identity not confirmed"); continue; }
                    var command = new Command(text, gen, expires, id, motion);
                    if (!(stop ? urgent : normal).Writer.TryWrite(command)) { Ack(id, false, "Command queue full"); TriggerFault("Command queue saturated", true); }
                } catch (Exception ex) { TriggerFault("Invalid request: " + ex.Message, true); }
            }
        } catch (OperationCanceledException) { }
        catch (Exception ex) { TriggerFault("Parent IPC failed: " + ex.Message, true); }
    }
    private async Task PhysicalAsync() {
        try {
            while (!shutdown.IsCancellationRequested) {
                Command? cmd;
                if (!urgent.Reader.TryRead(out cmd) && !heartbeat.Reader.TryRead(out cmd) && !normal.Reader.TryRead(out cmd)) { await Task.Delay(5, shutdown.Token); continue; }
                bool allowed;
                TaskCompletionSource? drained = null;
                lock (state) {
                    var jogRefresh = active && activeJog != null && cmd.Text == activeJog && AgeMs(lastTelemetry) <= 1000 && AgeMs(Volatile.Read(ref lastParent)) <= 900;
                    allowed = cmd.Emergency || (cmd.ExpiresAt > Now && cmd.Generation == generation && (!cmd.Motion || (!blocked && !faulted && identified && ((!active && isReady && AgeMs(lastReady) <= 1000) || jogRefresh))));
                    if (allowed) {
                        var mode = Regex.Match(cmd.Text, @"(?:^|,)JoRe:([01])(?:,|\n)", RegexOptions.IgnoreCase);
                        if (mode.Success) relativeMode = mode.Groups[1].Value == "1";
                    }
                    if (allowed && cmd.Motion && !jogRefresh) {
                        active = true; busySeen = false; stopSeen = false; readySince = 0;
                        activeJog = !relativeMode && Regex.IsMatch(cmd.Text, @"^M(Az[LR]|Al[UD]):1\n$", RegexOptions.IgnoreCase) ? cmd.Text : null;
                    }
                    if (allowed && !cmd.Motion && (cmd.Text.StartsWith("STOP:", StringComparison.OrdinalIgnoreCase) || cmd.Text.StartsWith("ESTOP:", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(cmd.Text, @"^M(Az[LR]|Al[UD]):0", RegexOptions.IgnoreCase))) { stopSeen = true; readySince = 0; }
                    if (allowed) { drained = new(TaskCreationOptions.RunContinuationsAsynchronously); writeInFlight = drained; }
                }
                if (!allowed) { if (cmd.Id.ValueKind != JsonValueKind.Undefined) Ack(cmd.Id, false, "Expired, fenced, stale, or wrong generation"); continue; }
                try {
                    var data = Encoding.ASCII.GetBytes(cmd.Text);
                    var deadline = new CancellationTokenSource(250);
                    var write = Task.Run(async () => await port.BaseStream.WriteAsync(data.AsMemory(), deadline.Token));
                    _ = write.ContinueWith(t => { _ = t.Exception; deadline.Dispose(); }, TaskScheduler.Default);
                    await write.WaitAsync(TimeSpan.FromMilliseconds(300));
                    if (cmd.Id.ValueKind != JsonValueKind.Undefined) Ack(cmd.Id, true);
                } catch (Exception ex) {
                    if (cmd.Id.ValueKind != JsonValueKind.Undefined) Ack(cmd.Id, false, "USB write unconfirmed");
                    TriggerFault("USB write unconfirmed: " + ex.Message, false);
                    // A wedged driver cannot be trusted to accept a STOP: retire this process/link.
                    _ = Task.Run(() => { try { port.Dispose(); } catch { } }); shutdown.Cancel(); return;
                } finally { lock (state) { drained?.TrySetResult(); if (ReferenceEquals(writeInFlight, drained)) writeInFlight = null; } }
            }
        } catch (OperationCanceledException) { }
    }
    private async Task ReceiveAsync() {
        var buffer = new byte[2048]; var pending = new StringBuilder();
        try {
            while (!shutdown.IsCancellationRequested) {
                var n = await port.BaseStream.ReadAsync(buffer, shutdown.Token); if (n == 0) throw new IOException("USB stream closed");
                Volatile.Write(ref lastRx, Now);
                foreach (var ch in Encoding.UTF8.GetString(buffer, 0, n)) {
                    if (ch != '\n') { if (pending.Length < 8192) pending.Append(ch); else throw new IOException("USB line too long"); continue; }
                    var line = pending.ToString().TrimEnd('\r'); pending.Clear(); var stamp = Now;
                    lock (state) {
                        if (Identity.IsMatch(line)) identified = true;
                        if (line.StartsWith('<')) {
                            var status = line[1..].Split('|', '>')[0];
                            var ready = status.Equals("READY", StringComparison.OrdinalIgnoreCase);
                            lastTelemetry = stamp;
                            isReady = ready;
                            if (ready) {
                                lastReady = stamp;
                                if (active && (busySeen || stopSeen)) {
                                    if (readySince == 0) readySince = stamp;
                                    else if (AgeMs(readySince) >= 500) { active = false; activeJog = null; readySince = 0; }
                                }
                            } else { readySince = 0; if (active) busySeen = true; }
                            var pos = Regex.Match(line, @"Mpos:([^|>]+)");
                            if (pos.Success) { if (active && initialPosition != null && initialPosition != pos.Value) busySeen = true; initialPosition = pos.Value; }
                        }
                    }
                    if (line.Contains("Serial heartbeat timeout", StringComparison.OrdinalIgnoreCase)) TriggerFault("Firmware reported serial heartbeat ESTOP", true);
                    bool motionReady;
                    lock (state) motionReady = identified && !faulted && !active && isReady && AgeMs(lastTelemetry) <= 1000;
                    Emit(new { type = "line", text = line, receivedAt = stamp, motionReady, session });
                }
            }
        } catch (OperationCanceledException) { }
        catch (Exception ex) { TriggerFault("USB receive failed: " + ex.Message, true); }
    }
    private async Task HousekeepingAsync() {
        var lastQuery = 0L;
        while (!shutdown.IsCancellationRequested) {
            // Held-button jogs need a short deadman. Firmware-controlled finite moves
            // may be quiet during backlash take-up; give them the normal link grace.
            bool known, moving, jogging, stale;
            lock (state) { known = identified; moving = active; jogging = activeJog != null; stale = active && AgeMs(lastTelemetry) > (jogging ? 1000 : 15000); }
            if (stale) TriggerFault("Telemetry became stale during motion", true);
            if (known && !moving && AgeMs(Volatile.Read(ref lastRx)) > 15000) {
                TriggerFault("USB receive silence exceeded 15 seconds", true); QueueDisconnect(); await Task.Delay(1000); shutdown.Cancel(); return;
            }
            if (known && (lastQuery == 0 || AgeMs(lastQuery) >= 200)) {
                lastQuery = Now;
                // Heartbeat is generated independently of parent requests, through the same writer.
                heartbeat.Writer.TryWrite(new Command("?\n", 0, Now + Stopwatch.Frequency / 5, default, false, true));
            }
            bool alive;
            try { using var p = Process.GetProcessById(parent); alive = !p.HasExited; } catch { alive = false; }
            if (!alive || AgeMs(Volatile.Read(ref lastParent)) > (moving && jogging ? 900 : 30000)) {
                TriggerFault(!alive ? "Parent process exited" : "Parent heartbeat timed out", true);
                if (!alive || !moving) { QueueDisconnect(); await Task.Delay(1000); shutdown.Cancel(); return; }
            }
            await Task.Delay(25, shutdown.Token);
        }
    }
    public async Task RunAsync() {
        port.Open();
        var physical = PhysicalAsync(); var receive = ReceiveAsync(); var outputTask = Task.Factory.StartNew(OutputLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); var house = HousekeepingAsync();
        Emit(new { type = "opened", session });
        var inputTask = Task.Factory.StartNew(InputLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { await inputTask.WaitAsync(shutdown.Token); } catch (OperationCanceledException) { }
        finally { shutdown.Cancel(); try { await Task.WhenAll(physical, receive, house).WaitAsync(TimeSpan.FromSeconds(1)); } catch { } _ = Task.Run(() => { try { port.Dispose(); } catch { } }); output.Writer.TryComplete(); try { await outputTask.WaitAsync(TimeSpan.FromMilliseconds(200)); } catch { } }
    }
}
