using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Ryujinx.Graphics.OpenGL
{
    // Experimental same-state geometry stereo with isolated per-eye image storage.
    internal sealed class NativeStereo : IDisposable
    {
        private static readonly bool OriginalShaderDiagnostic = Environment.GetEnvironmentVariable("RYU_TOTK_ORIGINAL_SHADER_DIAGNOSTIC") == "1" && string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase);
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable("RYU_GEOMETRY_STEREO") == "1";
        private static float Separation = ReadFloat("RYU_STEREO_SEPARATION", 0.012f, 0f, 0.1f);
        internal static float Convergence = ReadFloat("RYU_STEREO_CONVERGENCE", 600f, 0f, 10000f);
        private static float HudPopout;
        internal static int ShadowProbe;
        internal static int ShadowProbeProgram;
        internal static float HudEyeOffset => Separation > 0 ? (CurrentEye == 0 ? 1f : -1f) * HudPopout / 100f : 0;
        internal static bool GeometryPass = true;
        internal static bool TraceFrame;
        internal static bool DisableCharacterShadows;
        internal static bool DirectOutput = true;
        internal static bool AlternatingEyes;
        internal static readonly bool LayeredStorage = Environment.GetEnvironmentVariable("RYU_STEREO_LAYERED") == "1";
        internal static bool LayeredRendering = true;
        internal static bool LayeredTarget;
        internal static readonly bool InstancedStorage = LayeredStorage && Environment.GetEnvironmentVariable("RYU_STEREO_INSTANCED") == "1";
        internal static readonly bool ArraySampling = InstancedStorage && Environment.GetEnvironmentVariable("RYU_STEREO_ARRAY_SAMPLING") == "1";
        internal static bool InstancedRendering = true;
        internal static bool GuestInstancedRendering;
        internal static bool BufferMultiBind = true;
        internal static bool CacheRenderTargets = true;
        internal static bool CacheBufferBindings = true;
        internal static bool CacheStereoResources = true;
        internal static bool RenderSecondEye = true;
        internal static float CommandFlushMilliseconds;
        internal static bool CacheVertexBindings;
        internal static bool InstancedDraw;
        internal static float ViewportX, ViewportY, ViewportWidth = 1, ViewportHeight = 1;
        [ThreadStatic] private static int _eye;
        internal static int CurrentEye => _eye;
        internal static void SetEye(int eye) => _eye = eye;
        private static int _rewritten;
        internal static float EyeShift => (Volatile.Read(ref _eye) == 0 ? -1f : 1f) * Separation;
        private readonly int[] _textures = new int[2];
        private readonly int[] _framebuffers = new int[2];
        private int _width, _height, _frame;
        private long _lastPresent;
        private readonly double[] _frameIntervals = new double[120];
        private readonly NativeLeiaOutput _nativeLeia = new();
        private readonly SbsFrameCapture _recording = new();

        private static DateTime _settingsStamp;
        static NativeStereo() { if (Enabled) ReloadSettings(); }
        private static void ReloadSettings()
        {
            string path = Environment.GetEnvironmentVariable("RYU_STEREO_SETTINGS");
            if (string.IsNullOrEmpty(path)) path = Path.Combine(AppContext.BaseDirectory, "StereoSettings.json");
            try
            {
                if (!File.Exists(path)) return;
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _settingsStamp) return;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("separation", out var s) && s.TryGetSingle(out float separation) && float.IsFinite(separation)) Separation = Math.Clamp(separation, 0, 0.1f);
                if (doc.RootElement.TryGetProperty("convergence", out var c) && c.TryGetSingle(out float convergence) && float.IsFinite(convergence)) Convergence = Math.Clamp(convergence, 0, 10000f);
                DisableCharacterShadows = doc.RootElement.TryGetProperty("disable_character_shadows", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("direct_output", out var output)) DirectOutput = output.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("alternating_eyes", out var alternating)) AlternatingEyes = alternating.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("layered_framebuffer", out var layered)) LayeredRendering = layered.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("instanced_eyes", out var instanced)) InstancedRendering = instanced.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("guest_instanced_eyes", out var guest)) GuestInstancedRendering = guest.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("buffer_multibind", out var multi)) BufferMultiBind = multi.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("cache_vertex_bindings", out var vertexBindings)) CacheVertexBindings = vertexBindings.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("handoff_spin_us", out var spinUs) && spinUs.TryGetInt32(out int us)) Ryujinx.Graphics.GAL.Multithreading.ThreadedRenderer.IdleSpinMicroseconds = Math.Clamp(us, 0, 100);
                if (doc.RootElement.TryGetProperty("command_flush_ms", out var flushMs) && flushMs.TryGetSingle(out float ms) && float.IsFinite(ms)) CommandFlushMilliseconds = Math.Clamp(ms, 0, 10);
                if (doc.RootElement.TryGetProperty("render_second_eye", out var secondEye)) RenderSecondEye = secondEye.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("direct_panel_output", out var panel)) NativeLeiaOutput.Requested = panel.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("cache_stereo_resources", out var stereoResources)) CacheStereoResources = stereoResources.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("cache_buffer_bindings", out var bufferBindings)) CacheBufferBindings = bufferBindings.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("cache_render_targets", out var targets)) CacheRenderTargets = targets.ValueKind == System.Text.Json.JsonValueKind.True;
                HudPopout = doc.RootElement.TryGetProperty("hud_popout_percent", out var hud) && hud.TryGetSingle(out float h) && float.IsFinite(h) ? Math.Clamp(h, 0, 2) : 0;
                ShadowProbe = doc.RootElement.TryGetProperty("diagnostic_shadow_probe", out var probe) && probe.TryGetInt32(out int probeValue) && probeValue >= 0 && probeValue <= 9 ? probeValue : 0;
                ShadowProbeProgram = doc.RootElement.TryGetProperty("diagnostic_shadow_program", out var programProbe) && programProbe.TryGetInt32(out int programValue) ? Math.Max(0, programValue) : 0;
                StereoOutput.Load(doc.RootElement);
                NativeLeiaOutput.Requested = StereoOutput.Mode == "panel";
                _settingsStamp = stamp;
                Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo settings: separation={Separation}, convergence={Convergence}, disableCharacterShadows={DisableCharacterShadows}");
            }
            catch (IOException) { }
            catch (System.Text.Json.JsonException) { }
        }

        private static float ReadFloat(string name, float fallback, float min, float max)
        {
            return float.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
                ? Math.Clamp(value, min, max) : fallback;
        }

        internal static string Rewrite(string code, ShaderStage stage, ShaderSource[] stages)
        {
            if (OriginalShaderDiagnostic) return code;
            ShaderStage finalStage = stages.Any(s => s.Stage == ShaderStage.Geometry) ? ShaderStage.Geometry :
                stages.Any(s => s.Stage == ShaderStage.TessellationEvaluation) ? ShaderStage.TessellationEvaluation : ShaderStage.Vertex;
            if (Enabled && stage == ShaderStage.Fragment)
            {
                ShaderSource vertex = stages.FirstOrDefault(s => s.Stage == ShaderStage.Vertex);
                if (!string.IsNullOrEmpty(vertex.Code))
                {
                    code = TotkCharacterThreshold.Rewrite(code);
                    code = TotkHistogram.Rewrite(code);
                    code = TotkCharacterLighting.Rewrite(code);
                    code = TotkShadowFiltering.Rewrite(code);
                    code = TotkCloudDepth.Rewrite(code, vertex.Code);
                    code = ZeldaStereoReconstruction.Rewrite(code, vertex.Code);
                    code = StereoShadowReconstruction.Rewrite(code, vertex.Code);
                    code = StereoDeferredReconstruction.Rewrite(code, vertex.Code);
                    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(vertex.Code.Replace("\r\n", "\n"))));
                    if (StereoProjectionFixes.Outputs.ContainsKey(hash))
                    {
                        var sample = new Regex(@"texture\(fp_t_tcb_10,\s*vec2\([^()]*\)\)");
                        if (sample.IsMatch(code))
                        {
                            code = sample.Replace(code, "texture(fp_t_tcb_10, gl_FragCoord.xy * ryu_stereo_depth_viewport.xy + ryu_stereo_depth_viewport.zw)");
                            int mainOffset = code.IndexOf("void main()", StringComparison.Ordinal);
                            code = code.Insert(mainOffset, "uniform vec4 ryu_stereo_depth_viewport;\n");
                            Logger.Notice.Print(LogClass.Gpu, "GeometryStereo: depth sampling uses raster coordinates");
                        }
                    }
                }
                return StereoArraySampling.Rewrite(code, stage);
            }
            if (!Enabled || stage != finalStage) return StereoArraySampling.Rewrite(code, stage);
            var main = new Regex(@"\bvoid\s+main\s*\(\s*(?:void\s*)?\)");
            if (!main.IsMatch(code)) return code;
            string correction = "if (abs(gl_Position.w - 1.0) > 0.0001) gl_Position.x += ryu_geometry_stereo.x * (gl_Position.w - ryu_geometry_stereo.y);\n";
            string projectedVaryings = stage == ShaderStage.Vertex ? StereoProjectedVaryings.Correction(code) : "";
            if (stage == ShaderStage.Vertex && projectedVaryings.Length == 0) projectedVaryings = TotkCloudDepth.ProjectedCorrection(code);
            if (stage == ShaderStage.Vertex && projectedVaryings.Length == 0) (code, projectedVaryings) = ZeldaStereoEffects.ExtendProjectedCoordinates(code);
            correction += projectedVaryings;
            bool hudProjection = stage == ShaderStage.Vertex && (TotkHudProjection.Matches(code) || SharedHudProjection.Matches(code));
            if (hudProjection) correction += "gl_Position.x += ryu_hud_offset * gl_Position.w;\n";
            if (hudProjection && TotkHudProjection.IsPlainColorOverlay(stages.FirstOrDefault(s => s.Stage == ShaderStage.Fragment).Code))
                correction = TotkHudProjection.SaveOverlayBounds + correction + TotkHudProjection.CoverOverlayEdges;
            bool projectedDepth = stage == ShaderStage.Vertex && TotkCloudDepth.Matches(code);
            if (stage == ShaderStage.Vertex)
            {
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(code.Replace("\r\n", "\n"))));
                if (StereoProjectionFixes.Outputs.TryGetValue(hash, out string[] projected))
                {
                    projectedDepth = true;
                    Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo: correcting projected depth coordinates: {hash.Substring(0, 12)}");
                }
            }
            if (LayeredStorage)
            {
                // Only the final vertex-processing stage selects the framebuffer layer.
                // Keep guest layer assignments intact when using a conventional framebuffer.
                bool guestLayer = Regex.IsMatch(code, @"gl_Layer\s*=");
                bool pairable = InstancedStorage && stage == ShaderStage.Vertex &&
                    stages.All(s => s.Language == Ryujinx.Graphics.Shader.Translation.TargetLanguage.Glsl &&
                        !Regex.IsMatch(s.Code, @"\bbuffer\b|\bimage\w*\b|\batomic\w*\b"));
                if (pairable)
                {
                    // Preserve the guest instance ID in the doubled-instance path; ordinary draws are unchanged.
                    code = Regex.Replace(code, @"\bgl_InstanceID\b", "(ryu_stereo_layer == -2 ? (gl_InstanceID >> 1) : gl_InstanceID)");
                    correction = correction.Replace("ryu_geometry_stereo.x", "(ryu_geometry_stereo.x * ((ryu_stereo_layer == -2 && (gl_InstanceID & 1) != 0) ? -1.0 : 1.0))");
                    if (hudProjection) correction = correction.Replace("ryu_hud_offset", "(ryu_hud_offset * ((ryu_stereo_layer == -2 && (gl_InstanceID & 1) != 0) ? -1.0 : 1.0))");
                    if (ArraySampling)
                        correction = correction.Replace("ryu_stereo_projected_varying.x", "(ryu_stereo_projected_varying.x * ((ryu_stereo_layer == -2 && (gl_InstanceID & 1) != 0) ? -1.0 : 1.0))");
                    correction += "if (ryu_stereo_layer == -2) gl_Layer = gl_InstanceID & 1; else " +
                        (guestLayer ? "if (ryu_stereo_layer >= 0) gl_Layer = ryu_stereo_layer;\n" : "gl_Layer = max(ryu_stereo_layer, 0);\n");
                }
                else correction += guestLayer ? "if (ryu_stereo_layer >= 0) gl_Layer = ryu_stereo_layer;\n" : "gl_Layer = max(ryu_stereo_layer, 0);\n";
                if (!code.Contains("GL_ARB_shader_viewport_layer_array", StringComparison.Ordinal))
                    code = code.Insert(code.IndexOf('\n') + 1, "#extension GL_ARB_shader_viewport_layer_array : require\n");
                // Declare before helper functions, which may also read the guest instance ID.
                int declaration = 0;
                foreach (string line in code.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#", StringComparison.Ordinal)) break;
                    declaration += line.Length + 1;
                }
                code = code.Insert(Math.Min(declaration, code.Length), "uniform int ryu_stereo_layer;\n");
                if (pairable)
                {
                    code = code.Replace("ryu_stereo_layer", "ryu_stereo_layer_pairable");
                    correction = correction.Replace("ryu_stereo_layer", "ryu_stereo_layer_pairable");
                }
            }
            if (stage == ShaderStage.Geometry)
            {
                var emit = new Regex(@"\bEmitVertex\s*\(\s*\)");
                if (!emit.IsMatch(code)) return code;
                code = emit.Replace(code, "ryu_geometry_emit()");
                int insert = main.Match(code).Index;
                code = code.Insert(insert, "uniform vec2 ryu_geometry_stereo;\nvoid ryu_geometry_emit() { vec4 original = gl_Position;\n" +
                    correction + "EmitVertex(); gl_Position = original; }\n");
            }
            else
            {
                code = main.Replace(code, "void ryu_geometry_original_main()", 1);
                code += "\nuniform vec2 ryu_geometry_stereo;\n" + (projectedVaryings.Length > 0 ? "uniform vec2 ryu_stereo_projected_varying;\n" : "") + "void main() { ryu_geometry_original_main();\n" + correction + "}\n";
            }
            if (hudProjection) code = code.Insert(code.IndexOf("void ryu_geometry_original_main", StringComparison.Ordinal), "uniform float ryu_hud_offset;\n");
            if (projectedDepth) code = code.Replace("ryu_geometry_stereo", "ryu_geometry_stereo_projected");
            Interlocked.Increment(ref _rewritten);
            return StereoArraySampling.Rewrite(code, stage);
        }

        private void EnsureSize(int width, int height)
        {
            if (_width == width && _height == height) return;
            DisposeEyeTargets();
            _width = width; _height = height;
            for (int i = 0; i < 2; i++)
            {
                GL.CreateTextures(TextureTarget.Texture2D, 1, out _textures[i]);
                GL.TextureStorage2D(_textures[i], 1, SizedInternalFormat.Rgba8, width, height);
                GL.CreateFramebuffers(1, out _framebuffers[i]);
                GL.NamedFramebufferTexture(_framebuffers[i], FramebufferAttachment.ColorAttachment0, _textures[i], 0);
                GL.NamedFramebufferReadBuffer(_framebuffers[i], ReadBufferMode.ColorAttachment0);
                GL.NamedFramebufferDrawBuffer(_framebuffers[i], DrawBufferMode.ColorAttachment0);
                if (GL.CheckNamedFramebufferStatus(_framebuffers[i], FramebufferTarget.Framebuffer) != FramebufferStatus.FramebufferComplete)
                    throw new InvalidOperationException("Geometry stereo eye framebuffer is incomplete.");
            }
        }

        private static long _nextSettingsPoll;
        internal static void RefreshOutputSettings()
        {
            long now = Environment.TickCount64;
            if (now < _nextSettingsPoll) return;
            _nextSettingsPoll = now + 250;
            ReloadSettings();
        }

        internal void Present(Action<int> renderEye, int width, int height, Action swapBuffers, bool direct = false)
        {
            if (width <= 0 || height <= 0) return;
            if (!direct) EnsureSize(StereoOutput.EyeWidth(width), StereoOutput.EyeHeight(height));
            if (StereoPerformance.Enabled)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_lastPresent != 0) _frameIntervals[_frame % 120] = (now - _lastPresent) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _lastPresent = now;
            }
            try
            {
                for (int eye = 0; eye < 2; eye++)
                {
                    SetEye(eye);
                    renderEye(direct ? 0 : _framebuffers[eye]);
                }
                SetEye(0);
                GL.Disable(IndexedEnableCap.ScissorTest, 0);
                GL.Disable(EnableCap.RasterizerDiscard);
                if (!direct) StereoOutput.Compose(_framebuffers, _width, _height, width, height, 0, recording: true);
                if (TraceFrame)
                {
                    string capturePath = Environment.GetEnvironmentVariable("RYU_STEREO_CAPTURE");
                    if (!string.IsNullOrEmpty(capturePath)) Capture(capturePath);
                }
                TraceFrame = false;
                string tracePath = Environment.GetEnvironmentVariable("RYU_STEREO_TRACE");
                if (_frame % 120 == 0 && !string.IsNullOrEmpty(tracePath) && File.Exists(Path.Combine(tracePath, "trace.request")))
                {
                    SameFrameStereo.BeginTrace();
                    TraceFrame = true;
                    File.Delete(Path.Combine(tracePath, "trace.request"));
                }
                StereoGpuTiming.Frame();
                _frame++;
                if (_frame % 120 == 0)
                {

                    StereoPerformance.Report();
                    if (StereoPerformance.Enabled)
                    {
                    var intervals = (double[])_frameIntervals.Clone();
                    Array.Sort(intervals);
                    Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo pacing ms: median={intervals[60]:F2}, p95={intervals[114]:F2}, max={intervals[119]:F2}");
                    Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo SAME FRAME: {_frame} pairs, {SameFrameStereo.DrawPairs} dual commands, {SameFrameStereo.StorageCount} eye allocations, {_rewritten} shaders; {width}x{height}; separation={Separation}, convergence={Convergence}; directOutput={direct}; renderSecondEye={RenderSecondEye}; genericBufferBindSkips={StereoBufferBinding.GenericBindSkips}; vertexBindingCache={CacheVertexBindings}; vertexBindSkips={VertexArray.SkippedVertexBinds}; divisorSkips={VertexArray.SkippedDivisorBinds}; indexBindSkips={VertexArray.SkippedIndexBinds}; handoffSpinUs={Ryujinx.Graphics.GAL.Multithreading.ThreadedRenderer.IdleSpinMicroseconds}; commandFlushMs={CommandFlushMilliseconds}; commandFlushes={StereoCommandFlush.Count}; commandFlushCpuMs={StereoCommandFlush.CpuMilliseconds:F2}; alternatingEyes={AlternatingEyes}; framebufferBinds={FramebufferBinding.DrawBinds}; layeredStorage={LayeredStorage}; layeredRendering={LayeredRendering}; layeredDraws={SameFrameStereo.LayeredDrawPairs}; instancedEyes={InstancedRendering}; instancedDraws={SameFrameStereo.InstancedDrawPairs}; arraySampling={ArraySampling}");
                    }
                    string capture = Environment.GetEnvironmentVariable("RYU_STEREO_CAPTURE");
                    if (!string.IsNullOrWhiteSpace(capture) && File.Exists(Path.Combine(capture, "motion.request")))
                    {
                        StereoMotionCapture.Begin(capture);
                        File.Delete(Path.Combine(capture, "motion.request"));
                    }
                    if (!string.IsNullOrWhiteSpace(capture) && File.Exists(Path.Combine(capture, "capture.request")))
                    {
                        Capture(capture);
                        File.Delete(Path.Combine(capture, "capture.request"));
                    }
                }
                FramebufferBinding.Bind(FramebufferTarget.Framebuffer, 0);
                StereoMotionCapture.Frame(width, height);
                try { _recording.Frame(width, height); }
                catch (Exception recordingError) { _recording.Dispose(); SbsRecording.Notify("GPU recording stopped: " + recordingError.Message, false); }
                if (!direct && !StereoOutput.Legacy) StereoOutput.Compose(_framebuffers, _width, _height, width, height, 0, textures: _textures);
                FramebufferBinding.Bind(FramebufferTarget.Framebuffer, 0);
                _nativeLeia.Present(width, height);
                swapBuffers();
            }
            finally { SetEye(0); }
        }

        private void Capture(string directory)
        {
            Directory.CreateDirectory(directory);
            int oldPack = GL.GetInteger(GetPName.PackAlignment);
            int oldPackBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, _framebuffers[i]);
                    byte[] rgb = new byte[_width * _height * 3];
                    GL.ReadPixels(0, 0, _width, _height, PixelFormat.Rgb, PixelType.UnsignedByte, rgb);
                    using var file = File.Create(Path.Combine(directory, i == 0 ? "left.ppm" : "right.ppm"));
                    file.Write(Encoding.ASCII.GetBytes($"P6\n{_width} {_height}\n255\n"));
                    file.Write(rgb);
                }
            }
            finally
            {
                GL.PixelStore(PixelStoreParameter.PackAlignment, oldPack);
                GL.BindBuffer(BufferTarget.PixelPackBuffer, oldPackBuffer);
            }
        }

        public void Dispose()
        {
            _recording.Dispose();
            _nativeLeia.Dispose();
            StereoOutput.Dispose();
            DisposeEyeTargets();
        }

        private void DisposeEyeTargets()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_framebuffers[i] != 0) FramebufferBinding.Delete(_framebuffers[i]);
                if (_textures[i] != 0) GL.DeleteTexture(_textures[i]);
                _framebuffers[i] = _textures[i] = 0;

            }
            _width = _height = 0;

        }
    }
}
