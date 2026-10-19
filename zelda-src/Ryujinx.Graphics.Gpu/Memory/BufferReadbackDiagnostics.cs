using System;
namespace Ryujinx.Graphics.Gpu.Memory
{
    public static class BufferReadbackDiagnostics
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("RYU_OPENGL_BUFFER_PREFLUSH") == "1";
        public static volatile bool UsePrefetchedReadbacks = true;
        public static long Copies, CopyBytes, PrefetchedReads, DirectReads;
    }
}
