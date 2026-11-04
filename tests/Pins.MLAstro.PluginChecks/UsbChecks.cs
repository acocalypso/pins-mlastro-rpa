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
    public string Path { get; }
    public int MovementCommands;
    public int StopCommands;
    public PtyFirmware(bool identity, int bootDelayMs = 0)
    {
        var name = new byte[256];
        if (openpty(out master, out slave, name, IntPtr.Zero, IntPtr.Zero) != 0) throw new Exception("openpty failed " + Marshal.GetLastPInvokeError());
        Path = Encoding.UTF8.GetString(name).TrimEnd('\0');
        worker = Task.Run(() => {
            var fd = new PollFd { fd = master, events = 1 }; var buffer = new byte[4096]; var pending = "";
            var last = System.Diagnostics.Stopwatch.StartNew();
            var boot = System.Diagnostics.Stopwatch.StartNew();
            void Send(string line) { var bytes = Encoding.UTF8.GetBytes(line); write(master, bytes, (nuint)bytes.Length); }
            while (!shutdown.IsCancellationRequested) {
                if (poll(ref fd, 1, 50) > 0 && (fd.revents & 1) != 0) {
                    var count = read(master, buffer, (nuint)buffer.Length);
                    if (count > 0) pending += Encoding.UTF8.GetString(buffer, 0, (int)count);
                    int end;
                    while ((end = pending.IndexOf('\n')) >= 0) {
                        var line = pending[..end].Trim(); pending = pending[(end + 1)..];
                        if (line == "[MLAstroRPA-TC]" && boot.ElapsedMilliseconds >= bootDelayMs) Send(identity ? "ok,firmware 1.13.0,SN:SIMULATOR\n" : "ok\n");
                        if (line.StartsWith("MAz") || line.StartsWith("MAl") || line.StartsWith("AAll")) Interlocked.Increment(ref MovementCommands);
                        if (line.StartsWith("STOP:")) Interlocked.Increment(ref StopCommands);
                    }
                }
                if (last.ElapsedMilliseconds >= 100 && boot.ElapsedMilliseconds >= bootDelayMs) {
                    Send("<READY|Mpos:0,0|>SLvl:1,Back:1,AzBl:77917,AlBl:1000,AzPH:0,AlPH:0\n"); last.Restart();
                }
            }
        });
    }
    public void Dispose() { shutdown.Cancel(); worker.GetAwaiter().GetResult(); close(master); close(slave); shutdown.Dispose(); }
}
