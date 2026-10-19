using OpenTK.Graphics.OpenGL;
using Ryujinx.Graphics.OpenGL.Image;
using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.Graphics.OpenGL
{
    internal static class StereoArrayBinding
    {
        [ThreadStatic] private static Dictionary<int, int> _bound;
        [ThreadStatic] private static long _seenDeletion;
        private static long _deletions;
        internal static void Invalidate(int unit) => _bound?.Remove(unit);
        internal static void Deleted() => Interlocked.Increment(ref _deletions);
        internal static void Bind(int unit, TextureBase texture)
        {
            if (!NativeStereo.ArraySampling || texture is not TextureView view || view.Target != Target.Texture2D) return;
            int handle = view.ArraySamplingHandle;
            _bound ??= new();
            long deletion = Volatile.Read(ref _deletions);
            if (_seenDeletion != deletion) { _bound.Clear(); _seenDeletion = deletion; }
            if (_bound.TryGetValue(unit, out int old) && old == handle) return;
            GL.BindTextureUnit(unit, handle);
            _bound[unit] = handle;
        }
    }
}
