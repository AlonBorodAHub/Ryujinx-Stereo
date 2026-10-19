using System;
using OpenTK.Graphics.OpenGL;
using Ryujinx.Graphics.GAL;
namespace Ryujinx.Graphics.OpenGL.Image
{
    internal static class StereoLightingTransfer
    {
        private static int _program, _halfProgram, _bufferBinding = -1;
        internal static bool Enabled = Environment.GetEnvironmentVariable("RYU_STEREO_GPU_READBACK") == "1";
        internal static bool TryCopy(TextureView view, BufferRange range, int layer, int level, int stride, Pipeline pipeline)
        {
            TextureCreateInfo info = view.Info;
            if (!Enabled || !NativeStereo.Enabled || pipeline == null || view.Target != Target.Texture2D || layer != 0 || level != 0 || stride != 0 ||
                info.Width > 32 || info.Height > 8 ||
                (info.Format != Format.R32G32B32A32Float && info.Format != Format.R16G16B16A16Float) ||
                info.SwizzleR != SwizzleComponent.Red || info.SwizzleG != SwizzleComponent.Green ||
                info.SwizzleB != SwizzleComponent.Blue || info.SwizzleA != SwizzleComponent.Alpha) return false;
            int words = info.Format == Format.R32G32B32A32Float ? 4 : 2;
            if (range.Offset % 4 != 0 || range.Size < info.Width * info.Height * words * 4) return false;
            EnsureProgram();
            if (_bufferBinding < 80) return false;
            long start = StereoPerformance.Start();
            try
            {
                GL.UseProgram(words == 4 ? _program : _halfProgram);
                GL.BindImageTexture(0, view.Handle, 0, false, 0, TextureAccess.ReadOnly,
                    words == 4 ? SizedInternalFormat.Rgba32f : SizedInternalFormat.Rgba16f);
                BindOutput(range.Handle.ToInt32(), range.Offset + range.Size);
                GL.Uniform4(0, info.Width, info.Height, range.Offset / 4, words);
                GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.FramebufferBarrierBit);
                GL.DispatchCompute((info.Width * info.Height + 63) / 64, 1, 1);
                GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
            }
            finally
            {
                BindOutput(0, 0);
                pipeline.RestoreImages1And2();
                pipeline.RestoreProgram();
                if (StereoPerformance.Enabled) StereoPerformance.End(start, $"gpu-copy {info.Width}x{info.Height} {info.Format}");
            }
            return true;
        }
        private static unsafe void BindOutput(int handle, int length)
        {
            nint offset = 0, size = length;
            if (handle == 0) GL.BindBuffersBase(BufferRangeTarget.ShaderStorageBuffer, _bufferBinding, 1, &handle);
            else GL.BindBuffersRange(BufferRangeTarget.ShaderStorageBuffer, _bufferBinding, 1, &handle, &offset, &size);
        }
        private static void EnsureProgram()
        {
            if (_program != 0) return;
            GL.GetInteger((GetPName)All.MaxShaderStorageBufferBindings, out int bindings);
            // Maxwell graphics has five stages with at most sixteen storage buffers
            // each. Use a driver slot beyond that range, with multi-bind preserving
            // the generic binding. Smaller implementations use the original path.
            _bufferBinding = bindings - 1;
            if (_bufferBinding < 80) return;
            _program = CreateProgram(false);
            _halfProgram = CreateProgram(true);
        }
        private static int CreateProgram(bool half)
        {
            string source = "#version 450 core\nlayout(local_size_x=64)in;layout(" + (half ? "rgba16f" : "rgba32f") + ",binding=0)readonly uniform image2D sourceTexture;layout(std430,binding=" + _bufferBinding + ")buffer Output{uint data[];};layout(location=0)uniform ivec4 dimensions;void main(){uint i=gl_GlobalInvocationID.x;if(i>=uint(dimensions.x*dimensions.y))return;vec4 v=imageLoad(sourceTexture,ivec2(i%uint(dimensions.x),i/uint(dimensions.x)));uint o=uint(dimensions.z)+i*uint(dimensions.w);if(dimensions.w==4){uvec4 b=floatBitsToUint(v);data[o]=b.x;data[o+1]=b.y;data[o+2]=b.z;data[o+3]=b.w;}else{data[o]=packHalf2x16(v.xy);data[o+1]=packHalf2x16(v.zw);}}";
            int shader = GL.CreateShader(ShaderType.ComputeShader);
            GL.ShaderSource(shader, source); GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
            if (compiled == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            int program = GL.CreateProgram(); GL.AttachShader(program, shader); GL.LinkProgram(program); GL.DeleteShader(shader);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
            return program;
        }
    }
}
