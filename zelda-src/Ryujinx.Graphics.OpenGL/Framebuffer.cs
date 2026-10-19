using OpenTK.Graphics.OpenGL;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.OpenGL.Image;
using System;
using System.Runtime.CompilerServices;

namespace Ryujinx.Graphics.OpenGL
{
    class Framebuffer : IDisposable
    {
        private int _leftHandle;
        private int _rightHandle;
        private int _layeredHandle, _layeredDepth, _layeredDrawCount = -1;
        private readonly int[] _layeredColors = new int[8];
        private FramebufferAttachment _layeredDepthAttachment;

        private long _attachmentRevision, _layeredRevision = -1, _layeredStorageRevision = -1;
        private bool _layeredAvailable;
        private int _leftDepthHandle;
        private bool TryBindLayered()
        {
            if (_layeredRevision == _attachmentRevision && _layeredStorageRevision == SameFrameStereo.StorageCount)
            {
                if (_layeredAvailable) FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, _layeredHandle);
                return _layeredAvailable;
            }
            _layeredRevision = _attachmentRevision;
            _layeredStorageRevision = SameFrameStereo.StorageCount;
            _layeredAvailable = false;
            bool any = false;
            foreach (var color in _colors)
            {
                if (color == null) continue;
                if (!color.CanRenderLayered) return false;
                any = true;
            }
            if (_depthStencil != null)
            {
                if (!_depthStencil.CanRenderLayered) return false;
                any = true;
            }
            if (!any) return false;
            bool created = _layeredHandle == 0;
            if (created) _layeredHandle = GL.GenFramebuffer();
            FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, _layeredHandle);
            for (int i = 0; i < _colors.Length; i++)
            {
                int handle = _colors[i]?.LayeredRenderHandle ?? 0;
                if (_layeredColors[i] == handle) continue;
                GL.FramebufferTexture(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0 + i, handle, 0);
                _layeredColors[i] = handle;
            }
            int depth = _depthStencil?.LayeredRenderHandle ?? 0;
            if (_layeredDepthAttachment != _lastDsAttachment || _layeredDepth != depth)
            {
                if (_layeredDepthAttachment != 0) GL.FramebufferTexture(FramebufferTarget.DrawFramebuffer, _layeredDepthAttachment, 0, 0);
                if (_lastDsAttachment != 0) GL.FramebufferTexture(FramebufferTarget.DrawFramebuffer, _lastDsAttachment, depth, 0);
                _layeredDepthAttachment = _lastDsAttachment;
                _layeredDepth = depth;
            }
            int count = _dualSourceBlend ? 1 : _colorsCount;
            if (_layeredDrawCount != count) { SetDrawBuffersImpl(count); _layeredDrawCount = count; }
            if (created && GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException("Layered stereo framebuffer is incomplete.");
            _layeredAvailable = true;
            return true;
        }
        private readonly int[] _rightColors = new int[8];
        private int _rightDepth;
        private FramebufferAttachment _rightDepthAttachment;
        private int _rightDrawCount = -1;
        public int Handle { get => NativeStereo.Enabled && NativeStereo.CurrentEye == 1 && _rightHandle != 0 ? _rightHandle : _leftHandle; private set => _leftHandle = value; }
        private int _clearFbHandle;
        private bool _clearFbInitialized;

        private FramebufferAttachment _lastDsAttachment;

        private readonly TextureView[] _colors;
        private TextureView _depthStencil;

        private int _colorsCount;
        private bool _dualSourceBlend;

        public Framebuffer()
        {
            Handle = GL.GenFramebuffer();
            _clearFbHandle = GL.GenFramebuffer();

            _colors = new TextureView[8];
        }

        private bool _targetsConfigured;
        internal bool MatchesTargets(ITexture[] colors, ITexture depthStencil)
        {
            if (!_targetsConfigured || _colorsCount != colors.Length || !ReferenceEquals(_depthStencil, depthStencil)) return false;
            for (int i = 0; i < colors.Length; i++)
                if (!ReferenceEquals(_colors[i], colors[i])) return false;
            return true;
        }

