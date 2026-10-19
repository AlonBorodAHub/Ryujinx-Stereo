using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ryujinx.Common.Logging;
namespace Ryujinx.Graphics.OpenGL
{
    internal static class StereoPerformance
    {
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("RYU_STEREO_PERF") == "1";
        private static readonly Dictionary<string, (long Ticks, int Calls)> Samples = new();
        internal static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;
        internal static void End(long start, string kind)
        {
            if (!Enabled) return;
            long ticks = Stopwatch.GetTimestamp() - start;
            Samples.TryGetValue(kind, out var sample);
            Samples[kind] = (sample.Ticks + ticks, sample.Calls + 1);
        }
        internal static void Report()
        {
            if (!Enabled) return;
            foreach (var item in Samples)
                Logger.Notice.Print(LogClass.Gpu, $"StereoPerformance: {item.Key} calls={item.Value.Calls} ms={item.Value.Ticks * 1000.0 / Stopwatch.Frequency:F2}");
            Samples.Clear();
        }
    }
}
