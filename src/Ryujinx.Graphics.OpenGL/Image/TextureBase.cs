using OpenTK.Graphics.OpenGL;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.OpenGL.Image
{
    class TextureBase
    {
        protected int _handle;
        public virtual int Handle { get => _handle; protected set => _handle = value; }

        public TextureCreateInfo Info { get; }

        public int Width => Info.Width;
        public int Height => Info.Height;

        public Target Target => Info.Target;
        public Format Format => Info.Format;

        public TextureBase(TextureCreateInfo info)
        {
            Info = info;

            Handle = GL.GenTexture();
        }

        public void Bind(int unit)
        {
            Bind(Target.Convert(), unit);
        }

        protected void Bind(TextureTarget target, int unit)
        {
            SameFrameStereo.TrackTexture(unit, this);
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(target, Handle);
            if (target == TextureTarget.Texture2DArray) StereoArrayBinding.Invalidate(unit);
            StereoArrayBinding.Bind(unit, this);
        }

        public static void ClearBinding(int unit)
        {
            SameFrameStereo.TrackTexture(unit, null);
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTextureUnit(unit, 0);
            StereoArrayBinding.Invalidate(unit);
        }
    }
}
