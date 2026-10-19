using OpenTK.Graphics.OpenGL;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.OpenGL;
using SPB.Graphics;
using SPB.Graphics.Exceptions;
using SPB.Graphics.OpenGL;
using SPB.Platform;
using SPB.Platform.WGL;
using SPB.Windowing;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Ava.UI.Renderer
{
    public class EmbeddedWindowOpenGL : EmbeddedWindow
    {
        private SwappableNativeWindowBase _window;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetSwapInterval();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int SetSwapInterval(int interval);
        private GetSwapInterval _getSwapInterval;
        private SetSwapInterval _setSwapInterval;
        private bool _swapIntervalInitialized;
        private int _originalSwapInterval;
        private int _appliedSwapInterval = int.MinValue;
        public volatile int RequestedSwapInterval = -1;

        public OpenGLContextBase Context { get; set; }

        protected override void OnWindowDestroying()
        {
            Context.Dispose();

            base.OnWindowDestroying();
        }

        public override void OnWindowCreated()
        {
            base.OnWindowCreated();

            if (OperatingSystem.IsWindows())
            {
                _window = new WGLWindow(new NativeHandle(WindowHandle));
            }
            else if (OperatingSystem.IsLinux())
            {
                _window = X11Window;
            }
            else
            {
                throw new PlatformNotSupportedException();
            }

            OpenGLContextFlags flags = OpenGLContextFlags.Compat;
            if (ConfigurationState.Instance.Logger.GraphicsDebugLevel != GraphicsDebugLevel.None)
            {
                flags |= OpenGLContextFlags.Debug;
            }

            FramebufferFormat graphicsMode = Environment.OSVersion.Platform == PlatformID.Unix ? new FramebufferFormat(new ColorFormat(8, 8, 8, 0), 16, 0, ColorFormat.Zero, 0, 2, false) : FramebufferFormat.Default;

            Context = PlatformHelper.CreateOpenGLContext(graphicsMode, 3, 3, flags);

            Context.Initialize(_window);
            Context.MakeCurrent(_window);

            GL.LoadBindings(new OpenTKBindingsContext(Context.GetProcAddress));

            Context.MakeCurrent(null);
        }

        public void MakeCurrent(bool unbind = false, bool shouldThrow = true)
        {
            try
            {
                Context?.MakeCurrent(!unbind ? _window : null);
            }
            catch (ContextException e)
            {
                if (shouldThrow)
                {
                    throw;
                }

                Logger.Warning?.Print(LogClass.UI, $"Failed to {(!unbind ? "bind" : "unbind")} OpenGL context: {e}");
            }
        }

        public void SwapBuffers()
        {
            // Host display synchronization is separate from the guest Switch
            // timing mode. Apply only on this window's current GL thread.
            if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("RYU_GEOMETRY_STEREO") == "1")
            {
                if (!_swapIntervalInitialized)
                {
                    _swapIntervalInitialized = true;
                    nint getter = Context.GetProcAddress("wglGetSwapIntervalEXT");
                    nint setter = Context.GetProcAddress("wglSwapIntervalEXT");
                    if (getter != nint.Zero && setter != nint.Zero)
                    {
                        _getSwapInterval = Marshal.GetDelegateForFunctionPointer<GetSwapInterval>(getter);
                        _setSwapInterval = Marshal.GetDelegateForFunctionPointer<SetSwapInterval>(setter);
                        _originalSwapInterval = _getSwapInterval();
                        Logger.Notice.Print(LogClass.UI, $"GeometryStereo host swap interval: initial={_originalSwapInterval}");
                    }
                }
                int requested = RequestedSwapInterval;
                if (_setSwapInterval != null && requested != _appliedSwapInterval)
                {
                    int interval = requested == -1 ? _originalSwapInterval : Math.Clamp(requested, 0, 1);
                    if (_setSwapInterval(interval) != 0)
                    {
                        _appliedSwapInterval = requested;
                        Logger.Notice.Print(LogClass.UI, $"GeometryStereo host swap interval: requested={requested}, actual={_getSwapInterval()}");
                    }
                    else
                    {
                        Logger.Warning?.Print(LogClass.UI, $"GeometryStereo host swap interval request {requested} failed");
                        _appliedSwapInterval = requested;
                    }
                }
            }
            _window?.SwapBuffers();
        }

        public void InitializeBackgroundContext(IRenderer renderer)
        {
            (renderer as OpenGLRenderer)?.InitializeBackgroundContext(SPBOpenGLContext.CreateBackgroundContext(Context));

            MakeCurrent();
        }
    }
}
