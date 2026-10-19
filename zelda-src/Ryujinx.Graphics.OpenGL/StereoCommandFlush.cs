using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
namespace Ryujinx.Graphics.OpenGL
{
    internal static class StereoCommandFlush
    {
        [System.ThreadStatic] private static long _lastFlush;
        internal static long Count;
        internal static double CpuMilliseconds;
        internal static void Tick()
        {
            float interval = NativeStereo.CommandFlushMilliseconds;
            if (!NativeStereo.Enabled || interval <= 0 || BackgroundContextWorker.InBackground) return;
            long now = Stopwatch.GetTimestamp();
            if (_lastFlush == 0) { _lastFlush = now; return; }
            if ((now - _lastFlush) * 1000.0 / Stopwatch.Frequency < interval) return;
            // Submit pending work without waiting for its GPU completion. BOTW
            // can otherwise reach a CPU readback before the driver's batch runs.
            GL.Flush();
            long end = Stopwatch.GetTimestamp();
            Count++;
            CpuMilliseconds += (end - now) * 1000.0 / Stopwatch.Frequency;
            _lastFlush = end;
        }
    }
}