        internal bool HasStereoTargets
        {
            get
            {
                if (_depthStencil?.HasStereoStorage == true) return true;
                foreach (var color in _colors) if (color?.HasStereoStorage == true) return true;
                return false;
            }
        }
        internal void TraceOutput(int program, long sequence)
        {
            TextureView view = _colors[0];
            if (view == null || view.Target != Target.Texture2D || view.Width < 800 || view.Width < view.Height * 1.5f || view.Width > view.Height * 2.1f) return;
            string path = System.Environment.GetEnvironmentVariable("RYU_STEREO_TRACE");
            int width = System.Math.Min(360, view.Width), height = System.Math.Min(480, view.Height);
            int x = (view.Width - width) / 2, y = System.Math.Clamp((int)(view.Height * 0.72f) - height / 2, 0, view.Height - height);
            byte[] bytes = new byte[width * height * 4];
            GL.GetInteger(GetPName.PixelPackBufferBinding, out int pack);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
            try
            {
                GL.GetTextureSubImage(view.Handle, 0, x, y, 0, width, height, 1, PixelFormat.Rgba, PixelType.UnsignedByte, bytes.Length, bytes);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(path, $"after{sequence:D9}_p{program}_eye{NativeStereo.CurrentEye}_{width}x{height}_at{x}x{y}_of{view.Width}x{view.Height}.rgba8"), bytes);
            }
            finally { GL.BindBuffer(BufferTarget.PixelPackBuffer, pack); }
        }
        internal void MakeStereoTargets()
        {
            foreach (var color in _colors) color?.MakeStereo();
            _depthStencil?.MakeStereo();
        }
        internal bool RightEyeIsBound => _rightHandle != 0 && FramebufferBinding.IsDrawBound(_rightHandle);

        internal void RebindStereoEye(bool allowLayered = false)
        {
            NativeStereo.LayeredTarget = allowLayered && NativeStereo.LayeredStorage && NativeStereo.LayeredRendering && TryBindLayered();
            if (NativeStereo.LayeredTarget) return;
            // Eye draws never read from the framebuffer binding. Keep the read
            // target stable; texture-copy operations bind their own read FBO.

            if (NativeStereo.CurrentEye == 0)
            {
                FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, _leftHandle);
                return;
            }
            if (_rightHandle == 0) _rightHandle = GL.GenFramebuffer();
            FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, _rightHandle);
            for (int i = 0; i < _colors.Length; i++)
            {
                int handle = _colors[i]?.Handle ?? 0;
                if (_rightColors[i] != handle)
                {
                    GL.FramebufferTexture(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0 + i, handle, 0);
                    _rightColors[i] = handle;
                }
            }
            int depth = _depthStencil?.Handle ?? 0;
            if (_rightDepthAttachment != _lastDsAttachment || _rightDepth != depth)
            {
                if (_rightDepthAttachment != 0)
                    GL.FramebufferTexture(FramebufferTarget.DrawFramebuffer, _rightDepthAttachment, 0, 0);
                if (_lastDsAttachment != 0)
                    GL.FramebufferTexture(FramebufferTarget.DrawFramebuffer, _lastDsAttachment, depth, 0);
                _rightDepthAttachment = _lastDsAttachment;
                _rightDepth = depth;
            }
            int count = _dualSourceBlend ? 1 : _colorsCount;
            if (_rightDrawCount != count)
            {
                SetDrawBuffersImpl(count);
                _rightDrawCount = count;
            }
        }

        public int Bind()
        {
            FramebufferBinding.Bind(FramebufferTarget.Framebuffer, Handle);
            return Handle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AttachColor(int index, TextureView color)
        {
            if (_colors[index] == color)
            {
                return;
            }

            FramebufferAttachment attachment = FramebufferAttachment.ColorAttachment0 + index;

            GL.FramebufferTexture(FramebufferTarget.Framebuffer, attachment, color?.Handle ?? 0, 0);

            _colors[index] = color;
            _attachmentRevision++;
        }

        public void AttachDepthStencil(TextureView depthStencil)
        {
            int handle = depthStencil?.Handle ?? 0;
            if (ReferenceEquals(_depthStencil, depthStencil) && _leftDepthHandle == handle) return;
            _leftDepthHandle = handle;
            _attachmentRevision++;
            // Detach the last depth/stencil buffer if there is any.
            if (_lastDsAttachment != 0)
            {
                GL.FramebufferTexture(FramebufferTarget.Framebuffer, _lastDsAttachment, 0, 0);
            }

            if (depthStencil != null)
            {
                FramebufferAttachment attachment = GetAttachment(depthStencil.Format);

                GL.FramebufferTexture(
                    FramebufferTarget.Framebuffer,
                    attachment,
                    depthStencil.Handle,
                    0);

                _lastDsAttachment = attachment;
            }
            else
            {
                _lastDsAttachment = 0;
            }

            _depthStencil = depthStencil;
        }

        public void SetDualSourceBlend(bool enable)
        {
            bool oldEnable = _dualSourceBlend;
            if (oldEnable == enable) return;
            // Draw-buffer selection belongs to the left FBO; the right copy is updated on its next bind.
            if (NativeStereo.Enabled) RebindStereoEye();

            _dualSourceBlend = enable;
            _attachmentRevision++;

            // When dual source blend is used,
            // we can only have one draw buffer.
            if (enable)
            {
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            }
            else if (oldEnable)
            {
                SetDrawBuffersImpl(_colorsCount);
            }
        }

        public void SetDrawBuffers(int colorsCount)
        {
            _targetsConfigured = true;
            if (_colorsCount != colorsCount && !_dualSourceBlend)
            {
                SetDrawBuffersImpl(colorsCount);
            }

            if (_colorsCount != colorsCount) _attachmentRevision++;
            _colorsCount = colorsCount;
        }

        private static void SetDrawBuffersImpl(int colorsCount)
        {
            DrawBuffersEnum[] drawBuffers = new DrawBuffersEnum[colorsCount];

            for (int index = 0; index < colorsCount; index++)
            {
                drawBuffers[index] = DrawBuffersEnum.ColorAttachment0 + index;
            }

            GL.DrawBuffers(colorsCount, drawBuffers);
        }

        private static FramebufferAttachment GetAttachment(Format format)
        {
            if (FormatTable.IsPackedDepthStencil(format))
            {
                return FramebufferAttachment.DepthStencilAttachment;
            }
            else if (FormatTable.IsDepthOnly(format))
            {
                return FramebufferAttachment.DepthAttachment;
            }
            else
            {
                return FramebufferAttachment.StencilAttachment;
            }
        }

        public int GetColorLayerCount(int index)
        {
            return _colors[index]?.Info.GetDepthOrLayers() ?? 0;
        }

        public int GetDepthStencilLayerCount()
        {
            return _depthStencil?.Info.GetDepthOrLayers() ?? 0;
        }

        public void AttachColorLayerForClear(int index, int layer)
        {
            TextureView color = _colors[index];

            if (!IsLayered(color))
            {
                return;
            }

            BindClearFb();
            GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + index, color.Handle, 0, layer);
        }

        public void DetachColorLayerForClear(int index)
        {
            TextureView color = _colors[index];

            if (!IsLayered(color))
            {
                return;
            }

            GL.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + index, 0, 0);
            Bind();
        }

        public void AttachDepthStencilLayerForClear(int layer)
        {
            TextureView depthStencil = _depthStencil;

            if (!IsLayered(depthStencil))
            {
                return;
            }

            BindClearFb();
            GL.FramebufferTextureLayer(FramebufferTarget.Framebuffer, GetAttachment(depthStencil.Format), depthStencil.Handle, 0, layer);
        }

        public void DetachDepthStencilLayerForClear()
        {
            TextureView depthStencil = _depthStencil;

            if (!IsLayered(depthStencil))
            {
                return;
            }

            GL.FramebufferTexture(FramebufferTarget.Framebuffer, GetAttachment(depthStencil.Format), 0, 0);
            Bind();
        }

        private void BindClearFb()
        {
            FramebufferBinding.Bind(FramebufferTarget.Framebuffer, _clearFbHandle);

            if (!_clearFbInitialized)
            {
                SetDrawBuffersImpl(Constants.MaxRenderTargets);
                _clearFbInitialized = true;
            }
        }

        private static bool IsLayered(TextureView view)
        {
            return view != null &&
                   view.Target != Target.Texture1D &&
                   view.Target != Target.Texture2D &&
                   view.Target != Target.Texture2DMultisample &&
                   view.Target != Target.TextureBuffer;
        }

        public void Dispose()
        {
            if (Handle != 0)
            {
                FramebufferBinding.Delete(_leftHandle);
                if (_rightHandle != 0) { FramebufferBinding.Delete(_rightHandle); _rightHandle = 0; }

                Handle = 0;
            }

            if (_layeredHandle != 0) { FramebufferBinding.Delete(_layeredHandle); _layeredHandle = 0; }

            if (_clearFbHandle != 0)
            {
                FramebufferBinding.Delete(_clearFbHandle);

                _clearFbHandle = 0;
            }
        }
    }
}
