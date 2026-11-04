using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.MLAstroRPA;

public static class UsbAutoConnector
{
    public static async Task<string> FindAsync(UsbDiscovery.Device[] devices, string verifiedPath,
        Func<string, CancellationToken, Task<bool>> connectConfirmed, CancellationToken token)
    {
        var candidates = devices.Where(d => d.compatible)
            .OrderByDescending(d => d.path == verifiedPath && d.path.Contains("/serial/by-id/", StringComparison.Ordinal))
            .ThenByDescending(d => d.likelyRpa).ThenBy(d => d.path, StringComparer.Ordinal)
            .DistinctBy(d => d.target).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No compatible USB serial devices found. Plug the RPA into the Pi and refresh.");
        var failures = new List<string>();
        foreach (var candidate in candidates) {
            token.ThrowIfCancellationRequested();
            try { if (await connectConfirmed(candidate.path, token)) return candidate.path; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException || ex is TimeoutException) {
                failures.Add(ex.Message);
            }
        }
        throw new InvalidOperationException($"No RPA identified on {candidates.Length} compatible USB port(s). Check power and the USB data connection; disconnect other RPA control apps. " + failures.LastOrDefault());
    }
}
