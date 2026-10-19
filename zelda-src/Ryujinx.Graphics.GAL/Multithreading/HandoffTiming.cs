using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Ryujinx.Common.Logging;
namespace Ryujinx.Graphics.GAL.Multithreading
{
    // Optional wall-time attribution. GPU waits remain part of command time;
    // these numbers are not presented as CPU execution time.
    internal static class HandoffTiming
    {
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("RYU_HANDOFF_TIMING") == "1";
        private static readonly long[] Ticks = new long[256];
        private static readonly long[] Calls = new long[256];
        private static readonly long[] Maximum = new long[256];
        private static long _lastReport = Stopwatch.GetTimestamp();
        private static long _idleTicks, _idleCalls, _invokeTicks, _invokeCalls, _copyTicks, _copyCalls, _copyBytes;
        internal static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;
        internal static void Idle(long start) { if (!Enabled) return; _idleTicks += Stopwatch.GetTimestamp()-start; _idleCalls++; }
        internal static void Invoke(long start) { if (!Enabled) return; Interlocked.Add(ref _invokeTicks, Stopwatch.GetTimestamp()-start); Interlocked.Increment(ref _invokeCalls); }
        internal static void Copy(long start, long bytes) { if (!Enabled) return; Interlocked.Add(ref _copyTicks, Stopwatch.GetTimestamp()-start); Interlocked.Increment(ref _copyCalls); Interlocked.Add(ref _copyBytes,bytes); }
        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        internal static void Command(byte kind, long start)
        {
            if (!Enabled) return;
            long now=Stopwatch.GetTimestamp(), ticks=now-start;
            Ticks[kind]+=ticks; Calls[kind]++; Maximum[kind]=Math.Max(Maximum[kind],ticks);
            if ((now-_lastReport) < Stopwatch.Frequency*2) return;
            Logger.Notice.Print(LogClass.Gpu, $"HandoffTiming: windowMs={Ms(now-_lastReport):F2} idleMs={Ms(_idleTicks):F2} idleCalls={_idleCalls} invokeMs={Ms(Interlocked.Exchange(ref _invokeTicks,0)):F2} invokeCalls={Interlocked.Exchange(ref _invokeCalls,0)} copyMs={Ms(Interlocked.Exchange(ref _copyTicks,0)):F2} copyCalls={Interlocked.Exchange(ref _copyCalls,0)} copyBytes={Interlocked.Exchange(ref _copyBytes,0)}");
            foreach (int i in Enumerable.Range(0,256).OrderByDescending(i=>Ticks[i]).Take(12))
                if(Calls[i]>0) Logger.Notice.Print(LogClass.Gpu, $"HandoffCommand: {(CommandType)i} calls={Calls[i]} totalMs={Ms(Ticks[i]):F2} maxMs={Ms(Maximum[i]):F3}");
            Array.Clear(Ticks); Array.Clear(Calls); Array.Clear(Maximum);
            _idleTicks=0; _idleCalls=0; _lastReport=now;
        }
    }
}
