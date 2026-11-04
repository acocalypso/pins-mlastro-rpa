using System;

namespace NINA.Plugins.MLAstroRPA;

/// <summary>Tracks stop operations and latches uncertain motion until a verified reconnect.</summary>
public sealed class MotionSafety
{
    private readonly object gate = new();
    private long epoch;
    private bool stopping;
    private string fault;

    public long Epoch { get { lock (gate) return epoch; } }
    public bool Inhibited { get { lock (gate) return stopping || fault != null; } }
    public string State { get { lock (gate) return fault != null ? "Faulted" : stopping ? "Stopping" : "Ready"; } }
    public string Fault { get { lock (gate) return fault; } }

    public long BeginStop()
    {
        lock (gate) {
            stopping = true;
            return ++epoch;
        }
    }

    public void Fail(long operationEpoch, string reason)
    {
        lock (gate) {
            if (operationEpoch > epoch) throw new ArgumentOutOfRangeException(nameof(operationEpoch));
            // A late failure is still evidence of uncertainty, even after a newer stop.
            fault ??= string.IsNullOrWhiteSpace(reason) ? "Motion could not be confirmed stopped." : reason;
            // A recovery that began before this failure cannot clear it.
            epoch++;
            stopping = false;
        }
    }

    public void CompleteStop(long operationEpoch)
    {
        lock (gate) {
            if (operationEpoch == epoch && fault == null) stopping = false;
        }
    }

    /// <summary>The caller must first verify a fresh ready connection and operation quiescence.</summary>
    public bool ClearAfterReconnect(long expectedEpoch)
    {
        lock (gate) {
            if (expectedEpoch != epoch) return false;
            fault = null;
            stopping = false;
            return true;
        }
    }
}
