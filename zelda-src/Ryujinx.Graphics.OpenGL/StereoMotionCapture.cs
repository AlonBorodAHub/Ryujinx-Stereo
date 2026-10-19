using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.OpenGL
{
    // Bounded, opt-in capture of consecutive left-eye images. Readbacks use a
    // ring of PBOs and zero-timeout fence checks, never a synchronous GPU wait.
    internal static class StereoMotionCapture
    {
        private const int Width = 320, Height = 180, Bytes = Width * Height * 4;
        private sealed class Slot
        {
            public int Buffer, Frame;
            public nint Fence;
            public long Timestamp;
        }
        private sealed record Sample(int Frame, long Timestamp, byte[] Pixels);
        private static Slot[] _slots;
        private static readonly List<Sample> _samples = new();
        private static int _texture, _framebuffer, _nextFrame, _target, _dropped;
        private static string _directory;

        internal static void Begin(string directory, int frames = 180)
        {
            if (_slots != null) return;
            _directory = directory;
            _target = Math.Clamp(frames, 1, 600);
            _nextFrame = _dropped = 0;
            _samples.Clear();
            GL.CreateTextures(TextureTarget.Texture2D, 1, out _texture);
            GL.TextureStorage2D(_texture, 1, SizedInternalFormat.Rgba8, Width, Height);
            GL.CreateFramebuffers(1, out _framebuffer);
            GL.NamedFramebufferTexture(_framebuffer, FramebufferAttachment.ColorAttachment0, _texture, 0);
            GL.NamedFramebufferReadBuffer(_framebuffer, ReadBufferMode.ColorAttachment0);
            GL.NamedFramebufferDrawBuffer(_framebuffer, DrawBufferMode.ColorAttachment0);
            _slots = new Slot[8];
            for (int i = 0; i < _slots.Length; i++)
            {
                GL.CreateBuffers(1, out int buffer);
                GL.NamedBufferData(buffer, Bytes, nint.Zero, BufferUsageHint.StreamRead);
                _slots[i] = new Slot { Buffer = buffer };
            }
        }

        internal static void Frame(int width, int height, int sourceFramebuffer = 0)
        {
            if (_slots == null) return;
            int oldPack = GL.GetInteger(GetPName.PixelPackBufferBinding);
            try
            {
                foreach (Slot slot in _slots)
                {
                    if (slot.Fence == nint.Zero) continue;
                    var status = GL.ClientWaitSync(slot.Fence, ClientWaitSyncFlags.None, 0);
                    if (status != WaitSyncStatus.AlreadySignaled && status != WaitSyncStatus.ConditionSatisfied) continue;
                    byte[] pixels = new byte[Bytes];
                    GL.BindBuffer(BufferTarget.PixelPackBuffer, slot.Buffer);
                    GL.GetBufferSubData(BufferTarget.PixelPackBuffer, nint.Zero, Bytes, pixels);
                    _samples.Add(new Sample(slot.Frame, slot.Timestamp, pixels));
                    GL.DeleteSync(slot.Fence);
                    slot.Fence = nint.Zero;
                }
                if (_nextFrame < _target)
                {
                    Slot slot = _slots[_nextFrame % _slots.Length];
                    if (slot.Fence != nint.Zero) _dropped++;
                    else
                    {
                        int oldRead = GL.GetInteger(GetPName.ReadFramebufferBinding);
                        int oldDraw = GL.GetInteger(GetPName.DrawFramebufferBinding);
                        int alignment = GL.GetInteger(GetPName.PackAlignment);
                        int rowLength = GL.GetInteger(GetPName.PackRowLength);
                        int skipRows = GL.GetInteger(GetPName.PackSkipRows);
                        int skipPixels = GL.GetInteger(GetPName.PackSkipPixels);
                        try
                        {
                            FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, sourceFramebuffer);
                            FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, _framebuffer);
                            GL.BlitFramebuffer(0, 0, width / 2, height, 0, 0, Width, Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
                            FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, _framebuffer);
                            GL.BindBuffer(BufferTarget.PixelPackBuffer, slot.Buffer);
                            GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
                            GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
                            GL.PixelStore(PixelStoreParameter.PackSkipRows, 0);
                            GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
                            GL.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, nint.Zero);
                            slot.Timestamp = Stopwatch.GetTimestamp();
                            slot.Frame = _nextFrame;
                            slot.Fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
                        }
                        finally
                        {
                            GL.PixelStore(PixelStoreParameter.PackAlignment, alignment);
                            GL.PixelStore(PixelStoreParameter.PackRowLength, rowLength);
                            GL.PixelStore(PixelStoreParameter.PackSkipRows, skipRows);
                            GL.PixelStore(PixelStoreParameter.PackSkipPixels, skipPixels);
                            FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, oldRead);
                            FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, oldDraw);
                        }
                    }
                    _nextFrame++;
                }
                else if (_slots.All(slot => slot.Fence == nint.Zero))
                {
                    var samples = _samples.OrderBy(sample => sample.Frame).ToArray();
                    string directory = _directory;
                    int dropped = _dropped;
                    foreach (Slot slot in _slots) GL.DeleteBuffer(slot.Buffer);
                    GL.DeleteFramebuffer(_framebuffer);
                    GL.DeleteTexture(_texture);
                    _slots = null;
                    _samples.Clear();
                    Task.Run(() =>
                    {
                        Directory.CreateDirectory(directory);
                        using (var output = File.Create(Path.Combine(directory, "motion.rgba")))
                            foreach (Sample sample in samples) output.Write(sample.Pixels);
                        File.WriteAllText(Path.Combine(directory, "motion.json"), JsonSerializer.Serialize(new
                        {
                            width = Width, height = Height, dropped,
                            frequency = Stopwatch.Frequency,
                            samples = samples.Select(sample => new { frame = sample.Frame, timestamp = sample.Timestamp })
                        }));
                    });
                }
            }
            finally { GL.BindBuffer(BufferTarget.PixelPackBuffer, oldPack); }
        }
    }
}
