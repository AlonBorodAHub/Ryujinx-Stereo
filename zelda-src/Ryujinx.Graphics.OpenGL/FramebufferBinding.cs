using OpenTK.Graphics.OpenGL;
using System;

namespace Ryujinx.Graphics.OpenGL
{
    // Physical GL state, independent of the logical guest eye. Every backend FBO bind goes through here.
    internal static class FramebufferBinding
    {
        [ThreadStatic] private static bool _drawKnown, _readKnown;
        [ThreadStatic] private static int _draw, _read;
        [ThreadStatic] internal static long DrawBinds;

        internal static bool IsDrawBound(int handle) => _drawKnown && _draw == handle;

        internal static void Bind(FramebufferTarget target, int handle)
        {
            bool draw = target != FramebufferTarget.ReadFramebuffer;
            bool read = target != FramebufferTarget.DrawFramebuffer;
            if (NativeStereo.Enabled && (!draw || (_drawKnown && _draw == handle)) &&
                (!read || (_readKnown && _read == handle))) return;

            GL.BindFramebuffer(target, handle);
            if (draw) { _draw = handle; _drawKnown = true; DrawBinds++; }
            if (read) { _read = handle; _readKnown = true; }
        }

        internal static void Delete(int handle)
        {
            GL.DeleteFramebuffer(handle);
            // Deletion may reset a binding; invalidate rather than assume whether the object was bound.
            if (_drawKnown && _draw == handle) _drawKnown = false;
            if (_readKnown && _read == handle) _readKnown = false;
        }
    }
}
