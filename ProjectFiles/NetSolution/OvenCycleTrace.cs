using System;
using System.Collections.Generic;
using FTOptix.System;

/// <summary>
/// Actual temperature trace of the current (or last) cycle per oven. It is fed by OvenSimulationLogic, which always runs,
/// so the trace survives the chart row being destroyed and recreated when the user leaves and re-enters the page.
/// </summary>
public static class OvenCycleTrace
{
    private const int MaxSamples = 7200;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Trace> Traces = new();

    private sealed class Trace
    {
        public readonly List<double[]> Samples = new();
        public bool Running;
    }

    /// <summary>Appends a sample while the cycle runs; the trace restarts when a new cycle begins and is kept once it ends.</summary>
    public static void Record(string ovenId, bool running, double cycleElapsedSeconds, double temperature)
    {
        lock (Sync)
        {
            if (!Traces.TryGetValue(ovenId, out var trace)) Traces[ovenId] = trace = new Trace();

            if (running && !trace.Running) trace.Samples.Clear();
            trace.Running = running;
            if (!running) return;

            trace.Samples.Add(new[] { Math.Round(cycleElapsedSeconds, 1), Math.Round(temperature, 1) });
            if (trace.Samples.Count > MaxSamples) trace.Samples.RemoveAt(0);
        }
    }

    public static double[][] Get(string ovenId)
    {
        lock (Sync)
        {
            return Traces.TryGetValue(ovenId, out var trace) ? trace.Samples.ToArray() : Array.Empty<double[]>();
        }
    }
}
