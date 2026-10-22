using OpenTK.Graphics.OpenGL;
using Ryujinx.Graphics.OpenGL.Image;
using Ryujinx.Graphics.GAL;
using System;
using System.Runtime.InteropServices;
namespace Ryujinx.Graphics.OpenGL;
internal static class StereoTraceReadback
{
    // Diagnostic-only framebuffer reads. Texture-view DSA readback gave stale
    // values in the independent two-eye copy test; framebuffer reads did not.
    internal static void Read<T>(TextureView view, int handle, PixelFormat format, PixelType type, T[] output) where T : unmanaged
    {
        GL.GetInteger(GetPName.ReadFramebufferBinding, out int previous);
        int framebuffer = GL.GenFramebuffer();
        var pinned = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, framebuffer);
            bool depth = view.Format.IsDepthOrStencil();
            var attachment = depth ? FramebufferAttachment.DepthAttachment : FramebufferAttachment.ColorAttachment0;
            GL.ReadBuffer(depth ? ReadBufferMode.None : ReadBufferMode.ColorAttachment0);
            int layers = view.Target == Target.Texture2DArray ? view.Info.GetLayers() : 1;
            int layerBytes = checked(output.Length / layers * Marshal.SizeOf<T>());
            for (int layer = 0; layer < layers; layer++)
            {
                if (view.Target == Target.Texture2DArray)
                    GL.FramebufferTextureLayer(FramebufferTarget.ReadFramebuffer, attachment, handle, 0, layer);
                else GL.FramebufferTexture(FramebufferTarget.ReadFramebuffer, attachment, handle, 0);
                if (GL.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer) != FramebufferErrorCode.FramebufferComplete)
                    throw new InvalidOperationException("Diagnostic texture is not framebuffer-readable: " + view.Format);
                GL.ReadPixels(0, 0, view.Width, view.Height, format, type, pinned.AddrOfPinnedObject() + layer * layerBytes);
            }
        }
        finally
        {
            pinned.Free();
            FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, previous);
            FramebufferBinding.Delete(framebuffer);
        }
    }
}
