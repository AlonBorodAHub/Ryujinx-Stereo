using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Ryujinx.Graphics.OpenGL
{
    class Program : IProgram
    {
        private const int MaxShaderLogLength = 2048;

        public int Handle { get; private set; }

        public bool IsLinked
        {
            get
            {
                if (_status == ProgramLinkStatus.Incomplete)
                {
                    CheckProgramLink(true);
                }

                return _status == ProgramLinkStatus.Success;
            }
        }

        private ProgramLinkStatus _status = ProgramLinkStatus.Incomplete;
        private int[] _shaderHandles;
        private int _stereoLocation = -2;
        private int _stereoLayerLocation = -2, _stereoLayerValue = int.MinValue;
        private bool _stereoPairable;
        private int _arrayEyeLocation = -2, _arrayEyeValue = int.MinValue;
        private readonly HashSet<int> _arraySamplerUnits = new();
        internal bool SamplesEyeArray(int unit) { ReadTextureBindings(); return _arraySamplerUnits.Contains(unit); }
        internal bool HasLayeredStereo
        {
            get
            {
                if (!NativeStereo.LayeredStorage || !IsLinked) return false;
                if (_stereoLayerLocation == -2)
                {
                    _stereoLayerLocation = GL.GetUniformLocation(Handle, "ryu_stereo_layer");
                    if (_stereoLayerLocation < 0)
                    {
                        _stereoLayerLocation = GL.GetUniformLocation(Handle, "ryu_stereo_layer_pairable");
                        _stereoPairable = _stereoLayerLocation >= 0;
                    }
                }
                return _stereoLayerLocation >= 0;
            }
        }
        internal bool HasInstancedStereo
        {
            get
            {
                if (!HasLayeredStereo || !_stereoPairable) return false;
                if (_stereoReconstruction == -2) _stereoReconstruction = GL.GetUniformLocation(Handle, "ryu_stereo_reconstruction");
                if (_stereoDeferred == -2) _stereoDeferred = GL.GetUniformLocation(Handle, "ryu_stereo_deferred");
                return NativeStereo.ArraySampling || (_stereoReconstruction < 0 && _stereoDeferred < 0);
            }
        }
        private int _stereoDepthViewport = -2;
        private int _stereoReconstruction = -2;
        private int _stereoDeferred = -2;
        private int _stereoProjectedVarying = -2;
        internal bool DeferredReconstruction => _stereoDeferred >= 0;
        private readonly float[] _stereoValues = new float[12];
        private int _stereoValueMask;
        private long _screenBindingRevision = -1, _screenStorageRevision = -1;
        private float _screenViewportWidth, _screenViewportHeight;
        private Image.TextureBase _screenUnitZero;
        private uint _screenSamplingMask;
        private bool _stereoProjectedDepth;
        private long _effectBindingRevision = -1, _effectStorageRevision = -1;
        private bool _effectSamplesDepth;
        internal bool StereoProjectedDepth
        {
            get
            {
                if (_stereoProjectedDepth) return true;
                if (_stereoProjectedVarying == -2) _stereoProjectedVarying = GL.GetUniformLocation(Handle, "ryu_stereo_projected_varying");
                if (_stereoProjectedVarying < 0) return false;
                if (_effectBindingRevision != SameFrameStereo.TextureBindingRevision || _effectStorageRevision != SameFrameStereo.StorageCount)
                {
                    _effectSamplesDepth = SameFrameStereo.SamplesSceneDepth(this);
                    _effectBindingRevision = SameFrameStereo.TextureBindingRevision;
                    _effectStorageRevision = SameFrameStereo.StorageCount;
                }
                return _effectSamplesDepth;
            }
        }
        internal bool ShadowReconstruction => _stereoReconstruction >= 0;
        private HashSet<int> _samplerUnits;
        private HashSet<int> _imageUnits;
        internal HashSet<int> SamplerUnits { get { ReadTextureBindings(); return _samplerUnits; } }
        internal HashSet<int> ImageUnits { get { ReadTextureBindings(); return _imageUnits; } }
        private void ReadTextureBindings()
        {
            if (_samplerUnits != null) return;
            _samplerUnits = new(); _imageUnits = new();
            if (!IsLinked) { _samplerUnits = null; _imageUnits = null; return; }
            GL.GetProgram(Handle, GetProgramParameterName.ActiveUniforms, out int count);
            for (int i = 0; i < count; i++)
            {
                string name = GL.GetActiveUniform(Handle, i, out int size, out ActiveUniformType type);
                string kind = type.ToString();
                bool sampled = kind.Contains("Sampler", StringComparison.Ordinal);
                bool image = kind.Contains("Image", StringComparison.Ordinal);
                if (!sampled && !image) continue;
                int location = GL.GetUniformLocation(Handle, name);
                if (location < 0) continue;
                GL.GetUniform(Handle, location, out int binding);
                for (int j = 0; j < size; j++)
                {
                    (sampled ? _samplerUnits : _imageUnits).Add(binding + j);
                    if (type == ActiveUniformType.Sampler2DArray) _arraySamplerUnits.Add(binding + j);
                }
            }
        }


        public int FragmentOutputMap { get; }

        public Program(ShaderSource[] shaders, int fragmentOutputMap)
        {
            Handle = GL.CreateProgram();

            GL.ProgramParameter(Handle, ProgramParameterName.ProgramBinaryRetrievableHint, 1);

            _shaderHandles = new int[shaders.Length];
            bool hasFragmentShader = false;

            for (int index = 0; index < shaders.Length; index++)
            {
                ShaderSource shader = shaders[index];
                if (NativeStereo.Enabled && shader.Stage == ShaderStage.Vertex && shader.Language == TargetLanguage.Glsl)
                {
                    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(shader.Code.Replace("\r\n", "\n"))));
                    _stereoProjectedDepth |= StereoProjectionFixes.Outputs.ContainsKey(hash);
                }
                string dumpPath = Environment.GetEnvironmentVariable("RYU_STEREO_SHADER_DUMP");
                if (NativeStereo.Enabled && !string.IsNullOrEmpty(dumpPath) && shader.Language == TargetLanguage.Glsl)
                {
                    System.IO.Directory.CreateDirectory(dumpPath);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dumpPath, $"{Handle}_{shader.Stage}.glsl"), shader.Code);
                }

                if (shader.Stage == ShaderStage.Fragment)
                {
                    hasFragmentShader = true;
                }

                int shaderHandle = GL.CreateShader(shader.Stage.Convert());

                switch (shader.Language)
                {
                    case TargetLanguage.Glsl:
                        GL.ShaderSource(shaderHandle, NativeStereo.Rewrite(shader.Code, shader.Stage, shaders));
                        GL.CompileShader(shaderHandle);
                        break;
                    case TargetLanguage.Spirv:
                        GL.ShaderBinary(1, ref shaderHandle, (BinaryFormat)All.ShaderBinaryFormatSpirVArb, shader.BinaryCode, shader.BinaryCode.Length);
                        GL.SpecializeShader(shaderHandle, "main", 0, (int[])null, (int[])null);
                        break;
                }

                GL.AttachShader(Handle, shaderHandle);

                _shaderHandles[index] = shaderHandle;
            }

            GL.LinkProgram(Handle);
            if (NativeStereo.Enabled && BackgroundContextWorker.InBackground) GL.Flush();

            FragmentOutputMap = hasFragmentShader ? fragmentOutputMap : 0;
        }

        public Program(ReadOnlySpan<byte> code, bool hasFragmentShader, int fragmentOutputMap)
        {
            Handle = GL.CreateProgram();

            if (code.Length >= 4)
            {
                BinaryFormat binaryFormat = (BinaryFormat)BinaryPrimitives.ReadInt32LittleEndian(code.Slice(code.Length - 4, 4));

                unsafe
                {
                    fixed (byte* ptr = code)
                    {
                        GL.ProgramBinary(Handle, binaryFormat, (nint)ptr, code.Length - 4);
                    }
                }
            }

            FragmentOutputMap = hasFragmentShader ? fragmentOutputMap : 0;
        }

        private void ResolveStereoLocation()
        {
            if (_stereoLocation == -2)
            {
                _stereoLocation = GL.GetUniformLocation(Handle, "ryu_geometry_stereo");
                if (_stereoLocation < 0)
                {
                    _stereoLocation = GL.GetUniformLocation(Handle, "ryu_geometry_stereo_projected");
                    _stereoProjectedDepth = _stereoLocation >= 0;
                }
            }
        }
        internal bool HasGeometryStereo
        {
            get { if (!IsLinked) return false; ResolveStereoLocation(); return _stereoLocation >= 0; }
        }
        private void StereoUniform2(int location, int offset, float x, float y)
        {
            if ((_stereoValueMask & (1 << offset)) != 0 && _stereoValues[offset] == x && _stereoValues[offset + 1] == y) return;
            GL.ProgramUniform2(Handle, location, x, y);
            _stereoValues[offset] = x; _stereoValues[offset + 1] = y;
            _stereoValueMask |= 1 << offset;
        }
        private uint CachedScreenMask(Image.TextureBase unitZero)
        {
            if (_screenBindingRevision != SameFrameStereo.TextureBindingRevision || _screenStorageRevision != SameFrameStereo.StorageCount ||
                _screenViewportWidth != NativeStereo.ViewportWidth || _screenViewportHeight != NativeStereo.ViewportHeight || !ReferenceEquals(_screenUnitZero, unitZero))
            {
                _screenSamplingMask = SameFrameStereo.ScreenSpaceMask(SamplerUnits, unitZero);
                _screenBindingRevision = SameFrameStereo.TextureBindingRevision;
                _screenStorageRevision = SameFrameStereo.StorageCount;
                _screenViewportWidth = NativeStereo.ViewportWidth; _screenViewportHeight = NativeStereo.ViewportHeight;
                _screenUnitZero = unitZero;
            }
            return _screenSamplingMask;
        }
        internal void ApplyNativeStereo(bool worldDraw, Image.TextureBase unitZero = null)
        {
            if (!NativeStereo.Enabled) return;
            ResolveStereoLocation();
            bool projectedDepth = StereoProjectedDepth;
            if (NativeStereo.ArraySampling)
            {
                if (_arrayEyeLocation == -2) _arrayEyeLocation = GL.GetUniformLocation(Handle, "ryu_stereo_sampling_eye");
                int arrayEye = NativeStereo.InstancedDraw ? -2 : NativeStereo.CurrentEye;
                if (_arrayEyeLocation >= 0 && _arrayEyeValue != arrayEye)
                {
                    GL.ProgramUniform1(Handle, _arrayEyeLocation, arrayEye);
                    _arrayEyeValue = arrayEye;
                }
            }
            if (HasLayeredStereo)
            {
                int layer = NativeStereo.LayeredTarget ? (NativeStereo.InstancedDraw ? -2 : NativeStereo.CurrentEye) : -1;
                if (layer != _stereoLayerValue) { GL.ProgramUniform1(Handle, _stereoLayerLocation, layer); _stereoLayerValue = layer; }
            }
            if (_stereoProjectedVarying == -2) _stereoProjectedVarying = GL.GetUniformLocation(Handle, "ryu_stereo_projected_varying");
            if (_stereoProjectedVarying >= 0)
            {
                bool active = NativeStereo.GeometryPass && (worldDraw || projectedDepth) && CachedScreenMask(unitZero) != 0;
                StereoUniform2(_stereoProjectedVarying, 2, active ? NativeStereo.EyeShift : 0f, NativeStereo.Convergence);
            }
            if (_stereoReconstruction == -2) _stereoReconstruction = GL.GetUniformLocation(Handle, "ryu_stereo_reconstruction");
            if (_stereoReconstruction >= 0) StereoUniform2(_stereoReconstruction, 4, NativeStereo.EyeShift, NativeStereo.Convergence);
            if (_stereoDeferred == -2) _stereoDeferred = GL.GetUniformLocation(Handle, "ryu_stereo_deferred");
            if (_stereoDeferred >= 0) StereoUniform2(_stereoDeferred, 6, NativeStereo.EyeShift, NativeStereo.Convergence);
            if (_stereoDepthViewport == -2) _stereoDepthViewport = GL.GetUniformLocation(Handle, "ryu_stereo_depth_viewport");
            if (_stereoDepthViewport >= 0)
            {
                float iw = 1f / MathF.Max(1, NativeStereo.ViewportWidth);
                float ih = 1f / MathF.Max(1, NativeStereo.ViewportHeight);
                float vx = -NativeStereo.ViewportX * iw, vy = -NativeStereo.ViewportY * ih;
                if ((_stereoValueMask & 256) == 0 || _stereoValues[8] != iw || _stereoValues[9] != ih || _stereoValues[10] != vx || _stereoValues[11] != vy)
                {
                    GL.ProgramUniform4(Handle, _stereoDepthViewport, iw, ih, vx, vy);
                    _stereoValues[8] = iw; _stereoValues[9] = ih; _stereoValues[10] = vx; _stereoValues[11] = vy;
                    _stereoValueMask |= 256;
                }
            }
            if (_stereoLocation >= 0)
                StereoUniform2(_stereoLocation, 0, NativeStereo.GeometryPass && (worldDraw || projectedDepth) ? NativeStereo.EyeShift : 0f, NativeStereo.Convergence);
        }

        public void Bind()
        {
            GL.UseProgram(Handle);
        }

        public ProgramLinkStatus CheckProgramLink(bool blocking)
        {
            if (!blocking && HwCapabilities.SupportsParallelShaderCompile)
            {
                GL.GetProgram(Handle, (GetProgramParameterName)ArbParallelShaderCompile.CompletionStatusArb, out int completed);

                if (completed == 0)
                {
                    return ProgramLinkStatus.Incomplete;
                }
            }

            GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int status);
            DeleteShaders();

            if (status == 0)
            {
                _status = ProgramLinkStatus.Failure;

                string log = GL.GetProgramInfoLog(Handle);

                if (log.Length > MaxShaderLogLength)
                {
                    log = log[..MaxShaderLogLength] + "...";
                }

                Logger.Warning?.Print(LogClass.Gpu, $"Shader linking failed: \n{log}");
            }
            else
            {
                _status = ProgramLinkStatus.Success;
                if (NativeStereo.Enabled) GL.Flush();
            }

            return _status;
        }

        public byte[] GetBinary()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            GL.GetProgram(Handle, (GetProgramParameterName)All.ProgramBinaryLength, out int size);

            byte[] data = new byte[size + 4];

            GL.GetProgramBinary(Handle, size, out _, out BinaryFormat binFormat, data);

            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(size, 4), (int)binFormat);

            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (NativeStereo.Enabled && ms > 8) Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo shader binary export: {ms:F1} ms, background={BackgroundContextWorker.InBackground}");
            return data;
        }

        private void DeleteShaders()
        {
            if (_shaderHandles != null)
            {
                foreach (int shaderHandle in _shaderHandles)
                {
                    GL.DetachShader(Handle, shaderHandle);
                    GL.DeleteShader(shaderHandle);
                }

                _shaderHandles = null;
            }
        }

        public void Dispose()
        {
            if (Handle != 0)
            {
                DeleteShaders();
                GL.DeleteProgram(Handle);

                Handle = 0;
            }
        }
    }
}
