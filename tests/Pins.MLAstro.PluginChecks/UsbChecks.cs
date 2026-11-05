using System.Runtime.InteropServices;
using System.Text;
using NINA.Plugins.MLAstroRPA;

static class UsbChecks
{
    public static void Metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "mlastro-usb-" + Guid.NewGuid().ToString("N"));
        var dev = Path.Combine(root, "dev"); var tty = Path.Combine(root, "tty");
        try {
            Directory.CreateDirectory(Path.Combine(dev, "serial/by-id"));
            var device = Path.Combine(root, "devices/usb1/1-2"); Directory.CreateDirectory(device);
            var port = Path.Combine(device, "1-2:1.0/ttyUSB0");
            var realTty = Path.Combine(port, "tty/ttyUSB0"); Directory.CreateDirectory(realTty);
            Directory.CreateDirectory(tty);
            Directory.CreateSymbolicLink(Path.Combine(tty, "ttyUSB0"), realTty);
            Directory.CreateSymbolicLink(Path.Combine(realTty, "device"), "../..");
            File.WriteAllText(Path.Combine(dev, "ttyUSB0"), "unchanged");
            foreach (var item in new[] { ("idVendor", "10c4"), ("idProduct", "ea60"), ("manufacturer", "Silicon Labs"), ("product", "CP2102 USB UART"), ("serial", "test-1") }) File.WriteAllText(Path.Combine(device, item.Item1), item.Item2);
            var stable = Path.Combine(dev, "serial/by-id/usb-test-1");
            File.CreateSymbolicLink(stable, Path.Combine(dev, "ttyUSB0"));
            var devices = UsbDiscovery.Enumerate(dev, tty);
            if (devices.Length != 1 || devices[0].path != stable || UsbDiscovery.Select(devices, "") != stable) throw new Exception("USB metadata discovery failed stable path selection");
            var other = devices[0] with { path = "/dev/ttyUSB1", target = "/dev/ttyUSB1" };
            try { UsbDiscovery.Select([devices[0], other], ""); throw new Exception("Ambiguous USB devices silently selected"); } catch (InvalidOperationException) { }
            if (UsbDiscovery.Select([devices[0], other], stable) != stable) throw new Exception("Verified stable path not remembered");
            var branded = other with { likelyRpa = true };
            if (UsbDiscovery.Select([devices[0], branded], "") != branded.path) throw new Exception("Branded device not preferred");
            try { UsbDiscovery.Select([devices[0] with { compatible = false }], ""); throw new Exception("Unrelated USB device auto-selected"); } catch (InvalidOperationException) { }
            if (File.ReadAllText(Path.Combine(dev, "ttyUSB0")) != "unchanged") throw new Exception("Discovery wrote to a device");
            Console.WriteLine("PASS USB metadata-only stable paths, ambiguity, remembered identity and unrelated-device exclusion");
            Console.WriteLine("INFO real USB metadata devices: " + UsbDiscovery.Enumerate().Length);
        } finally { Directory.Delete(root, true); }
    }
    public static async Task Serial(MLAstroPlugin plugin, CancellationToken token)
    {
        var choices = new[] {
            new UsbDiscovery.Device("/dev/a-busy", "busy", false, true, "/dev/a-busy"),
            new UsbDiscovery.Device("/dev/b-wrong", "wrong", false, true, "/dev/b-wrong"),
            new UsbDiscovery.Device("/dev/c-working", "RPA", true, true, "/dev/c-working") };
        var tried = new List<string>();
        var found = await UsbAutoConnector.FindAsync(choices.Select(d => d with { likelyRpa = false }).ToArray(), "", (path, ct) => {
            tried.Add(path);
            if (path == "/dev/a-busy") throw new InvalidOperationException("Port busy");
            return Task.FromResult(path == "/dev/c-working");
        }, token);
        if (found != "/dev/c-working" || tried.Count != 3) throw new Exception("USB scan did not skip unconfirmed ports");
        Console.WriteLine("PASS USB auto scan skips busy/non-RPA ports and stops after identity confirmation");
        using (var firmware = new PtyFirmware(true, bootDelayMs: 2500)) {
            if (!await plugin.ConnectAsync(false, firmware.Path, token)) throw new Exception("Virtual USB connection failed");
            await Task.Delay(1300, token);
            if (!plugin.Serial.IsConnected || !plugin.Serial.ControllerIdentityConfirmed || plugin.GetDeviceSettings().azSteps != 77917) throw new Exception("Virtual USB connection lost or identity/settings missing");
            await plugin.DisconnectAsync();
            if (firmware.MovementCommands != 0) throw new Exception("USB connect/disconnect sent movement");
            Console.WriteLine("PASS virtual USB identity, fresh telemetry, settings and stable connection without motion");
        }
        using (var firmware = new PtyFirmware(true)) {
            if (!await plugin.ConnectAsync(false, firmware.Path, token)) throw new Exception("Virtual USB gap-test connection failed");
            firmware.PauseResponses(2000);
            await Task.Delay(1300, token);
            if (!plugin.Serial.IsConnected || plugin.Serial.IsDeviceIdle) throw new Exception("Stale telemetry remained idle or disconnected during short gap");
            try { await plugin.ManualMoveAsync("az", 1, 10); throw new Exception("Manual movement accepted stale telemetry"); } catch (InvalidOperationException) { }
            try { await plugin.SetAutomationAsync(true); throw new Exception("Automation accepted stale telemetry"); } catch (InvalidOperationException) { }
            var readyDeadline = System.Diagnostics.Stopwatch.StartNew();
            while (!plugin.Serial.IsDeviceIdle && readyDeadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50, token);
            if (!plugin.Serial.IsConnected || !plugin.Serial.IsDeviceIdle || firmware.MovementCommands != 0) throw new Exception("Short telemetry gap did not recover safely");
            Console.WriteLine("PASS short USB silence blocks motion and recovers with fresh READY without disconnect");
            firmware.PauseResponses(8000);
            var disconnectDeadline = System.Diagnostics.Stopwatch.StartNew();
            while (plugin.Serial.IsConnected && disconnectDeadline.Elapsed < TimeSpan.FromSeconds(7)) await Task.Delay(50, token);
            if (plugin.Serial.IsConnected || disconnectDeadline.Elapsed < TimeSpan.FromSeconds(4.5)) throw new Exception("Long USB silence did not disconnect after the timeout");
            var cleanupDeadline = System.Diagnostics.Stopwatch.StartNew();
            while (!plugin.Serial.WritesQuiescent && cleanupDeadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50, token);
            if (!plugin.Serial.WritesQuiescent) throw new Exception("USB cleanup did not quiesce after timeout");
            Console.WriteLine("PASS prolonged USB silence disconnects after grace period");
        }
        using (var setupGap = new PtyFirmware(true, pauseAfterHandshakeMs: 2000)) {
            if (!await plugin.ConnectAsync(false, setupGap.Path, token) || !plugin.Serial.IsDeviceIdle)
                throw new Exception("Connection setup did not recover from a short telemetry gap");
            await plugin.DisconnectAsync();
            if (setupGap.MovementCommands != 0) throw new Exception("Setup gap sent a movement command");
            Console.WriteLine("PASS USB setup tolerates a short telemetry gap and confirms fresh READY");
        }
        using (var unrelated = new PtyFirmware(false)) {
            try { await plugin.ConnectAsync(false, unrelated.Path, token); throw new Exception("Non-RPA serial device accepted"); }
            catch (InvalidOperationException) { }
            if (plugin.Serial.IsConnected || unrelated.MovementCommands != 0) throw new Exception("Rejected USB device left connected or moved");
            Console.WriteLine("PASS generic serial OK cannot masquerade as RPA; failed connection closes port");
        }
        using (var delayed = new PtyFirmware(true, bootDelayMs: 3000)) {
            var connecting = plugin.ConnectAsync(false, delayed.Path, token);
            await Task.Delay(800, token);
            var stopping = plugin.StopAsync();
            try { await connecting; throw new Exception("Stop did not cancel USB identification"); }
            catch (OperationCanceledException) { }
            await stopping;
            if (plugin.Serial.IsConnected || delayed.StopCommands != 0) throw new Exception("Cancelled identification kept port open or sent Stop to unconfirmed equipment");
            Console.WriteLine("PASS Stop cancels USB identification without sending device commands to unconfirmed equipment");
        }
        using (var jogGap = new PtyFirmware(true)) {
            if (!await plugin.ConnectAsync(false, jogGap.Path, token)) throw new Exception("Jog gap test connection failed");
            var lease = await plugin.StartJogAsync("az", 1);
            jogGap.PauseResponses(2000);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!plugin.Serial.MotionWritesBlocked && elapsed.Elapsed < TimeSpan.FromSeconds(3)) {
                await Task.Delay(150, token);
                try { await plugin.RenewJogAsync(lease); } catch (InvalidOperationException) { }
            }
            if (!plugin.Serial.MotionWritesBlocked) throw new Exception("Active jog did not fence on stale telemetry");
            var stopWait = System.Diagnostics.Stopwatch.StartNew();
            while (jogGap.StopCommands == 0 && stopWait.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20, token);
            if (jogGap.StopCommands == 0) throw new Exception("Stale active jog did not attempt general STOP");
            try { await plugin.DisconnectAsync(); } catch (InvalidOperationException) { }
            var quiet = System.Diagnostics.Stopwatch.StartNew();
            while (!plugin.Serial.WritesQuiescent && quiet.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50, token);
            if (!await plugin.ConnectAsync(false, jogGap.Path, token)) throw new Exception("Jog gap recovery reconnect failed");
            await plugin.DisconnectAsync();
            Console.WriteLine("PASS stale telemetry during active jog fences motion and attempts STOP");
        }
        using (var firmware = new PtyFirmware(true)) {
            if (!await plugin.ConnectAsync(false, firmware.Path, token)) throw new Exception("ESTOP test connection failed");
            firmware.EmitLine("ERROR:Serial heartbeat timeout -> ESTOP\n");
            var faultDeadline = System.Diagnostics.Stopwatch.StartNew();
            while (!plugin.Serial.MotionWritesBlocked && faultDeadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20, token);
            if (!plugin.Serial.MotionWritesBlocked) throw new Exception("Firmware heartbeat ESTOP did not latch the motion fence");
            try { await plugin.ManualMoveAsync("az", 1, 10); throw new Exception("Manual movement accepted after firmware heartbeat ESTOP"); } catch (InvalidOperationException) { }
            try { await plugin.SetAutomationAsync(true); throw new Exception("Automation accepted after firmware heartbeat ESTOP"); } catch (InvalidOperationException) { }
            try { await plugin.DisconnectAsync(); } catch (InvalidOperationException) { }
            var quiet = System.Diagnostics.Stopwatch.StartNew();
            while (!plugin.Serial.WritesQuiescent && quiet.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50, token);
            if (!await plugin.ConnectAsync(false, firmware.Path, token)) throw new Exception("Verified reconnect failed after simulated ESTOP");
            await plugin.DisconnectAsync();
            Console.WriteLine("PASS firmware heartbeat ESTOP latches motion fence independently of host silence threshold");
        }
    }
}

