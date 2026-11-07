using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.MLAstroRPA;

public enum DiagnosticTransport { Unknown, Usb, Wifi }
public enum DiagnosticReadiness { Unknown, Ready, NotReady }
public enum DiagnosticAutomation { Unknown, Idle, Running, Stopping, Faulted }
public enum DiagnosticFault { None, Unknown, Connection, Timeout, Transport, Protocol, Safety, Worker, FirmwareWatchdog, TelemetryTimeout, WriteUnconfirmed, StopUnconfirmed }
public enum DiagnosticEventCode { Started, Stopped, Connected, Disconnected, Ready, NotReady, Fault, WatchdogRead, WatchdogUpdated }
public sealed record DiagnosticEvent(DateTimeOffset Timestamp, DiagnosticEventCode Code);
public sealed record DiagnosticsInput(
    string PluginVersion, string DotNetVersion, string OsVersion,
    DiagnosticTransport Transport, bool HeartbeatWorkerEnabled, DiagnosticFault Fault,
    DiagnosticReadiness Readiness, DiagnosticAutomation Automation,
    bool WatchdogSupported, bool? WatchdogEnabled,
    IReadOnlyList<DiagnosticEvent> Events,
    string? FirmwareVersion = null, string? PinsVersion = null, string? TppaVersion = null, string? RelayVersion = null,
    double? TelemetryAgeMilliseconds = null, double? LongestTelemetryGapMilliseconds = null);
public sealed record PiPowerStatus(uint? Throttled, uint? UsbMaxCurrentEnable);

/// <summary>Only explicitly modeled, non-identifying fields can enter the report.</summary>
public static class DiagnosticsReport
{
    public static string Capture(DiagnosticsInput input, PiPowerStatus? power = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var report = new
        {
            pluginVersion = SafeVersion(input.PluginVersion), dotNetVersion = SafeVersion(input.DotNetVersion), osVersion = SafeVersion(input.OsVersion),
            firmwareVersion = SafeVersion(input.FirmwareVersion), pinsVersion = SafeVersion(input.PinsVersion), tppaVersion = SafeVersion(input.TppaVersion), relayVersion = SafeVersion(input.RelayVersion),
            telemetryAgeMilliseconds = SafeDuration(input.TelemetryAgeMilliseconds), longestTelemetryGapMilliseconds = SafeDuration(input.LongestTelemetryGapMilliseconds),
            transport = SafeEnum(input.Transport), heartbeatWorkerEnabled = input.HeartbeatWorkerEnabled,
            fault = SafeEnum(input.Fault), readiness = SafeEnum(input.Readiness), automation = SafeEnum(input.Automation),
            watchdog = new { supported = input.WatchdogSupported, enabled = input.WatchdogSupported ? input.WatchdogEnabled : null },
            piPower = new { throttled = power?.Throttled, usbMaxCurrentEnable = power?.UsbMaxCurrentEnable },
            events = (input.Events ?? Array.Empty<DiagnosticEvent>()).Where(e => e != null && Enum.IsDefined(e.Code)).TakeLast(50).Select(e => new { timestamp = e.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), code = e.Code.ToString() }).ToArray()
        };
        return JsonSerializer.Serialize(report);
    }
    private static string SafeVersion(string? value) => Version.TryParse(value, out var version) ? version.ToString() : "unknown";
    private static double? SafeDuration(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= 86400000 ? value : null;
    private static string SafeEnum<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";

    public static async Task<PiPowerStatus> ReadPiPowerAsync(CancellationToken cancellationToken = default)
    {
        var throttled = await ReadNumber("get_throttled", "throttled=", true, cancellationToken);
        var usb = await ReadNumber("get_config", "usb_max_current_enable=", false, cancellationToken, "usb_max_current_enable");
        return new(throttled, usb);
    }

    private static async Task<uint?> ReadNumber(string command, string prefix, bool hex, CancellationToken cancellationToken, string? argument = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "vcgencmd", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.ArgumentList.Add(command);
        if (argument != null) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return null;
            var buffer = new char[128];
            var count = await process.StandardOutput.ReadAsync(buffer.AsMemory(), deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0 || count == buffer.Length) return null;
            var text = new string(buffer, 0, count).Trim();
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var number = text.Substring(prefix.Length);
            if (hex && number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) number = number.Substring(2);
            return uint.TryParse(number, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { try { if (!process.HasExited) process.Kill(); } catch { } throw; }
        catch { try { if (!process.HasExited) process.Kill(); } catch { } return null; }
    }
}

