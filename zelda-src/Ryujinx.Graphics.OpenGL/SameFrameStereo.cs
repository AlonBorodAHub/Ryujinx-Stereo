using OpenTK.Graphics.OpenGL;
using Ryujinx.Graphics.OpenGL.Image;
using System;
using System.Collections.Generic;
using System.IO;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.OpenGL
{
    // Both draws consume the same guest command and input buffers before the queue advances.
    internal static class SameFrameStereo
    {
        [ThreadStatic] internal static bool Executing;
        [ThreadStatic] private static Dictionary<int, TextureBase> _textures;
        [ThreadStatic] private static Dictionary<int, TextureBase> _images;
        [ThreadStatic] private static List<(int Unit, int Handle)> _sampledBindings;
        [ThreadStatic] private static List<(int Unit, int Handle, SizedInternalFormat Format)> _imageBindings;
        private static readonly HashSet<int> _tracedPrograms = new();
        private static readonly HashSet<(int Handle, int Eye)> _tracedTextures = new();
        internal static void BeginTrace() { _tracedPrograms.Clear(); _tracedTextures.Clear(); }
        private static void Trace(Program program)
        {
            if (!NativeStereo.TraceFrame || program == null || !_tracedPrograms.Add(program.Handle)) return;
            string path = Environment.GetEnvironmentVariable("RYU_STEREO_TRACE");
            Directory.CreateDirectory(path);
            using var log = File.AppendText(Path.Combine(path, "bindings.txt"));
            log.WriteLine($"program={program.Handle} projectedDepth={program.StereoProjectedDepth} geometryPass={NativeStereo.GeometryPass} shadowCorrection={program.ShadowReconstruction} deferredCorrection={program.DeferredReconstruction}");
            GL.GetProgram(program.Handle, GetProgramParameterName.ActiveUniformBlocks, out int blockCount);
            for (int block = 0; block < blockCount; block++)
            {
                GL.GetActiveUniformBlock(program.Handle, block, ActiveUniformBlockParameter.UniformBlockBinding, out int binding);
                GL.GetInteger(GetIndexedPName.UniformBufferBinding, binding, out int buffer);
                GL.GetInteger64(GetIndexedPName.UniformBufferStart, binding, out long offset);
                GL.GetInteger64(GetIndexedPName.UniformBufferSize, binding, out long length);
                int bytes = (int)System.Math.Min(length, 4096);
                if (buffer == 0 || bytes < 16) continue;
                byte[] data = new byte[bytes];
                GL.GetNamedBufferSubData(buffer, (nint)offset, bytes, data);
                File.WriteAllBytes(Path.Combine(path, $"p{program.Handle}_ubo{binding}.bin"), data);
            }
            if (program.SamplerUnits == null || _textures == null) return;
            foreach (int unit in program.SamplerUnits)
            {
                if (!_textures.TryGetValue(unit, out var texture) || texture is not TextureView view) continue;
                log.WriteLine($"  unit={unit} handle={view.Handle} size={view.Width}x{view.Height} format={view.Format} target={view.Target} stereo={view.HasStereoStorage}");
                bool scalar = view.Format.IsDepthOrStencil() || view.Format == Format.R16Float || view.Format == Format.R32Float;
                bool scene = view.Width > view.Height * 1.5f && view.Width < view.Height * 2.1f;
                if (view.Target != Target.Texture2D || view.Width * (long)view.Height > 8000000 || view.Width < 256 || !(scalar || (scene && view.HasStereoStorage))) continue;
                GL.GetInteger(GetPName.PixelPackBufferBinding, out int pack);
                GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
                try
                {
                    for (int eye = 0; eye < 2; eye++)
                    {
                        NativeStereo.SetEye(eye);
                        int handle = view.Handle;
                        if (!_tracedTextures.Add((handle, eye))) continue;
                        string fileName = $"p{program.Handle}_u{unit}_h{handle}_eye{eye}_{view.Width}x{view.Height}_{view.Format}";
                        if (scalar)
                        {
                            float[] values = new float[view.Width * view.Height];
                            GL.GetTextureImage(handle, 0, view.Format.IsDepthOrStencil() ? PixelFormat.DepthComponent : PixelFormat.Red, PixelType.Float, values.Length * sizeof(float), values);
                            byte[] bytes = new byte[values.Length * sizeof(float)];
                            System.Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                            File.WriteAllBytes(Path.Combine(path, fileName + ".f32"), bytes);
                        }
                        else
                        {
                            byte[] bytes = new byte[view.Width * view.Height * 4];
                            GL.GetTextureImage(handle, 0, PixelFormat.Rgba, PixelType.UnsignedByte, bytes.Length, bytes);
                            File.WriteAllBytes(Path.Combine(path, fileName + ".rgba8"), bytes);
                        }
                    }
                }
                finally { NativeStereo.SetEye(0); GL.BindBuffer(BufferTarget.PixelPackBuffer, pack); }
            }
        }
        internal static long StorageCount;
        internal static long TextureBindingRevision;
        internal static long DrawPairs;
        internal static long LayeredDrawPairs;
        internal static bool IsCharacterShadow(Program program, TextureBase unitZero)
        {
            bool character = false, camera = false;
            if (program?.SamplerUnits == null) return false;
            foreach (int unit in program.SamplerUnits)
            {
                TextureBase texture = null;
                if (unit == 0) texture = unitZero;
                else _textures?.TryGetValue(unit, out texture);
                if (texture is not TextureView view || view.Target != Target.Texture2D) continue;
                character |= view.Width == 384 && view.Height == 384 && view.Format == Format.R16Float;
                camera |= view.HasStereoStorage && view.Width > view.Height * 1.5f && view.Width < view.Height * 2.1f;
            }
            return character && camera;
        }
        internal static uint ScreenSpaceMask(HashSet<int> units, TextureBase unitZero)
        {
            uint mask = 0;
            if (units == null) return mask;
            foreach (int unit in units)
            {
                TextureBase texture = null;
                if (unit == 0) texture = unitZero;
                else _textures?.TryGetValue(unit, out texture);
                if (unit >= 32 || texture is not TextureView view || !view.HasStereoStorage || view.Target != Target.Texture2D) continue;
                float aspect = (float)view.Width / view.Height;
                float viewportAspect = NativeStereo.ViewportWidth / NativeStereo.ViewportHeight;
                if (aspect > 1.5f && aspect < 2.1f && System.MathF.Abs(aspect - viewportAspect) < 0.02f && view.Width >= 256)
                    mask |= 1u << unit;
            }
            return mask;
        }
        internal static bool SamplesSceneDepth(Program program)
        {
            if (_textures == null || program?.SamplerUnits == null) return false;
            foreach (int unit in program.SamplerUnits)
                if (_textures.TryGetValue(unit, out var texture) && texture is TextureView view && view.HasStereoStorage &&
                    view.Target == Target.Texture2D && (view.Format == Format.R32Float || view.Format.IsDepthOrStencil()) &&
                    view.Width >= 256 && view.Width > view.Height * 1.5f && view.Width < view.Height * 2.1f) return true;
            return false;
        }

        internal static bool NeedsStereoCompute(Program program)
        {
            if (program?.SamplerUnits != null && _textures != null)
                foreach (int unit in program.SamplerUnits)
                    if (_textures.TryGetValue(unit, out var texture) && texture is TextureView view && view.HasStereoStorage) return true;
            if (program?.ImageUnits != null && _images != null)
                foreach (int unit in program.ImageUnits)
                    if (_images.TryGetValue(unit, out var texture) && texture is TextureView view && view.HasStereoStorage) return true;
            return false;
        }
        internal static void TrackTexture(int unit, TextureBase texture)
        {
            if (!NativeStereo.Enabled || BackgroundContextWorker.InBackground) return;
            _textures ??= new();
            if (texture == null)
            {
                if (_textures.Remove(unit)) TextureBindingRevision++;
            }
            else if (!_textures.TryGetValue(unit, out var previous) || !ReferenceEquals(previous, texture))
            {
                _textures[unit] = texture;
                TextureBindingRevision++;
            }
        }
        internal static void TrackImage(int unit, TextureBase texture)
        {
            if (!NativeStereo.Enabled || BackgroundContextWorker.InBackground) return;
            _images ??= new();
            if (texture == null) _images.Remove(unit);
            else
            {
                _images[unit] = texture;
            }
        }
        private static void PrepareImages(Program program)
        {
            if (program?.ImageUnits == null || _images == null) return;
            foreach (int unit in program.ImageUnits)
                if (_images.TryGetValue(unit, out var texture)) (texture as TextureView)?.MakeStereo();
        }
        private static Program _boundResourceProgram;
        private static long _boundResourceRevision = -1, _boundResourceStorage = -1;
        private static int _boundResourceEye = -1;
        private static void BindResources(Program program)
        {
            if (NativeStereo.CacheStereoResources && program?.ImageUnits.Count == 0 && ReferenceEquals(_boundResourceProgram, program) &&
                _boundResourceRevision == TextureBindingRevision && _boundResourceStorage == StorageCount &&
                _boundResourceEye == NativeStereo.CurrentEye) return;
            var sampled = _sampledBindings ??= new(32);
            var images = _imageBindings ??= new(16);
            sampled.Clear();
            images.Clear();
            if (program?.SamplerUnits != null && _textures != null)
                foreach (int unit in program.SamplerUnits)
                    if (_textures.TryGetValue(unit, out var texture) && texture is TextureView view && view.HasStereoStorage)
                        sampled.Add((unit, texture.Handle));
            if (program?.ImageUnits != null && _images != null)
                foreach (int unit in program.ImageUnits)
                    if (_images.TryGetValue(unit, out var texture) && texture is TextureView view && view.HasStereoStorage)
                        images.Add((unit, texture.Handle, FormatTable.GetImageFormat(texture.Format)));
            // Allocation/view creation uses unit zero internally, so restore it even for a mono asset.
            if (_textures != null && _textures.TryGetValue(0, out var unitZero)) sampled.Add((0, unitZero.Handle));
            foreach (var entry in sampled)
            {
                TextureBase source = null;
                if (_textures != null && _textures.TryGetValue(entry.Unit, out source) &&
                    source is TextureView view && view.Target == Target.Texture2D && program?.SamplesEyeArray(entry.Unit) == true)
                {
                    // The array view is the shader's complete binding. Binding the
                    // unused 2D view first only adds another driver call per draw.
                    StereoArrayBinding.Bind(entry.Unit, source);
                }
                else
                {
                    GL.BindTextureUnit(entry.Unit, entry.Handle);
                    if (source != null) StereoArrayBinding.Bind(entry.Unit, source);
                }
            }
            foreach (var entry in images)
                if (entry.Format != 0)
                    GL.BindImageTexture(entry.Unit, entry.Handle, 0, true, 0, TextureAccess.ReadWrite, entry.Format);
            GL.ActiveTexture(TextureUnit.Texture0);
            _boundResourceProgram = program;
            _boundResourceRevision = TextureBindingRevision;
            _boundResourceStorage = StorageCount;
            _boundResourceEye = NativeStereo.CurrentEye;
        }
        internal static void Execute(Action draw, Action bindFramebuffer = null, Program program = null, Framebuffer framebuffer = null, bool retainDrawTarget = false)
        {
            if (!NativeStereo.Enabled || Executing) { draw(); return; }
            if (!NativeStereo.RenderSecondEye)
            {
                Executing = true;
                NativeStereo.SetEye(0);
                try { bindFramebuffer?.Invoke(); draw(); }
                finally { Executing = false; }
                return;
            }
            Executing = true;
            try
            {
                bool captureOutput = NativeStereo.TraceFrame && program != null && !_tracedPrograms.Contains(program.Handle);
                Trace(program);
                PrepareImages(program);
                bool alternate = retainDrawTarget && NativeStereo.AlternatingEyes && !NativeStereo.TraceFrame;
                int firstEye = alternate && framebuffer?.RightEyeIsBound == true ? 1 : 0;
                for (int step = 0; step < 2; step++)
                {
                    int eye = firstEye ^ step;
                    NativeStereo.SetEye(eye);
                    bindFramebuffer?.Invoke();
                    if (eye == 0 && step == 0 && !NativeStereo.ArraySampling)
                    {
                        // Guest bindings and the previous pair's restore already select left-eye resources.
                        // Texture allocation may have used unit zero internally.
                        if (_textures != null && _textures.TryGetValue(0, out var zero)) GL.BindTextureUnit(0, zero.Handle);
                        GL.ActiveTexture(TextureUnit.Texture0);
                    }
                    else BindResources(program);
                    draw();
                    if (captureOutput) framebuffer?.TraceOutput(program.Handle, DrawPairs);
                }
                if (NativeStereo.LayeredTarget) LayeredDrawPairs++;
                DrawPairs++;
            }
            finally
            {
                NativeStereo.SetEye(0);
                if (!retainDrawTarget || !NativeStereo.AlternatingEyes || NativeStereo.TraceFrame)
                    bindFramebuffer?.Invoke();
                BindResources(program);
                Executing = false;
            }
        }
        private static Program _batchResourceProgram;
        private static long _batchResourceRevision = -1, _batchResourceStorage = -1;
        private static bool _unbatchableResource;
        private static bool HasUnbatchableEyeTexture(Program program)
        {
            if (!NativeStereo.ArraySampling) return NeedsStereoCompute(program);
            if (NativeStereo.CacheStereoResources && ReferenceEquals(_batchResourceProgram, program) &&
                _batchResourceRevision == TextureBindingRevision && _batchResourceStorage == StorageCount) return _unbatchableResource;
            bool unbatchable = false;
            if (_textures != null)
                foreach (int unit in program.SamplerUnits)
                    if (_textures.TryGetValue(unit, out var texture) && texture is TextureView view && view.HasStereoStorage &&
                        (!view.CanSampleEyeLayers || !program.SamplesEyeArray(unit))) { unbatchable = true; break; }
            _batchResourceProgram = program;
            _batchResourceRevision = TextureBindingRevision;
            _batchResourceStorage = StorageCount;
            _unbatchableResource = unbatchable;
            return unbatchable;
        }
        internal static long InstancedDrawPairs;
        internal static bool TryExecuteInstanced(Action draw, Action bindFramebuffer, Program program)
        {
            if (!NativeStereo.RenderSecondEye || !NativeStereo.InstancedStorage || !NativeStereo.InstancedRendering || !NativeStereo.LayeredRendering ||
                NativeStereo.TraceFrame || Executing || program?.HasInstancedStereo != true ||
                program.ImageUnits.Count != 0 || HasUnbatchableEyeTexture(program)) return false;

            NativeStereo.SetEye(0);
            bindFramebuffer();
            if (!NativeStereo.LayeredTarget) return false;
            Executing = true;
            NativeStereo.InstancedDraw = true;
            try
            {
                BindResources(program);
                draw();
                DrawPairs++;
                LayeredDrawPairs++;
                InstancedDrawPairs++;
                return true;
            }
            finally
            {
                NativeStereo.InstancedDraw = false;
                NativeStereo.SetEye(0);
                // An instanced pair never selected physical right-eye bindings.
                // The draw's ordinary texture setup already preserves the left eye.
                if (!NativeStereo.CacheStereoResources) BindResources(program);
                Executing = false;
            }
        }
        internal static bool CopyBoth(TextureView source, TextureView destination, Action copy)
        {
            if (!NativeStereo.Enabled || !NativeStereo.RenderSecondEye || Executing || BackgroundContextWorker.InBackground ||
                !(source?.HasStereoStorage == true || destination?.HasStereoStorage == true)) return false;
            destination?.MakeStereo();
            Execute(copy);
            return true;
        }
    }
}


