using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Boundari.View;

/// <summary>
/// Adds up how long each part of a frame takes (simulation, territory uploads, overlay,
/// labels, HUD...), for the --bench report. Cheap enough to leave on.
/// </summary>
public static class FrameProfiler
{
    private static readonly Dictionary<string, long> Totals = new();
    private static readonly Dictionary<string, long> Worst = new();
    public static int Frames { get; private set; }

    public static long Start() => Stopwatch.GetTimestamp();

    public static void Stop(string section, long started)
    {
        long ticks = Stopwatch.GetTimestamp() - started;
        Totals[section] = Totals.GetValueOrDefault(section) + ticks;
        Worst[section] = System.Math.Max(Worst.GetValueOrDefault(section), ticks);
    }

    public static void EndFrame() => Frames++;

    /// <summary>Average (and worst) milliseconds per frame for each section, biggest first; then resets.</summary>
    public static string Report()
    {
        double ms = 1000.0 / Stopwatch.Frequency;
        int frames = System.Math.Max(1, Frames);
        string text = string.Join(", ", Totals.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key} {kv.Value * ms / frames:F2} (worst {Worst[kv.Key] * ms:F1})"));
        Totals.Clear();
        Worst.Clear();
        Frames = 0;
        return text;
    }
}
