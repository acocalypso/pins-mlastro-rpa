using System;
using System.Threading;
using System.Threading.Tasks;
using MLAstroRPA.Services;

namespace NINA.Plugins.MLAstroRPA;

/// <summary>Official manual-arrow protocol with a finite displacement and telemetry completion.</summary>
internal static class ManualRelativeMove
{
    public static async Task<bool> RunAsync(SerialConnectionService serial, MlastroWebSocketService wireless,
        string axis, int direction, int arcseconds, CancellationToken token)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = serial.LastTelemetry;
        var initialPosition = axis == "az" ? initial?.AzPositionDegrees : initial?.AltPositionDegrees;
        bool busySeen = false, sent = false;
        void Observe(object sender, TelemetryDataEventArgs args)
        {
            if (!sent) return;
            var data = args.Data;
            if (data.Status == "ERROR") { completed.TrySetException(new InvalidOperationException("RPA reported a hardware error.")); return; }
            if (data.Status != "READY") { busySeen = true; return; }
            var position = axis == "az" ? data.AzPositionDegrees : data.AltPositionDegrees;
            // Tiny moves can finish between telemetry frames. Require either a busy transition or
            // a changed coordinate before accepting fresh READY telemetry as completion.
            if (busySeen || (initialPosition.HasValue && Math.Abs(position - initialPosition.Value) > 1e-8)) completed.TrySetResult(true);
        }
        serial.TelemetryDataReceived += Observe;
        try
        {
            token.ThrowIfCancellationRequested();
            sent = true;
            if (wireless.IsConnected)
                await wireless.SendManualRelativeAsync(axis, direction, arcseconds, token);
            else
            {
                // Same setters and arrow commands used by the official dock in relative mode.
                foreach (var command in new[] { "JoRe:1\n", "ReDe:0\n", $"ReAM:{arcseconds / 60}\n", $"ReAS:{arcseconds % 60}\n" })
                    if (!serial.Send(command)) throw new InvalidOperationException("RPA did not accept the relative move settings.");
                var arrow = axis == "az" ? (direction == 1 ? "MAzR" : "MAzL") : (direction == 1 ? "MAlU" : "MAlD");
                token.ThrowIfCancellationRequested();
                if (!serial.Send(arrow + ":1\n")) throw new InvalidOperationException("RPA did not accept the relative move.");
            }
            return await completed.Task.WaitAsync(TimeSpan.FromSeconds(90), token);
        }
        catch (TimeoutException)
        {
            serial.Send("STOP:1\n");
            return false;
        }
        finally { serial.TelemetryDataReceived -= Observe; }
    }
}
