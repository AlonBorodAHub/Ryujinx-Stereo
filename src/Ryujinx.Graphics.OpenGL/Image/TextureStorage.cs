using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.OpenGL.Image
{
    class TextureStorage : ITextureInfo
    {
        public ITextureInfo Storage => this;
        public int Handle { get; private set; }

        public TextureCreateInfo Info { get; }

        private readonly OpenGLRenderer _renderer;

        private int _viewsCount;
        internal int LayeredHandle { get; private set; }

        private TextureStorage(OpenGLRenderer renderer, TextureCreateInfo info, int viewHandle)
        {
            _renderer = renderer;
            Info = info;
            Handle = viewHandle;
        }
        internal TextureStorage StereoStorage { get; private set; }
        internal void EnsureStereoStorage()
        {
            if (!NativeStereo.Enabled || BackgroundContextWorker.InBackground || Info.Target == Target.TextureBuffer || Info.Target == Target.Cubemap || Info.Target == Target.CubemapArray) return;
            if (StereoStorage != null && StereoStorage.Handle != 0) return;
            if (LayeredHandle != 0)
            {
                int right = GL.GenTexture();
                FormatInfo format = FormatTable.GetFormatInfo(Info.Format);
                PixelInternalFormat internalFormat = format.IsCompressed ? (PixelInternalFormat)format.PixelFormat : format.PixelInternalFormat;
                GL.TextureView(right, TextureTarget.Texture2D, LayeredHandle, internalFormat, 0, Info.Levels, 1, 1);
                StereoStorage = new TextureStorage(_renderer, Info, right);
            }
            else StereoStorage = new TextureStorage(_renderer, Info);
            CopyToStereoStorage();
            SameFrameStereo.StorageCount++;
        }
        internal void CopyToStereoStorage()
        {
            if (StereoStorage == null || StereoStorage.Handle == 0 || Handle == 0) return;
            for (int level = 0; level < Info.Levels; level++)
            {
                int depth = Info.Target == Target.Texture3D ? System.Math.Max(1, Info.Depth >> level) : Info.GetLayers();
                GL.CopyImageSubData(Handle, (ImageTarget)Info.Target.Convert(), level, 0, 0, 0,
                    StereoStorage.Handle, (ImageTarget)Info.Target.Convert(), level, 0, 0, 0,
                    System.Math.Max(1, Info.Width >> level), System.Math.Max(1, Info.Height >> level), depth);
            }
        }


        internal ITexture DefaultView { get; private set; }

        public TextureStorage(OpenGLRenderer renderer, TextureCreateInfo info)
        {
            _renderer = renderer;
            Info = info;

            Handle = GL.GenTexture();

            CreateImmutableStorage();
        }

        private void CreateImmutableStorage()
        {
            TextureTarget target = Info.Target.Convert();

            GL.ActiveTexture(TextureUnit.Texture0);

            FormatInfo format = FormatTable.GetFormatInfo(Info.Format);

            SizedInternalFormat internalFormat;

            if (format.IsCompressed)
            {
                internalFormat = (SizedInternalFormat)format.PixelFormat;
            }
            else
            {
                internalFormat = (SizedInternalFormat)format.PixelInternalFormat;
            }

            int levels = Info.Levels;
            // Screen-sized 2D targets can use two layers while all ordinary samplers keep 2D views.
            // Other target types retain the existing separate-eye path.
            if (NativeStereo.Enabled && NativeStereo.LayeredStorage && Info.Target == Target.Texture2D &&
                (NativeStereo.ArraySampling || (Info.Width >= 256 && Info.Width > Info.Height * 1.5f && Info.Width < Info.Height * 2.1f)))
            {
                LayeredHandle = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2DArray, LayeredHandle);
                StereoArrayBinding.Invalidate(0);
                GL.TexStorage3D(TextureTarget3d.Texture2DArray, levels, internalFormat, Info.Width, Info.Height, 2);
                GL.TextureView(Handle, TextureTarget.Texture2D, LayeredHandle, (PixelInternalFormat)internalFormat, 0, levels, 0, 1);
                return;
            }
            GL.BindTexture(target, Handle);
            StereoArrayBinding.Invalidate(0);

            switch (Info.Target)
            {
                case Target.Texture1D:
                    GL.TexStorage1D(
                        TextureTarget1d.Texture1D,
                        levels,
                        internalFormat,
                        Info.Width);
                    break;

                case Target.Texture1DArray:
                    GL.TexStorage2D(
                        TextureTarget2d.Texture1DArray,
                        levels,
                        internalFormat,
                        Info.Width,
                        Info.Height);
                    break;

                case Target.Texture2D:
                    GL.TexStorage2D(
                        TextureTarget2d.Texture2D,
                        levels,
                        internalFormat,
                        Info.Width,
                        Info.Height);
                    break;

                case Target.Texture2DArray:
                    GL.TexStorage3D(
                        TextureTarget3d.Texture2DArray,
                        levels,
                        internalFormat,
                        Info.Width,
                        Info.Height,
                        Info.Depth);
                    break;

                case Target.Texture2DMultisample:
                    GL.TexStorage2DMultisample(
                        TextureTargetMultisample2d.Texture2DMultisample,
                        Info.Samples,
                        internalFormat,
                        Info.Width,
                        Info.Height,
                        true);
                    break;

                case Target.Texture2DMultisampleArray:
                    GL.TexStorage3DMultisample(
                        TextureTargetMultisample3d.Texture2DMultisampleArray,
                        Info.Samples,
                        internalFormat,
                        Info.Width,
                        Info.Height,
                        Info.Depth,
                        true);
                    break;

                case Target.Texture3D:
                    GL.TexStorage3D(
                        TextureTarget3d.Texture3D,
                        levels,
                        internalFormat,
                        Info.Width,
                        Info.Height,
                        Info.Depth);
                    break;

                case Target.Cubemap:
                    GL.TexStorage2D(
                        TextureTarget2d.TextureCubeMap,
                        levels,
                        internalFormat,
                        Info.Width,
                        Info.Height);
                    break;

                case Target.CubemapArray:
                    GL.TexStorage3D(
                        (TextureTarget3d)All.TextureCubeMapArray,
                        levels,
                        internalFormat,
                        Info.Width,
                        Info.Height,
                        Info.Depth);
                    break;

                default:
                    Logger.Debug?.Print(LogClass.Gpu, $"Invalid or unsupported texture target: {target}.");
                    break;
            }
        }

        public ITexture CreateDefaultView()
        {
            DefaultView = CreateView(Info, 0, 0);

            return DefaultView;
        }

        public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            IncrementViewsCount();

            return new TextureView(_renderer, this, info, firstLayer, firstLevel);
        }

        private void IncrementViewsCount()
        {
            _viewsCount++;
        }

        public void DecrementViewsCount()
        {
            // If we don't have any views, then the storage is now useless, delete it.
            if (--_viewsCount == 0)
            {
                if (DefaultView == null)
                {
                    Dispose();
                }
                else
                {
                    // If the default view still exists, we can put it into the resource pool.
                    Release();
                }
            }
        }

        /// <summary>
        /// Release the TextureStorage to the resource pool without disposing its handle.
        /// </summary>
        public void Release()
        {
            _viewsCount = 1; // When we are used again, we will have the default view.

            _renderer.ResourcePool.AddTexture((TextureView)DefaultView);
        }

        public void DeleteDefault()
        {
            DefaultView = null;
        }

        public void Dispose()
        {
            StereoStorage?.Dispose();
            StereoStorage = null;
            DefaultView = null;
            if (LayeredHandle != 0) { GL.DeleteTexture(LayeredHandle); LayeredHandle = 0; }

            if (Handle != 0)
            {
                GL.DeleteTexture(Handle);

                Handle = 0;
            }
        }
    }
}
