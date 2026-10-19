using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Logging;
namespace Ryujinx.Graphics.OpenGL
{
    internal static class StereoGpuTiming
    {
        private static readonly string PathName = Environment.GetEnvironmentVariable("RYU_STEREO_GPU_PROFILE");
        private record Sample(int Program, string Operation, bool Geometry, float Width, float Height, int Start, int End, long CpuStart, double CpuMs = 0);
        private static readonly List<Sample> Samples = new();
        private static volatile bool _active;
        private static bool _pending;
        private static int _current = -1, _ownerThread, _pendingFrames;
        internal static void Begin(Program program, string operation)
        {
            if (!_active || Environment.CurrentManagedThreadId != _ownerThread) return;
            int a = GL.GenQuery(), b = GL.GenQuery();
            _current = Samples.Count;
            Samples.Add(new(program?.Handle ?? 0, operation, NativeStereo.GeometryPass, NativeStereo.ViewportWidth, NativeStereo.ViewportHeight, a, b, Stopwatch.GetTimestamp()));
            GL.QueryCounter(a, QueryCounterTarget.Timestamp);
        }
        internal static void End()
        {
            if (!_active || Environment.CurrentManagedThreadId != _ownerThread || _current < 0) return;
            var sample = Samples[_current];
            GL.QueryCounter(sample.End, QueryCounterTarget.Timestamp);
            Samples[_current] = sample with { CpuMs = (Stopwatch.GetTimestamp() - sample.CpuStart) * 1000.0 / Stopwatch.Frequency };
            _current = -1;
        }
        internal static void Frame()
        {
            if (string.IsNullOrEmpty(PathName)) return;
            _ownerThread = Environment.CurrentManagedThreadId;
            if (_active && _current >= 0) End();
            if (_active && Samples.Count != 0) { _active = false; _pending = true; }
            if (_pending)
            {
                GL.GetInteger((GetPName)All.QueryBufferBinding, out int queryBuffer);
                GL.BindBuffer(BufferTarget.QueryBuffer, 0);
                try
                {
                GL.GetQueryObject(Samples[^1].End, GetQueryObjectParam.QueryResultAvailable, out int ready);
                if (ready == 0) {
                    if (++_pendingFrames == 120) Logger.Notice.Print(LogClass.Gpu, $"StereoGpuTiming: pending samples={Samples.Count} lastStart={GL.IsQuery(Samples[^1].Start)} lastEnd={GL.IsQuery(Samples[^1].End)} error={GL.GetError()}");
                    return;
                }
                var rows = new List<object>();
                foreach (var sample in Samples)
                {
                    GL.GetQueryObject(sample.Start, GetQueryObjectParam.QueryResult, out long start);
                    GL.GetQueryObject(sample.End, GetQueryObjectParam.QueryResult, out long end);
                    rows.Add(new { sample.Program, sample.Operation, sample.Geometry, sample.Width, sample.Height, sample.CpuMs, GpuMs = (end - start) / 1000000.0 });
                    GL.DeleteQuery(sample.Start); GL.DeleteQuery(sample.End);
                }
                File.WriteAllText(PathName + ".json", JsonSerializer.Serialize(rows));
                Logger.Notice.Print(LogClass.Gpu, $"StereoGpuTiming: captured {rows.Count} operations to {PathName}.json");
                Samples.Clear(); _pending = false;
                }
                finally { GL.BindBuffer(BufferTarget.QueryBuffer, queryBuffer); }
            }
            if (File.Exists(PathName + ".request"))
            {
                File.Delete(PathName + ".request");
                Samples.Clear(); _active = true;
                Logger.Notice.Print(LogClass.Gpu, "StereoGpuTiming: armed for next game draws");
            }
        }
    }
}
