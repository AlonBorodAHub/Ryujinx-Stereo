using OpenTK.Graphics.OpenGL;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.OpenGL;

internal sealed class SbsFrameCapture : IDisposable
{
    private sealed class Slot { internal int Buffer; internal nint Fence; internal long Timestamp; }
    private readonly Slot[] _slots = { new(), new(), new() };
    private RecordingSession _session;
    private int _texture, _framebuffer, _write, _read, _pending;
    internal void Frame(int width, int height)
    {
        var session = SbsRecording.Current;
        if (session == null || session.Completed || session.Stopping)
        {
            if (_session != null) DisposeBuffers();
            _session = null; return;
        }
        if (!session.Prepared) return;
        if (!ReferenceEquals(_session, session)) { DisposeBuffers(); _session = session; }
        if (_texture == 0) Initialize();
        while (_pending > 0)
        {
            Slot slot = _slots[_read];
            var status = GL.ClientWaitSync(slot.Fence, ClientWaitSyncFlags.None, 0);
            if (status == WaitSyncStatus.TimeoutExpired) break;
            if (status == WaitSyncStatus.WaitFailed) { session.Stop(); throw new InvalidOperationException("Stereo recording GPU readback failed."); }
            GL.DeleteSync(slot.Fence); slot.Fence = 0;
            nint mapped = GL.MapNamedBufferRange(slot.Buffer, IntPtr.Zero, SbsRecording.FrameBytes, BufferAccessMask.MapReadBit);
            if (mapped == 0) { session.Stop(); throw new InvalidOperationException("Stereo recording readback buffer could not be mapped."); }
            byte[] pixels = ArrayPool<byte>.Shared.Rent(SbsRecording.FrameBytes);
            try { Marshal.Copy(mapped, pixels, 0, SbsRecording.FrameBytes); }
            finally { GL.UnmapNamedBuffer(slot.Buffer); }
            session.Offer(pixels, slot.Timestamp);
            _read = (_read + 1) % _slots.Length; _pending--;
        }
        if (_pending == _slots.Length) { System.Threading.Interlocked.Increment(ref session.Dropped); return; }
        bool scissor = GL.IsEnabled(IndexedEnableCap.ScissorTest, 0), srgb = GL.IsEnabled(EnableCap.FramebufferSrgb);
        int pack = GL.GetInteger(GetPName.PixelPackBufferBinding), alignment = GL.GetInteger(GetPName.PackAlignment), row = GL.GetInteger(GetPName.PackRowLength), skipRows = GL.GetInteger(GetPName.PackSkipRows), skipPixels = GL.GetInteger(GetPName.PackSkipPixels);
        try
        {
            GL.Disable(IndexedEnableCap.ScissorTest, 0); GL.Disable(EnableCap.FramebufferSrgb);
            // Flip on the GPU: the first byte row sent to FFmpeg is the screen's top row.
            GL.BlitNamedFramebuffer(0, _framebuffer, 0, 0, width, height, 0, SbsRecording.Height, SbsRecording.Width, 0, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
            Slot slot = _slots[_write];
            GL.BindBuffer(BufferTarget.PixelPackBuffer, slot.Buffer);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 4); GL.PixelStore(PixelStoreParameter.PackRowLength, 0); GL.PixelStore(PixelStoreParameter.PackSkipRows, 0); GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
            GL.GetTextureImage(_texture, 0, PixelFormat.Bgra, PixelType.UnsignedByte, SbsRecording.FrameBytes, IntPtr.Zero);
            slot.Timestamp = Stopwatch.GetTimestamp(); slot.Fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
            _write = (_write + 1) % _slots.Length; _pending++;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pack);
            GL.PixelStore(PixelStoreParameter.PackAlignment, alignment); GL.PixelStore(PixelStoreParameter.PackRowLength, row); GL.PixelStore(PixelStoreParameter.PackSkipRows, skipRows); GL.PixelStore(PixelStoreParameter.PackSkipPixels, skipPixels);
            if (scissor) GL.Enable(IndexedEnableCap.ScissorTest, 0);
            if (srgb) GL.Enable(EnableCap.FramebufferSrgb);
        }
    }
    private void Initialize()
    {
        GL.CreateTextures(TextureTarget.Texture2D, 1, out _texture);
        GL.TextureStorage2D(_texture, 1, SizedInternalFormat.Rgba8, SbsRecording.Width, SbsRecording.Height);
        GL.CreateFramebuffers(1, out _framebuffer);
        GL.NamedFramebufferTexture(_framebuffer, FramebufferAttachment.ColorAttachment0, _texture, 0);
        GL.NamedFramebufferDrawBuffer(_framebuffer, DrawBufferMode.ColorAttachment0);
        foreach (Slot slot in _slots)
        {
            GL.CreateBuffers(1, out slot.Buffer);
            GL.NamedBufferStorage(slot.Buffer, SbsRecording.FrameBytes, IntPtr.Zero, BufferStorageFlags.MapReadBit);
        }
    }
    private void DisposeBuffers()
    {
        foreach (Slot slot in _slots)
        {
            if (slot.Fence != 0) GL.DeleteSync(slot.Fence);
            if (slot.Buffer != 0) GL.DeleteBuffer(slot.Buffer);
            slot.Fence = 0; slot.Buffer = 0;
        }
        if (_framebuffer != 0) FramebufferBinding.Delete(_framebuffer);
        if (_texture != 0) GL.DeleteTexture(_texture);
        _texture = _framebuffer = _write = _read = _pending = 0;
    }
    public void Dispose() { SbsRecording.Stop(); DisposeBuffers(); _session = null; }
}
