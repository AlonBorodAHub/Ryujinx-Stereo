using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.OpenGL
{
    // Sends the completed geometric stereo image directly to the panel runtime.
    // No desktop/window capture, CPU readback, or second application is involved.
    internal sealed class NativeLeiaOutput : IDisposable
    {
        private static readonly bool Allowed = Environment.GetEnvironmentVariable("RYU_LEIA_DIRECT") == "1";
        internal static bool Requested;
        [DllImport("ZeldaLeiaBridge", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint zl_create(int activatePanel);
        [DllImport("ZeldaLeiaBridge", CallingConvention = CallingConvention.Cdecl)]
        private static extern int zl_weave(nint handle, uint texture, int width, int height, nint ready);
        [DllImport("ZeldaLeiaBridge", CallingConvention = CallingConvention.Cdecl)]
        private static extern void zl_destroy(nint handle);
        [DllImport("ZeldaLeiaBridge", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint zl_error();
        private nint _bridge;
        private int _texture, _width, _height;
        private bool _failed;
        private System.Threading.EventWaitHandle _panelReady;
        private bool _readySignalAttempted;
        private static string Error => Marshal.PtrToStringUTF8(zl_error()) ?? "Unknown native panel output error";

        internal void Present(int width, int height)
        {
            if (!Allowed || !Requested)
            {
                Dispose();
                _failed = false;
                return;
            }
            if (_failed) return;
            try
            {
                if (_bridge == nint.Zero)
                {
                    _bridge = zl_create(1);
                    if (_bridge == nint.Zero) throw new InvalidOperationException(Error);
                    Logger.Notice.Print(LogClass.Gpu, "GeometryStereo native LeiaSR panel output initialized; window capture bypassed");
                }
                if (_texture == 0 || width != _width || height != _height)
                {
                    if (_texture != 0) GL.DeleteTexture(_texture);
                    GL.CreateTextures(TextureTarget.Texture2D, 1, out _texture);
                    GL.TextureStorage2D(_texture, 1, SizedInternalFormat.Rgba8, width, height);
                    GL.TextureParameter(_texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    GL.TextureParameter(_texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    GL.TextureParameter(_texture, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    GL.TextureParameter(_texture, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                    _width = width; _height = height;
                }
                // NativeStereo has bound the finished SBS backbuffer here.
                GL.CopyTextureSubImage2D(_texture, 0, 0, 0, 0, 0, width, height);
                nint ready = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
                GL.Flush();
                try
                {
                    if (zl_weave(_bridge, (uint)_texture, width, height, ready) == 0)
                        throw new InvalidOperationException(Error);
                }
                finally { GL.DeleteSync(ready); }
                // Signal only after the native weaver has successfully presented.
                // One named-event update per activation; no per-frame synchronization.
                if (!_readySignalAttempted)
                {
                    _readySignalAttempted = true;
                    try
                    {
                        _panelReady = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset,
                            @"Local\RyujinxStereo.PanelReady." + Environment.ProcessId);
                        _panelReady.Set();
                    }
                    catch (Exception signalError) when (signalError is System.IO.IOException || signalError is UnauthorizedAccessException)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, "Native panel handoff signal unavailable: " + signalError.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is InvalidOperationException)
            {
                Logger.Error?.Print(LogClass.Gpu, $"GeometryStereo native panel output disabled: {ex.Message}");
                Dispose();
                _failed = true;
            }
        }

        public void Dispose()
        {
            if (_panelReady != null) { _panelReady.Reset(); _panelReady.Dispose(); _panelReady = null; }
            _readySignalAttempted = false;
            if (_bridge != nint.Zero)
            {
                zl_destroy(_bridge);
                _bridge = nint.Zero;
                Logger.Notice.Print(LogClass.Gpu, "GeometryStereo native LeiaSR panel output released");
            }
            if (_texture != 0) { GL.DeleteTexture(_texture); _texture = 0; }
            _width = _height = 0;
        }
    }
}
