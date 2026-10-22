using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.OpenGL.Effects;
using Ryujinx.Graphics.OpenGL.Effects.Smaa;
using Ryujinx.Graphics.OpenGL.Image;
using System;

namespace Ryujinx.Graphics.OpenGL
{
    class Window : IWindow, IDisposable
    {
        private readonly OpenGLRenderer _renderer;
        private readonly NativeStereo _nativeStereo = new();

        private bool _initialized;

        private int _width;
        private int _height;
        private bool _updateSize;
        private int _copyFramebufferHandle;
        private IPostProcessingEffect _antiAliasing;
        private IScalingFilter _scalingFilter;
        private bool _isLinear;
        private AntiAliasing _currentAntiAliasing;
        private bool _updateEffect;
        private ScalingFilter _currentScalingFilter;
        private float _scalingFilterLevel;
        private bool _updateScalingFilter;
        private bool _isBgra;
        private TextureView _upscaledTexture;

        internal BackgroundContextWorker BackgroundContext { get; private set; }

        internal bool ScreenCaptureRequested { get; set; }

        public Window(OpenGLRenderer renderer)
        {
            _renderer = renderer;
        }

        public void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            GL.Disable(EnableCap.FramebufferSrgb);

            (int oldDrawFramebufferHandle, int oldReadFramebufferHandle) = ((Pipeline)_renderer.Pipeline).GetBoundFramebuffers();

            if (NativeStereo.Enabled)
            {
                // Keep the diagnostic capture path intact. Normal presentation
                // needs only one resample into each half of the final surface.
                NativeStereo.RefreshOutputSettings();
                bool direct = StereoOutput.Direct && NativeStereo.DirectOutput &&
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RYU_STEREO_CAPTURE")) &&
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RYU_STEREO_TRACE"));
                _nativeStereo.Present(fbo => CopyTextureToFrameBufferRGB(fbo, GetCopyFramebufferHandleLazy(), (TextureView)texture, crop, () => { }, direct, true), _width, _height, swapBuffersCallback, direct);
            }
            else
                CopyTextureToFrameBufferRGB(0, GetCopyFramebufferHandleLazy(), (TextureView)texture, crop, swapBuffersCallback);

            FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, oldReadFramebufferHandle);
            FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, oldDrawFramebufferHandle);

            GL.Enable(EnableCap.FramebufferSrgb);

            // Restore unpack alignment to 4, as performance overlays such as RTSS may change this to load their resources.
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        }

        public void ChangeVSyncMode(VSyncMode vSyncMode) { }

        public void SetSize(int width, int height)
        {
            _width = width;
            _height = height;

            _updateSize = true;
        }

        private void CopyTextureToFrameBufferRGB(int drawFramebuffer, int readFramebuffer, TextureView view, ImageCrop crop, Action swapBuffersCallback, bool directStereo = false, bool stereo = false)
        {
            int outputWidth = stereo ? StereoOutput.EyeWidth(_width) : _width;
            int outputHeight = stereo ? StereoOutput.EyeHeight(_height) : _height;
            FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, drawFramebuffer);
            FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, readFramebuffer);

            TextureView viewConverted = view.Format.IsBgr() ? _renderer.TextureCopy.BgraSwap(view) : view;

            UpdateEffect();

            if (_antiAliasing != null)
            {
                TextureView oldView = viewConverted;

                viewConverted = _antiAliasing.Run(viewConverted, outputWidth, outputHeight);

                if (viewConverted.Format.IsBgr())
                {
                    TextureView swappedView = _renderer.TextureCopy.BgraSwap(viewConverted);

                    viewConverted?.Dispose();

                    viewConverted = swappedView;
                }

                if (viewConverted != oldView && oldView != view)
                {
                    oldView.Dispose();
                }
            }

            FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, drawFramebuffer);
            FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, readFramebuffer);

            GL.FramebufferTexture(
                FramebufferTarget.ReadFramebuffer,
                FramebufferAttachment.ColorAttachment0,
                viewConverted.Handle,
                0);

            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);

            GL.Disable(EnableCap.RasterizerDiscard);
            GL.Disable(IndexedEnableCap.ScissorTest, 0);

            if (!directStereo || NativeStereo.CurrentEye == 0)
            {
                if (directStereo) GL.ColorMask(true, true, true, true);
                GL.Clear(ClearBufferMask.ColorBufferBit);
            }

            int srcX0, srcX1, srcY0, srcY1;

            if (crop.Left == 0 && crop.Right == 0)
            {
                srcX0 = 0;
                srcX1 = viewConverted.Width;
            }
            else
            {
                srcX0 = crop.Left;
                srcX1 = crop.Right;
            }

            if (crop.Top == 0 && crop.Bottom == 0)
            {
                srcY0 = 0;
                srcY1 = viewConverted.Height;
            }
            else
            {
                srcY0 = crop.Top;
                srcY1 = crop.Bottom;
            }

            float ratioX = crop.IsStretched ? 1.0f : MathF.Min(1.0f, outputHeight * crop.AspectRatioX / (outputWidth * crop.AspectRatioY));
            float ratioY = crop.IsStretched ? 1.0f : MathF.Min(1.0f, outputWidth * crop.AspectRatioY / (outputHeight * crop.AspectRatioX));

            int dstWidth = (int)(outputWidth * ratioX);
            int dstHeight = (int)(outputHeight * ratioY);

            int dstPaddingX = (outputWidth - dstWidth) / 2;
            int dstPaddingY = (outputHeight - dstHeight) / 2;

            int dstX0 = crop.FlipX ? outputWidth - dstPaddingX : dstPaddingX;
            int dstX1 = crop.FlipX ? dstPaddingX : outputWidth - dstPaddingX;

            int dstY0 = crop.FlipY ? dstPaddingY : outputHeight - dstPaddingY;
            int dstY1 = crop.FlipY ? outputHeight - dstPaddingY : dstPaddingY;

            if (ScreenCaptureRequested)
            {
                CaptureFrame(srcX0, srcY0, srcX1, srcY1, view.Format.IsBgr(), crop.FlipX, crop.FlipY);

                ScreenCaptureRequested = false;
            }

            if (_scalingFilter != null)
            {
                if (viewConverted.Format.IsBgr() && !_isBgra)
                {
                    RecreateUpscalingTexture(true);
                }

                _scalingFilter.Run(
                    viewConverted,
                    _upscaledTexture,
                    outputWidth,
                    outputHeight,
                    new Extents2D(
                        srcX0,
                        srcY0,
                        srcX1,
                        srcY1),
                    new Extents2D(
                        dstX0,
                        dstY0,
                        dstX1,
                        dstY1)
                    );

                srcX0 = dstX0;
                srcY0 = dstY0;
                srcX1 = dstX1;
                srcY1 = dstY1;

                GL.FramebufferTexture(
                    FramebufferTarget.ReadFramebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    _upscaledTexture.Handle,
                    0);
            }

            if (directStereo)
            {
                int eyeOffset = NativeStereo.CurrentEye * (outputWidth / 2);
                dstX0 = dstX0 / 2 + eyeOffset;
                dstX1 = dstX1 / 2 + eyeOffset;
            }

            GL.BlitFramebuffer(
                srcX0,
                srcY0,
                srcX1,
                srcY1,
                dstX0,
                dstY0,
                dstX1,
                dstY1,
                ClearBufferMask.ColorBufferBit,
                _isLinear ? BlitFramebufferFilter.Linear : BlitFramebufferFilter.Nearest);


            // Remove Alpha channel
            if (!directStereo || NativeStereo.CurrentEye == 1)
            {
                GL.ColorMask(false, false, false, true);
                GL.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
            }

            for (int i = 0; i < Constants.MaxRenderTargets; i++)
            {
                ((Pipeline)_renderer.Pipeline).RestoreComponentMask(i);
            }

            // Set clip control, viewport and the framebuffer to the output to placate overlays and OBS capture.
            GL.ClipControl(ClipOrigin.LowerLeft, ClipDepthMode.NegativeOneToOne);
            GL.Viewport(0, 0, outputWidth, outputHeight);
            FramebufferBinding.Bind(FramebufferTarget.Framebuffer, drawFramebuffer);

            swapBuffersCallback();


            ((Pipeline)_renderer.Pipeline).RestoreClipControl();
            ((Pipeline)_renderer.Pipeline).RestoreScissor0Enable();
            ((Pipeline)_renderer.Pipeline).RestoreRasterizerDiscard();
            ((Pipeline)_renderer.Pipeline).RestoreViewport0();

            if (viewConverted != view)
            {
                viewConverted.Dispose();
            }
        }

        private int GetCopyFramebufferHandleLazy()
        {
            int handle = _copyFramebufferHandle;

            if (handle == 0)
            {
                handle = GL.GenFramebuffer();

                _copyFramebufferHandle = handle;
            }

            return handle;
        }

        public void InitializeBackgroundContext(IOpenGLContext baseContext)
        {
            BackgroundContext = new BackgroundContextWorker(baseContext);
            _initialized = true;
        }

        public void CaptureFrame(int x, int y, int width, int height, bool isBgra, bool flipX, bool flipY)
        {
            long size = Math.Abs(4 * width * height);
            byte[] bitmap = new byte[size];

            GL.ReadPixels(x, y, width, height, isBgra ? PixelFormat.Bgra : PixelFormat.Rgba, PixelType.UnsignedByte, bitmap);

            _renderer.OnScreenCaptured(new ScreenCaptureImageInfo(width, height, isBgra, bitmap, flipX, flipY));
        }

        public void Dispose()
        {
            _nativeStereo.Dispose();
            if (!_initialized)
            {
                return;
            }

            BackgroundContext.Dispose();

            if (_copyFramebufferHandle != 0)
            {
                FramebufferBinding.Delete(_copyFramebufferHandle);

                _copyFramebufferHandle = 0;
            }

            _antiAliasing?.Dispose();
            _scalingFilter?.Dispose();
            _upscaledTexture?.Dispose();
        }

        public void SetAntiAliasing(AntiAliasing effect)
        {
            if (_currentAntiAliasing == effect && _antiAliasing != null)
            {
                return;
            }

            _currentAntiAliasing = effect;

            _updateEffect = true;
        }

        public void SetScalingFilter(ScalingFilter type)
        {
            if (_currentScalingFilter == type && _antiAliasing != null)
            {
                return;
            }

            _currentScalingFilter = type;

            _updateScalingFilter = true;
        }

        public void SetColorSpacePassthrough(bool colorSpacePassthroughEnabled) { }

        private void UpdateEffect()
        {
            if (_updateEffect)
            {
                _updateEffect = false;

                switch (_currentAntiAliasing)
                {
                    case AntiAliasing.Fxaa:
                        _antiAliasing?.Dispose();
                        _antiAliasing = new FxaaPostProcessingEffect(_renderer);
                        break;
                    case AntiAliasing.None:
                        _antiAliasing?.Dispose();
                        _antiAliasing = null;
                        break;
                    case AntiAliasing.SmaaLow:
                    case AntiAliasing.SmaaMedium:
                    case AntiAliasing.SmaaHigh:
                    case AntiAliasing.SmaaUltra:
                        int quality = _currentAntiAliasing - AntiAliasing.SmaaLow;
                        if (_antiAliasing is SmaaPostProcessingEffect smaa)
                        {
                            smaa.Quality = quality;
                        }
                        else
                        {
                            _antiAliasing?.Dispose();
                            _antiAliasing = new SmaaPostProcessingEffect(_renderer, quality);
                        }
                        break;
                }
            }

            if (_updateSize && !_updateScalingFilter)
            {
                RecreateUpscalingTexture();
            }

            _updateSize = false;

            if (_updateScalingFilter)
            {
                _updateScalingFilter = false;

                switch (_currentScalingFilter)
                {
                    case ScalingFilter.Bilinear:
                    case ScalingFilter.Nearest:
                        _scalingFilter?.Dispose();
                        _scalingFilter = null;
                        _isLinear = _currentScalingFilter == ScalingFilter.Bilinear;
                        _upscaledTexture?.Dispose();
                        _upscaledTexture = null;
                        break;
                    case ScalingFilter.Fsr:
                        if (_scalingFilter is not FsrScalingFilter)
                        {
                            _scalingFilter?.Dispose();
                            _scalingFilter = new FsrScalingFilter(_renderer);
                        }
                        _isLinear = false;
                        _scalingFilter.Level = _scalingFilterLevel;

                        RecreateUpscalingTexture();
                        break;
                    case ScalingFilter.Area:
                        if (_scalingFilter is not AreaScalingFilter)
                        {
                            _scalingFilter?.Dispose();
                            _scalingFilter = new AreaScalingFilter(_renderer);
                        }
                        _isLinear = false;

                        RecreateUpscalingTexture();
                        break;
                }
            }
        }

        private void RecreateUpscalingTexture(bool forceBgra = false)
        {
            _upscaledTexture?.Dispose();

            TextureCreateInfo info = new(
                _width,
                _height,
                1,
                1,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8Unorm,
                DepthStencilMode.Depth,
                Target.Texture2D,
                forceBgra ? SwizzleComponent.Blue : SwizzleComponent.Red,
                SwizzleComponent.Green,
                forceBgra ? SwizzleComponent.Red : SwizzleComponent.Blue,
                SwizzleComponent.Alpha);

            _isBgra = forceBgra;
            _upscaledTexture = _renderer.CreateTexture(info) as TextureView;
        }

        public void SetScalingFilterLevel(float level)
        {
            _scalingFilterLevel = level;
            _updateScalingFilter = true;
        }
    }
}