/// <summary>Private kernel pseudoterminal. Never enumerates or opens real USB equipment.</summary>
sealed class PtyFirmware : IDisposable
{
    [DllImport("libc.so.6", SetLastError = true)] private static extern int openpty(out int master, out int slave, byte[] name, IntPtr term, IntPtr size);
    [DllImport("libc.so.6")] private static extern int close(int fd);
    [DllImport("libc.so.6")] private static extern int poll(ref PollFd fds, nuint count, int timeout);
    [DllImport("libc.so.6")] private static extern nint read(int fd, byte[] buffer, nuint count);
    [DllImport("libc.so.6")] private static extern nint write(int fd, byte[] buffer, nuint count);
    [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int fd; public short events; public short revents; }
    private readonly int master, slave;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private long pauseUntil;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> outgoing = new();
    public void EmitLine(string line) => outgoing.Enqueue(line);
    public void PauseResponses(int milliseconds) => Interlocked.Exchange(ref pauseUntil,
        System.Diagnostics.Stopwatch.GetTimestamp() + (long)(milliseconds / 1000.0 * System.Diagnostics.Stopwatch.Frequency));
    private bool ResponsesPaused => System.Diagnostics.Stopwatch.GetTimestamp() < Interlocked.Read(ref pauseUntil);
    public string Path { get; }
    public int MovementCommands;
    public int StopCommands;
    public PtyFirmware(bool identity, int bootDelayMs = 0, int pauseAfterHandshakeMs = 0)
    {
        var name = new byte[256];
        if (openpty(out master, out slave, name, IntPtr.Zero, IntPtr.Zero) != 0) throw new Exception("openpty failed " + Marshal.GetLastPInvokeError());
        Path = Encoding.UTF8.GetString(name).TrimEnd('\0');
        worker = Task.Run(() => {
            var fd = new PollFd { fd = master, events = 1 }; var buffer = new byte[4096]; var pending = "";
            var last = System.Diagnostics.Stopwatch.StartNew();
            var boot = System.Diagnostics.Stopwatch.StartNew();
            var pauseAfterHandshakeDone = false;
            void Send(string line) { var bytes = Encoding.UTF8.GetBytes(line); write(master, bytes, (nuint)bytes.Length); }
            while (!shutdown.IsCancellationRequested) {
                while (outgoing.TryDequeue(out var queuedLine)) Send(queuedLine);
                if (poll(ref fd, 1, 50) > 0 && (fd.revents & 1) != 0) {
                    var count = read(master, buffer, (nuint)buffer.Length);
                    if (count > 0) pending += Encoding.UTF8.GetString(buffer, 0, (int)count);
                    int end;
                    while ((end = pending.IndexOf('\n')) >= 0) {
                        var line = pending[..end].Trim(); pending = pending[(end + 1)..];
                        if (line == "[MLAstroRPA-TC]" && boot.ElapsedMilliseconds >= bootDelayMs && !ResponsesPaused) {
                            Send(identity ? "ok,firmware 1.13.0,SN:SIMULATOR\n" : "ok\n");
                            if (!pauseAfterHandshakeDone && pauseAfterHandshakeMs > 0) { pauseAfterHandshakeDone = true; PauseResponses(pauseAfterHandshakeMs); }
                        }
                        if (line.StartsWith("MAz") || line.StartsWith("MAl") || line.StartsWith("AAll")) Interlocked.Increment(ref MovementCommands);
                        if (line.StartsWith("STOP:")) Interlocked.Increment(ref StopCommands);
                    }
                }
                if (last.ElapsedMilliseconds >= 100 && boot.ElapsedMilliseconds >= bootDelayMs && !ResponsesPaused) {
                    Send("<READY|Mpos:0,0|>SLvl:1,Back:1,AzBl:77917,AlBl:1000,AzPH:0,AlPH:0\n"); last.Restart();
                }
            }
        });
    }
    public void Dispose() { shutdown.Cancel(); worker.GetAwaiter().GetResult(); close(master); close(slave); shutdown.Dispose(); }
}
