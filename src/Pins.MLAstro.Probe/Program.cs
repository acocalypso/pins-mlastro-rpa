using Pins.MLAstro.Connection;

if (args.Length != 2 || (args[0] != "wifi" && args[0] != "usb"))
{
    Console.Error.WriteLine("Usage: Pins.MLAstro.Probe wifi ws://mlastrorpa.local/ws | usb /dev/serial/by-id/DEVICE");
    return 2;
}
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; deadline.Cancel(); };
try
{
    var info = args[0] == "wifi"
        ? await DeviceDiagnostics.ProbeWifiAsync(new Uri(args[1]), deadline.Token)
        : await DeviceDiagnostics.ProbeUsbAsync(args[1], deadline.Token);
    Console.WriteLine($"{info.Transport}: {info.Firmware}");
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Probe cancelled or timed out."); return 3; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
