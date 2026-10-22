using OpenTK.Graphics.OpenGL;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.OpenGL.Image;
using Ryujinx.Graphics.OpenGL.Queries;
using Ryujinx.Graphics.Shader;
using System;

namespace Ryujinx.Graphics.OpenGL
{
    class Pipeline : IPipeline, IDisposable
    {
        private const int SavedImages = 2;
        private static readonly bool GuestTextureBarrierTest =
            Environment.GetEnvironmentVariable("RYU_TOTK_TEXTURE_BARRIER_TEST") == "1" &&
            string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase);
        private bool _guestTextureBarrierLogged;

        private readonly DrawTextureEmulation _drawTexture;

        internal ulong DrawCount { get; private set; }

        private Program _program;

        private bool _rasterizerDiscard;

        private VertexArray _vertexArray;
        private Framebuffer _framebuffer;

        private nint _indexBaseOffset;

        private DrawElementsType _elementsType;

        private PrimitiveType _primitiveType;

        private int _stencilFrontMask;
        private bool _depthMask;
        private bool _depthTestEnable;
        private bool _nativeAlwaysDepth;
        private bool _stencilTestEnable;
        private bool _cullEnable;

        private float[] _viewportArray = [];
        private double[] _depthRangeArray = [];

        private int _boundDrawFramebuffer;
        private int _boundReadFramebuffer;

        private CounterQueueEvent _activeConditionalRender;

        private readonly Vector4<int>[] _fpIsBgra = new Vector4<int>[SupportBuffer.FragmentIsBgraCount];

        private readonly TextureBase[] _images;
        private TextureBase _unit0Texture;
        private Sampler _unit0Sampler;

        private FrontFaceDirection _frontFace;
        private ClipOrigin _clipOrigin;
        private ClipDepthMode _clipDepthMode;

        private uint _fragmentOutputMap;
        private uint _componentMasks;
        private uint _currentComponentMasks;
        private bool _advancedBlendEnable;

        private uint _scissorEnables;

        private bool _tfEnabled;
        private TransformFeedbackPrimitiveType _tfTopology;

        private readonly BufferHandle[] _tfbs;
        private readonly BufferRange[] _tfbTargets;

        private ColorF _blendConstant;

        internal Pipeline()
        {
            _drawTexture = new DrawTextureEmulation();
            _rasterizerDiscard = false;
            _clipOrigin = ClipOrigin.LowerLeft;
            _clipDepthMode = ClipDepthMode.NegativeOneToOne;

            _fragmentOutputMap = uint.MaxValue;
            _componentMasks = uint.MaxValue;

            _images = new TextureBase[SavedImages];

            _tfbs = new BufferHandle[Constants.MaxTransformFeedbackBuffers];
            _tfbTargets = new BufferRange[Constants.MaxTransformFeedbackBuffers];
        }

        public void Barrier()
        {
            GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
        }

        public void BeginTransformFeedback(PrimitiveTopology topology)
        {
            GL.BeginTransformFeedback(_tfTopology = topology.ConvertToTfType());
            _tfEnabled = true;
        }

        public void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
        {
            Buffer.Clear(destination, offset, size, value);
        }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            if (NativeStereo.Enabled && !SameFrameStereo.Executing && _framebuffer?.HasStereoTargets == true)
            {
                SameFrameStereo.Execute(() => ClearRenderTargetColor(index, layer, layerCount, componentMask, color), () => _framebuffer?.RebindStereoEye());
                return;
            }

            EnsureFramebuffer();

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer.RebindStereoEye();

            GL.ColorMask(
                index,
                (componentMask & 1) != 0,
                (componentMask & 2) != 0,
                (componentMask & 4) != 0,
                (componentMask & 8) != 0);

            float[] colors = [color.Red, color.Green, color.Blue, color.Alpha];

            if (layer != 0 || layerCount != _framebuffer.GetColorLayerCount(index))
            {
                for (int l = layer; l < layer + layerCount; l++)
                {
                    _framebuffer.AttachColorLayerForClear(index, l);

                    GL.ClearBuffer(OpenTK.Graphics.OpenGL.ClearBuffer.Color, index, colors);
                }

                _framebuffer.DetachColorLayerForClear(index);
            }
            else
            {
                GL.ClearBuffer(OpenTK.Graphics.OpenGL.ClearBuffer.Color, index, colors);
            }

            RestoreComponentMask(index);
        }

        public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            if (NativeStereo.Enabled && !SameFrameStereo.Executing && _framebuffer?.HasStereoTargets == true)
            {
                SameFrameStereo.Execute(() => ClearRenderTargetDepthStencil(layer, layerCount, depthValue, depthMask, stencilValue, stencilMask), () => _framebuffer?.RebindStereoEye());
                return;
            }

            EnsureFramebuffer();

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer.RebindStereoEye();

            bool stencilMaskChanged =
                stencilMask != 0 &&
                stencilMask != _stencilFrontMask;

            bool depthMaskChanged = depthMask && depthMask != _depthMask;

            if (stencilMaskChanged)
            {
                GL.StencilMaskSeparate(StencilFace.Front, stencilMask);
            }

            if (depthMaskChanged)
            {
                GL.DepthMask(depthMask);
            }

            if (layer != 0 || layerCount != _framebuffer.GetDepthStencilLayerCount())
            {
                for (int l = layer; l < layer + layerCount; l++)
                {
                    _framebuffer.AttachDepthStencilLayerForClear(l);

                    ClearDepthStencil(depthValue, depthMask, stencilValue, stencilMask);
                }

                _framebuffer.DetachDepthStencilLayerForClear();
            }
            else
            {
                ClearDepthStencil(depthValue, depthMask, stencilValue, stencilMask);
            }

            if (stencilMaskChanged)
            {
                GL.StencilMaskSeparate(StencilFace.Front, _stencilFrontMask);
            }

            if (depthMaskChanged)
            {
                GL.DepthMask(_depthMask);
            }
        }

        private static void ClearDepthStencil(float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            if (depthMask && stencilMask != 0)
            {
                GL.ClearBuffer(ClearBufferCombined.DepthStencil, 0, depthValue, stencilValue);
            }
            else if (depthMask)
            {
                GL.ClearBuffer(OpenTK.Graphics.OpenGL.ClearBuffer.Depth, 0, ref depthValue);
            }
            else if (stencilMask != 0)
            {
                GL.ClearBuffer(OpenTK.Graphics.OpenGL.ClearBuffer.Stencil, 0, ref stencilValue);
            }
        }

        public void CommandBufferBarrier()
        {
            GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit);
        }

        public void CopyBuffer(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
        {
            Buffer.Copy(source, destination, srcOffset, dstOffset, size);
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
            if (NativeStereo.Enabled && NativeStereo.RenderSecondEye && !SameFrameStereo.Executing && SameFrameStereo.NeedsStereoCompute(_program))
            {
                SameFrameStereo.Execute(() => DispatchCompute(groupsX, groupsY, groupsZ), () => _framebuffer?.RebindStereoEye(), _program, _framebuffer);
                return;
            }

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer?.RebindStereoEye();

            if (!_program.IsLinked)
            {
                Logger.Debug?.Print(LogClass.Gpu, "Dispatch error, shader not linked.");
                return;
            }

            if (NativeStereo.DisableCharacterShadows && SameFrameStereo.IsCharacterShadow(_program, _unit0Texture)) return;
            _program.ApplyNativeStereo(_depthTestEnable && (!_nativeAlwaysDepth || _depthMask) && !_tfEnabled && !_rasterizerDiscard, _unit0Texture);

            PrepareForDispatch();

            StereoGpuTiming.Begin(_program, "compute");
            GL.DispatchCompute(groupsX, groupsY, groupsZ);
            StereoGpuTiming.End();
            StereoCommandFlush.Tick();
        }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
        {
            if (NativeStereo.Enabled && !SameFrameStereo.Executing && !_tfEnabled && !_rasterizerDiscard && PrepareStereoDraw())
            {
                if ((instanceCount == 1 || (NativeStereo.GuestInstancedRendering && instanceCount > 1 && instanceCount <= int.MaxValue / 2)) && firstInstance == 0 && _vertexArray?.HasInstancedBuffers != true &&
                    _primitiveType != PrimitiveType.Quads && _primitiveType != PrimitiveType.QuadStrip &&
                    SameFrameStereo.TryExecuteInstanced(() => Draw(vertexCount, instanceCount * 2, firstVertex, 0),
                        () => _framebuffer?.RebindStereoEye(_program?.HasLayeredStereo == true), _program)) return;
                SameFrameStereo.Execute(() => Draw(vertexCount, instanceCount, firstVertex, firstInstance), () => _framebuffer?.RebindStereoEye(_program?.HasLayeredStereo == true), _program, _framebuffer, retainDrawTarget: true);
                return;
            }

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer?.RebindStereoEye();

            if (!_program.IsLinked)
            {
                Logger.Debug?.Print(LogClass.Gpu, "Draw error, shader not linked.");
                return;
            }

            if (NativeStereo.DisableCharacterShadows && SameFrameStereo.IsCharacterShadow(_program, _unit0Texture)) return;
            _program.ApplyNativeStereo(_depthTestEnable && (!_nativeAlwaysDepth || _depthMask) && !_tfEnabled && !_rasterizerDiscard, _unit0Texture);

            PreDraw(vertexCount);

            if (_primitiveType == PrimitiveType.Quads && !HwCapabilities.SupportsQuads)
            {
                DrawQuadsImpl(vertexCount, instanceCount, firstVertex, firstInstance);
            }
            else if (_primitiveType == PrimitiveType.QuadStrip && !HwCapabilities.SupportsQuads)
            {
                DrawQuadStripImpl(vertexCount, instanceCount, firstVertex, firstInstance);
            }
            else
            {
                DrawImpl(vertexCount, instanceCount, firstVertex, firstInstance);
            }

            PostDraw();
        }

        private static void DrawQuadsImpl(
            int vertexCount,
            int instanceCount,
            int firstVertex,
            int firstInstance)
        {
            // TODO: Instanced rendering.
            int quadsCount = vertexCount / 4;

            int[] firsts = new int[quadsCount];
            int[] counts = new int[quadsCount];

            for (int quadIndex = 0; quadIndex < quadsCount; quadIndex++)
            {
                firsts[quadIndex] = firstVertex + quadIndex * 4;
                counts[quadIndex] = 4;
            }

            GL.MultiDrawArrays(
                PrimitiveType.TriangleFan,
                firsts,
                counts,
                quadsCount);
        }

        private static void DrawQuadStripImpl(
            int vertexCount,
            int instanceCount,
            int firstVertex,
            int firstInstance)
        {
            int quadsCount = (vertexCount - 2) / 2;

            if (firstInstance != 0 || instanceCount != 1)
            {
                for (int quadIndex = 0; quadIndex < quadsCount; quadIndex++)
                {
                    GL.DrawArraysInstancedBaseInstance(PrimitiveType.TriangleFan, firstVertex + quadIndex * 2, 4, instanceCount, firstInstance);
                }
            }
            else
            {
                int[] firsts = new int[quadsCount];
                int[] counts = new int[quadsCount];

                firsts[0] = firstVertex;
                counts[0] = 4;

                for (int quadIndex = 1; quadIndex < quadsCount; quadIndex++)
                {
                    firsts[quadIndex] = firstVertex + quadIndex * 2;
                    counts[quadIndex] = 4;
                }

                GL.MultiDrawArrays(
                    PrimitiveType.TriangleFan,
                    firsts,
                    counts,
                    quadsCount);
            }
        }

        private void DrawImpl(
            int vertexCount,
            int instanceCount,
            int firstVertex,
            int firstInstance)
        {
            if (firstInstance == 0 && instanceCount == 1)
            {
                GL.DrawArrays(_primitiveType, firstVertex, vertexCount);
            }
            else if (firstInstance == 0)
            {
                GL.DrawArraysInstanced(_primitiveType, firstVertex, vertexCount, instanceCount);
            }
            else
            {
                GL.DrawArraysInstancedBaseInstance(
                    _primitiveType,
                    firstVertex,
                    vertexCount,
                    instanceCount,
                    firstInstance);
            }
        }

        public void DrawIndexed(
            int indexCount,
            int instanceCount,
            int firstIndex,
            int firstVertex,
            int firstInstance)
        {
            if (NativeStereo.Enabled && !SameFrameStereo.Executing && !_tfEnabled && !_rasterizerDiscard && PrepareStereoDraw())
            {
                if ((instanceCount == 1 || (NativeStereo.GuestInstancedRendering && instanceCount > 1 && instanceCount <= int.MaxValue / 2)) && firstInstance == 0 && _vertexArray?.HasInstancedBuffers != true &&
                    _primitiveType != PrimitiveType.Quads && _primitiveType != PrimitiveType.QuadStrip &&
                    SameFrameStereo.TryExecuteInstanced(() => DrawIndexed(indexCount, instanceCount * 2, firstIndex, firstVertex, 0),
                        () => _framebuffer?.RebindStereoEye(_program?.HasLayeredStereo == true), _program)) return;
                SameFrameStereo.Execute(() => DrawIndexed(indexCount, instanceCount, firstIndex, firstVertex, firstInstance), () => _framebuffer?.RebindStereoEye(_program?.HasLayeredStereo == true), _program, _framebuffer, retainDrawTarget: true);
                return;
            }

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer?.RebindStereoEye();

            if (!_program.IsLinked)
            {
                Logger.Debug?.Print(LogClass.Gpu, "Draw error, shader not linked.");
                return;
            }

            if (NativeStereo.DisableCharacterShadows && SameFrameStereo.IsCharacterShadow(_program, _unit0Texture)) return;
            _program.ApplyNativeStereo(_depthTestEnable && (!_nativeAlwaysDepth || _depthMask) && !_tfEnabled && !_rasterizerDiscard, _unit0Texture);

            PreDrawVbUnbounded();

            int indexElemSize = 1;

            switch (_elementsType)
            {
                case DrawElementsType.UnsignedShort:
                    indexElemSize = 2;
                    break;
                case DrawElementsType.UnsignedInt:
                    indexElemSize = 4;
                    break;
            }

            nint indexBaseOffset = _indexBaseOffset + firstIndex * indexElemSize;

            if (_primitiveType == PrimitiveType.Quads && !HwCapabilities.SupportsQuads)
            {
                DrawQuadsIndexedImpl(
                    indexCount,
                    instanceCount,
                    indexBaseOffset,
                    indexElemSize,
                    firstVertex,
                    firstInstance);
            }
            else if (_primitiveType == PrimitiveType.QuadStrip && !HwCapabilities.SupportsQuads)
            {
                DrawQuadStripIndexedImpl(
                    indexCount,
                    instanceCount,
                    indexBaseOffset,
                    indexElemSize,
                    firstVertex,
                    firstInstance);
            }
            else
            {
                DrawIndexedImpl(
                    indexCount,
                    instanceCount,
                    indexBaseOffset,
                    firstVertex,
                    firstInstance);
            }

            PostDraw();
        }

        private void DrawQuadsIndexedImpl(
            int indexCount,
            int instanceCount,
            nint indexBaseOffset,
            int indexElemSize,
            int firstVertex,
            int firstInstance)
        {
            int quadsCount = indexCount / 4;

            if (firstInstance != 0 || instanceCount != 1)
            {
                if (firstVertex != 0 && firstInstance != 0)
                {
                    for (int quadIndex = 0; quadIndex < quadsCount; quadIndex++)
                    {
                        GL.DrawElementsInstancedBaseVertexBaseInstance(
                            PrimitiveType.TriangleFan,
                            4,
                            _elementsType,
                            indexBaseOffset + quadIndex * 4 * indexElemSize,
                            instanceCount,
                            firstVertex,
                            firstInstance);
                    }
                }
                else if (firstInstance != 0)
                {
                    for (int quadIndex = 0; quadIndex < quadsCount; quadIndex++)
                    {
                        GL.DrawElementsInstancedBaseInstance(
                            PrimitiveType.TriangleFan,
                            4,
                            _elementsType,
                            indexBaseOffset + quadIndex * 4 * indexElemSize,
                            instanceCount,
                            firstInstance);
                    }
                }
                else
                {
                    for (int quadIndex = 0; quadIndex < quadsCount; quadIndex++)
                    {
                        GL.DrawElementsInstanced(
                            PrimitiveType.TriangleFan,
                            4,
                            _elementsType,
                            indexBaseOffset + quadIndex * 4 * indexElemSize,
                            instanceCount);
                    }
                }
            }
            else
            {
                nint[] indices = new nint[quadsCount];

                int[] counts = new int[quadsCount];

                int[] baseVertices = new int[quadsCount];

                for (int quadIndex = 0; quadIndex < quadsCount; quadIndex++)
                {
                    indices[quadIndex] = indexBaseOffset + quadIndex * 4 * indexElemSize;

                    counts[quadIndex] = 4;

                    baseVertices[quadIndex] = firstVertex;
                }

                GL.MultiDrawElementsBaseVertex(
                    PrimitiveType.TriangleFan,
                    counts,
                    _elementsType,
                    indices,
                    quadsCount,
                    baseVertices);
            }
        }

        private void DrawQuadStripIndexedImpl(
            int indexCount,
            int instanceCount,
            nint indexBaseOffset,
            int indexElemSize,
            int firstVertex,
            int firstInstance)
        {
            // TODO: Instanced rendering.
            int quadsCount = (indexCount - 2) / 2;

            nint[] indices = new nint[quadsCount];

            int[] counts = new int[quadsCount];

            int[] baseVertices = new int[quadsCount];

            indices[0] = indexBaseOffset;

            counts[0] = 4;

            baseVertices[0] = firstVertex;

            for (int quadIndex = 1; quadIndex < quadsCount; quadIndex++)
            {
                indices[quadIndex] = indexBaseOffset + quadIndex * 2 * indexElemSize;

                counts[quadIndex] = 4;

                baseVertices[quadIndex] = firstVertex;
            }

            GL.MultiDrawElementsBaseVertex(
                PrimitiveType.TriangleFan,
                counts,
                _elementsType,
                indices,
                quadsCount,
                baseVertices);
        }

        private void DrawIndexedImpl(
            int indexCount,
            int instanceCount,
            nint indexBaseOffset,
            int firstVertex,
            int firstInstance)
        {
            if (firstInstance == 0 && firstVertex == 0 && instanceCount == 1)
            {
                GL.DrawElements(_primitiveType, indexCount, _elementsType, indexBaseOffset);
            }
            else if (firstInstance == 0 && instanceCount == 1)
            {
                GL.DrawElementsBaseVertex(
                    _primitiveType,
                    indexCount,
                    _elementsType,
                    indexBaseOffset,
                    firstVertex);
            }
            else if (firstInstance == 0 && firstVertex == 0)
            {
                GL.DrawElementsInstanced(
                    _primitiveType,
                    indexCount,
                    _elementsType,
                    indexBaseOffset,
                    instanceCount);
            }
            else if (firstInstance == 0)
            {
                GL.DrawElementsInstancedBaseVertex(
                    _primitiveType,
                    indexCount,
                    _elementsType,
                    indexBaseOffset,
                    instanceCount,
                    firstVertex);
            }
            else if (firstVertex == 0)
            {
                GL.DrawElementsInstancedBaseInstance(
                    _primitiveType,
                    indexCount,
                    _elementsType,
                    indexBaseOffset,
                    instanceCount,
                    firstInstance);
            }
            else
            {
                GL.DrawElementsInstancedBaseVertexBaseInstance(
                    _primitiveType,
                    indexCount,
                    _elementsType,
                    indexBaseOffset,
                    instanceCount,
                    firstVertex,
                    firstInstance);
            }
        }

        public void DrawIndexedIndirect(BufferRange indirectBuffer)
        {
            if (NativeStereo.Enabled && !SameFrameStereo.Executing && !_tfEnabled && !_rasterizerDiscard && PrepareStereoDraw())
            {
                SameFrameStereo.Execute(() => DrawIndexedIndirect(indirectBuffer), () => _framebuffer?.RebindStereoEye(_program?.HasLayeredStereo == true), _program, _framebuffer, retainDrawTarget: true);
                return;
            }

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer?.RebindStereoEye();

            if (!_program.IsLinked)
            {
                Logger.Debug?.Print(LogClass.Gpu, "Draw error, shader not linked.");
                return;
            }

            if (NativeStereo.DisableCharacterShadows && SameFrameStereo.IsCharacterShadow(_program, _unit0Texture)) return;
            _program.ApplyNativeStereo(_depthTestEnable && (!_nativeAlwaysDepth || _depthMask) && !_tfEnabled && !_rasterizerDiscard, _unit0Texture);

            PreDrawVbUnbounded();

            _vertexArray.SetRangeOfIndexBuffer();

            GL.BindBuffer((BufferTarget)All.DrawIndirectBuffer, indirectBuffer.Handle.ToInt32());

            GL.DrawElementsIndirect(_primitiveType, _elementsType, (nint)indirectBuffer.Offset);

            _vertexArray.RestoreIndexBuffer();

            PostDraw();
        }

        public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            if (NativeStereo.Enabled && !SameFrameStereo.Executing && !_tfEnabled && !_rasterizerDiscard && PrepareStereoDraw())
            {
                SameFrameStereo.Execute(() => DrawIndexedIndirectCount(indirectBuffer, parameterBuffer, maxDrawCount, stride), () => _framebuffer?.RebindStereoEye(_program?.HasLayeredStereo == true), _program, _framebuffer, retainDrawTarget: true);
                return;
            }

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer?.RebindStereoEye();

            if (!_program.IsLinked)
            {
                Logger.Debug?.Print(LogClass.Gpu, "Draw error, shader not linked.");
                return;
            }

            if (NativeStereo.DisableCharacterShadows && SameFrameStereo.IsCharacterShadow(_program, _unit0Texture)) return;
            _program.ApplyNativeStereo(_depthTestEnable && (!_nativeAlwaysDepth || _depthMask) && !_tfEnabled && !_rasterizerDiscard, _unit0Texture);

            PreDrawVbUnbounded();

            _vertexArray.SetRangeOfIndexBuffer();

            GL.BindBuffer((BufferTarget)All.DrawIndirectBuffer, indirectBuffer.Handle.ToInt32());
            GL.BindBuffer((BufferTarget)All.ParameterBuffer, parameterBuffer.Handle.ToInt32());

            GL.MultiDrawElementsIndirectCount(
                _primitiveType,
                (All)_elementsType,
                (nint)indirectBuffer.Offset,
                (nint)parameterBuffer.Offset,
                maxDrawCount,
                stride);

            _vertexArray.RestoreIndexBuffer();

            PostDraw();
        }

        public void DrawIndirect(BufferRange indirectBuffer)
        {
            if (NativeStereo.Enabled && !SameFrameStereo.Executing && !_tfEnabled && !_rasterizerDiscard && PrepareStereoDraw())
            {
                SameFrameStereo.Execute(() => DrawIndirect(indirectBuffer), () => _framebuffer?.RebindStereoEye(_program?.HasLayeredStereo == true), _program, _framebuffer, retainDrawTarget: true);
                return;
            }

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer?.RebindStereoEye();

            if (!_program.IsLinked)
            {
                Logger.Debug?.Print(LogClass.Gpu, "Draw error, shader not linked.");
                return;
            }

            if (NativeStereo.DisableCharacterShadows && SameFrameStereo.IsCharacterShadow(_program, _unit0Texture)) return;
            _program.ApplyNativeStereo(_depthTestEnable && (!_nativeAlwaysDepth || _depthMask) && !_tfEnabled && !_rasterizerDiscard, _unit0Texture);

            PreDrawVbUnbounded();

            GL.BindBuffer((BufferTarget)All.DrawIndirectBuffer, indirectBuffer.Handle.ToInt32());

            GL.DrawArraysIndirect(_primitiveType, (nint)indirectBuffer.Offset);

            PostDraw();
        }

        public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            if (NativeStereo.Enabled && !SameFrameStereo.Executing && !_tfEnabled && !_rasterizerDiscard && PrepareStereoDraw())
            {
                SameFrameStereo.Execute(() => DrawIndirectCount(indirectBuffer, parameterBuffer, maxDrawCount, stride), () => _framebuffer?.RebindStereoEye(_program?.HasLayeredStereo == true), _program, _framebuffer, retainDrawTarget: true);
                return;
            }

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer?.RebindStereoEye();

            if (!_program.IsLinked)
            {
                Logger.Debug?.Print(LogClass.Gpu, "Draw error, shader not linked.");
                return;
            }

            if (NativeStereo.DisableCharacterShadows && SameFrameStereo.IsCharacterShadow(_program, _unit0Texture)) return;
            _program.ApplyNativeStereo(_depthTestEnable && (!_nativeAlwaysDepth || _depthMask) && !_tfEnabled && !_rasterizerDiscard, _unit0Texture);

            PreDrawVbUnbounded();

            GL.BindBuffer((BufferTarget)All.DrawIndirectBuffer, indirectBuffer.Handle.ToInt32());
            GL.BindBuffer((BufferTarget)All.ParameterBuffer, parameterBuffer.Handle.ToInt32());

            GL.MultiDrawArraysIndirectCount(
                _primitiveType,
                (nint)indirectBuffer.Offset,
                (nint)parameterBuffer.Offset,
                maxDrawCount,
                stride);

            PostDraw();
        }

        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
        {
            if (NativeStereo.Enabled && !SameFrameStereo.Executing && !_tfEnabled && !_rasterizerDiscard && PrepareStereoDraw(texture as TextureView))
            {
                SameFrameStereo.Execute(() => DrawTexture(texture, sampler, srcRegion, dstRegion), () => _framebuffer?.RebindStereoEye(), _program, _framebuffer, retainDrawTarget: true);
                return;
            }

            if (NativeStereo.Enabled && !SameFrameStereo.Executing) _framebuffer?.RebindStereoEye();

            if (texture is TextureView view && sampler is Sampler samp)
            {
                if (HwCapabilities.SupportsDrawTexture)
                {
                    GL.NV.DrawTexture(
                        view.Handle,
                        samp.Handle,
                        dstRegion.X1,
                        dstRegion.Y1,
                        dstRegion.X2,
                        dstRegion.Y2,
                        0,
                        srcRegion.X1 / view.Width,
                        srcRegion.Y1 / view.Height,
                        srcRegion.X2 / view.Width,
                        srcRegion.Y2 / view.Height);
                }
                else
                {
                    static void Disable(EnableCap cap, bool enabled)
                    {
                        if (enabled)
                        {
                            GL.Disable(cap);
                        }
                    }

                    static void Enable(EnableCap cap, bool enabled)
                    {
                        if (enabled)
                        {
                            GL.Enable(cap);
                        }
                    }

                    Disable(EnableCap.CullFace, _cullEnable);
                    Disable(EnableCap.StencilTest, _stencilTestEnable);
                    Disable(EnableCap.DepthTest, _depthTestEnable);

                    if (_depthMask)
                    {
                        GL.DepthMask(false);
                    }

                    if (_tfEnabled)
                    {
                        GL.EndTransformFeedback();
                    }

                    GL.ClipControl(ClipOrigin.UpperLeft, ClipDepthMode.NegativeOneToOne);

                    _drawTexture.Draw(
                        view,
                        samp,
                        dstRegion.X1,
                        dstRegion.Y1,
                        dstRegion.X2,
                        dstRegion.Y2,
                        srcRegion.X1 / view.Width,
                        srcRegion.Y1 / view.Height,
                        srcRegion.X2 / view.Width,
                        srcRegion.Y2 / view.Height);

                    _program?.Bind();
                    _unit0Sampler?.Bind(0);

                    RestoreViewport0();

                    Enable(EnableCap.CullFace, _cullEnable);
                    Enable(EnableCap.StencilTest, _stencilTestEnable);
                    Enable(EnableCap.DepthTest, _depthTestEnable);

                    if (_depthMask)
                    {
                        GL.DepthMask(true);
                    }

                    if (_tfEnabled)
                    {
                        GL.BeginTransformFeedback(_tfTopology);
                    }

                    RestoreClipControl();
                }
            }
        }

        public void EndTransformFeedback()
        {
            GL.EndTransformFeedback();
            _tfEnabled = false;
        }

        public void SetAlphaTest(bool enable, float reference, CompareOp op)
        {
            if (!enable)
            {
                GL.Disable(EnableCap.AlphaTest);
                return;
            }

            GL.AlphaFunc((AlphaFunction)op.Convert(), reference);
            GL.Enable(EnableCap.AlphaTest);
        }

        public void SetBlendState(AdvancedBlendDescriptor blend)
        {
            if (HwCapabilities.SupportsBlendEquationAdvanced)
            {
                GL.BlendEquation((BlendEquationMode)blend.Op.Convert());
                GL.NV.BlendParameter(NvBlendEquationAdvanced.BlendOverlapNv, (int)blend.Overlap.Convert());
                GL.NV.BlendParameter(NvBlendEquationAdvanced.BlendPremultipliedSrcNv, blend.SrcPreMultiplied ? 1 : 0);
                GL.Enable(EnableCap.Blend);
                _advancedBlendEnable = true;
            }
        }

        public void SetBlendState(int index, BlendDescriptor blend)
        {
            if (_advancedBlendEnable)
            {
                GL.Disable(EnableCap.Blend);
                _advancedBlendEnable = false;
            }

            if (!blend.Enable)
            {
                GL.Disable(IndexedEnableCap.Blend, index);
                return;
            }

            GL.BlendEquationSeparate(
                index,
                blend.ColorOp.Convert(),
                blend.AlphaOp.Convert());

            GL.BlendFuncSeparate(
                index,
                (BlendingFactorSrc)blend.ColorSrcFactor.Convert(),
                (BlendingFactorDest)blend.ColorDstFactor.Convert(),
                (BlendingFactorSrc)blend.AlphaSrcFactor.Convert(),
                (BlendingFactorDest)blend.AlphaDstFactor.Convert());

            EnsureFramebuffer();

            _framebuffer.SetDualSourceBlend(
                blend.ColorSrcFactor.IsDualSource() ||
                blend.ColorDstFactor.IsDualSource() ||
                blend.AlphaSrcFactor.IsDualSource() ||
                blend.AlphaDstFactor.IsDualSource());

            if (_blendConstant != blend.BlendConstant)
            {
                _blendConstant = blend.BlendConstant;

                GL.BlendColor(
                    blend.BlendConstant.Red,
                    blend.BlendConstant.Green,
                    blend.BlendConstant.Blue,
                    blend.BlendConstant.Alpha);
            }

            GL.Enable(IndexedEnableCap.Blend, index);
        }

        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
        {
            if ((enables & PolygonModeMask.Point) != 0)
            {
                GL.Enable(EnableCap.PolygonOffsetPoint);
            }
            else
            {
                GL.Disable(EnableCap.PolygonOffsetPoint);
            }

            if ((enables & PolygonModeMask.Line) != 0)
            {
                GL.Enable(EnableCap.PolygonOffsetLine);
            }
            else
            {
                GL.Disable(EnableCap.PolygonOffsetLine);
            }

            if ((enables & PolygonModeMask.Fill) != 0)
            {
                GL.Enable(EnableCap.PolygonOffsetFill);
            }
            else
            {
                GL.Disable(EnableCap.PolygonOffsetFill);
            }

            if (enables == 0)
            {
                return;
            }

            if (HwCapabilities.SupportsPolygonOffsetClamp)
            {
                GL.PolygonOffsetClamp(factor, units, clamp);
            }
            else
            {
                GL.PolygonOffset(factor, units);
            }
        }

        public void SetDepthClamp(bool clamp)
        {
            if (!clamp)
            {
                GL.Disable(EnableCap.DepthClamp);
                return;
            }

            GL.Enable(EnableCap.DepthClamp);
        }

        public void SetDepthMode(DepthMode mode)
        {
            ClipDepthMode depthMode = mode.Convert();

            if (_clipDepthMode != depthMode)
            {
                _clipDepthMode = depthMode;

                GL.ClipControl(_clipOrigin, depthMode);
            }
        }

        public void SetDepthTest(DepthTestDescriptor depthTest)
        {
            if (depthTest.TestEnable)
            {
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc((DepthFunction)depthTest.Func.Convert());
            }
            else
            {
                GL.Disable(EnableCap.DepthTest);
            }

            GL.DepthMask(depthTest.WriteEnable);
            _depthMask = depthTest.WriteEnable;
            _depthTestEnable = depthTest.TestEnable;
            _nativeAlwaysDepth = depthTest.Func == CompareOp.Always || depthTest.Func == CompareOp.AlwaysGl;
        }

        public void SetFaceCulling(bool enable, Face face)
        {
            _cullEnable = enable;

            if (!enable)
            {
                GL.Disable(EnableCap.CullFace);
                return;
            }

            GL.CullFace(face.Convert());

            GL.Enable(EnableCap.CullFace);
        }

        public void SetFrontFace(FrontFace frontFace)
        {
            SetFrontFace(_frontFace = frontFace.Convert());
        }

        public void SetImage(ShaderStage stage, int binding, ITexture texture)
        {
            SameFrameStereo.TrackImage(binding, texture as TextureBase);
            if ((uint)binding < SavedImages)
            {
                _images[binding] = texture as TextureBase;
            }

            if (texture == null)
            {
                GL.BindImageTexture(binding, 0, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba8);
                return;
            }

            TextureBase texBase = (TextureBase)texture;

            SizedInternalFormat format = FormatTable.GetImageFormat(texBase.Format);

            if (format != 0)
            {
                GL.BindImageTexture(binding, texBase.Handle, 0, true, 0, TextureAccess.ReadWrite, format);
            }
        }

        public void SetImageArray(ShaderStage stage, int binding, IImageArray array)
        {
            (array as ImageArray).Bind(binding);
        }

        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array)
        {
            throw new NotSupportedException("OpenGL does not support descriptor sets.");
        }

        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _elementsType = type.Convert();

            _indexBaseOffset = (nint)buffer.Offset;

            EnsureVertexArray();

            _vertexArray.SetIndexBuffer(buffer);
        }

        public void SetLogicOpState(bool enable, LogicalOp op)
        {
            if (enable)
            {
                GL.Enable(EnableCap.ColorLogicOp);

                GL.LogicOp((LogicOp)op.Convert());
            }
            else
            {
                GL.Disable(EnableCap.ColorLogicOp);
            }
        }

        public void SetMultisampleState(MultisampleDescriptor multisample)
        {
            if (multisample.AlphaToCoverageEnable)
            {
                GL.Enable(EnableCap.SampleAlphaToCoverage);

                if (multisample.AlphaToOneEnable)
                {
                    GL.Enable(EnableCap.SampleAlphaToOne);
                }
                else
                {
                    GL.Disable(EnableCap.SampleAlphaToOne);
                }

                if (HwCapabilities.SupportsAlphaToCoverageDitherControl)
                {
                    GL.NV.AlphaToCoverageDitherControl(multisample.AlphaToCoverageDitherEnable
                        ? NvAlphaToCoverageDitherControl.AlphaToCoverageDitherEnableNv
                        : NvAlphaToCoverageDitherControl.AlphaToCoverageDitherDisableNv);
                }
            }
            else
            {
                GL.Disable(EnableCap.SampleAlphaToCoverage);
            }
        }

        public void SetLineParameters(float width, bool smooth)
        {
            if (smooth)
            {
                GL.Enable(EnableCap.LineSmooth);
            }
            else
            {
                GL.Disable(EnableCap.LineSmooth);
            }

            GL.LineWidth(width);
        }

        public unsafe void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel)
        {
            GL.PatchParameter(PatchParameterInt.PatchVertices, vertices);

            fixed (float* pOuterLevel = defaultOuterLevel)
            {
                GL.PatchParameter(PatchParameterFloat.PatchDefaultOuterLevel, pOuterLevel);
            }

            fixed (float* pInnerLevel = defaultInnerLevel)
            {
                GL.PatchParameter(PatchParameterFloat.PatchDefaultInnerLevel, pInnerLevel);
            }
        }

        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin)
        {
            // GL_POINT_SPRITE was deprecated in core profile 3.2+ and causes GL_INVALID_ENUM when set.
            // As we don't know if the current context is core or compat, it's safer to keep this code.
            if (enablePointSprite)
            {
                GL.Enable(EnableCap.PointSprite);
            }
            else
            {
                GL.Disable(EnableCap.PointSprite);
            }

            if (isProgramPointSize)
            {
                GL.Enable(EnableCap.ProgramPointSize);
            }
            else
            {
                GL.Disable(EnableCap.ProgramPointSize);
            }

            GL.PointParameter(origin == Origin.LowerLeft
                ? PointSpriteCoordOriginParameter.LowerLeft
                : PointSpriteCoordOriginParameter.UpperLeft);

            // Games seem to set point size to 0 which generates a GL_INVALID_VALUE
            // From the spec, GL_INVALID_VALUE is generated if size is less than or equal to 0.
            GL.PointSize(Math.Max(float.Epsilon, size));
        }

        public void SetPolygonMode(GAL.PolygonMode frontMode, GAL.PolygonMode backMode)
        {
            if (frontMode == backMode)
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, frontMode.Convert());
            }
            else
            {
                GL.PolygonMode(MaterialFace.Front, frontMode.Convert());
                GL.PolygonMode(MaterialFace.Back, backMode.Convert());
            }
        }

        public void SetPrimitiveRestart(bool enable, int index)
        {
            if (!enable)
            {
                GL.Disable(EnableCap.PrimitiveRestart);
                return;
            }

            GL.PrimitiveRestartIndex(index);

            GL.Enable(EnableCap.PrimitiveRestart);
        }

        public void SetPrimitiveTopology(PrimitiveTopology topology)
        {
            _primitiveType = topology.Convert();
        }

        public void SetProgram(IProgram program)
        {
            Program prg = (Program)program;

            if (_tfEnabled)
            {
                GL.EndTransformFeedback();
                prg.Bind();
                GL.BeginTransformFeedback(_tfTopology);
            }
            else
            {
                prg.Bind();
            }

            if (_fragmentOutputMap != (uint)prg.FragmentOutputMap)
            {
                _fragmentOutputMap = (uint)prg.FragmentOutputMap;

                for (int index = 0; index < Constants.MaxRenderTargets; index++)
                {
                    RestoreComponentMask(index, force: false);
                }
            }

            _program = prg;
        }

        public void SetRasterizerDiscard(bool discard)
        {
            if (discard)
            {
                GL.Enable(EnableCap.RasterizerDiscard);
            }
            else
            {
                GL.Disable(EnableCap.RasterizerDiscard);
            }

            _rasterizerDiscard = discard;
        }

        public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMasks)
        {
            _componentMasks = 0;

            for (int index = 0; index < componentMasks.Length; index++)
            {
                _componentMasks |= componentMasks[index] << (index * 4);

                RestoreComponentMask(index, force: false);
            }
        }

        private bool PrepareStereoDraw(TextureView explicitSource = null)
        {
            if (!NativeStereo.RenderSecondEye) return false;
            SameFrameStereo.TrackTexture(0, _unit0Texture);
            bool geometry = NativeStereo.GeometryPass && _program?.HasGeometryStereo == true &&
                ((_depthTestEnable && (!_nativeAlwaysDepth || _depthMask)) || _program.StereoProjectedDepth);
            bool stereo = geometry || _framebuffer?.HasStereoTargets == true ||
                explicitSource?.HasStereoStorage == true || SameFrameStereo.NeedsStereoCompute(_program);
            if (stereo) _framebuffer?.MakeStereoTargets();
            else if (NativeStereo.TraceFrame) SameFrameStereo.TraceUnpaired(_program);
            return stereo;
        }

        public void SetRenderTargets(ITexture[] colors, ITexture depthStencil)
        {
            // Shadow/depth-only passes use the shared light camera, not an eye camera.
            NativeStereo.GeometryPass = false;
            foreach (ITexture color in colors)
                if (color is TextureView view && !view.IsEnvironmentStorage) { NativeStereo.GeometryPass = true; break; }
            TextureView depthStencilView = (TextureView)depthStencil;
            if (!NativeStereo.GeometryPass && depthStencilView != null && !System.Array.Exists(colors, c => c is TextureView cv && cv.IsEnvironmentStorage))
                NativeStereo.GeometryPass = depthStencilView.Width > depthStencilView.Height * 1.5f && depthStencilView.Width < depthStencilView.Height * 2.1f;
            EnsureFramebuffer();

            // Identical guest targets must not bounce a valid layered stereo FBO
            // through the conventional left-eye FBO before every subsequent draw.
            if (NativeStereo.Enabled && NativeStereo.CacheRenderTargets && _framebuffer.MatchesTargets(colors, depthStencil)) return;
            if (NativeStereo.Enabled) _framebuffer.RebindStereoEye();

            for (int index = 0; index < colors.Length; index++)
            {
                TextureView color = (TextureView)colors[index];

                _framebuffer.AttachColor(index, color);

                if (color != null)
                {
                    int isBgra = color.Format.IsBgr() ? 1 : 0;

                    if (_fpIsBgra[index].X != isBgra)
                    {
                        _fpIsBgra[index].X = isBgra;

                        RestoreComponentMask(index);
                    }
                }
            }


            _framebuffer.AttachDepthStencil(depthStencilView);
            _framebuffer.SetDrawBuffers(colors.Length);
        }

        public void SetScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            int count = Math.Min(regions.Length, Constants.MaxViewports);

            Span<int> v = stackalloc int[count * 4];

            for (int index = 0; index < count; index++)
            {
                int vIndex = index * 4;

                Rectangle<int> region = regions[index];

                bool enabled = (region.X | region.Y) != 0 || region.Width != 0xffff || region.Height != 0xffff;
                uint mask = 1u << index;

                if (enabled)
                {
                    v[vIndex] = region.X;
                    v[vIndex + 1] = region.Y;
                    v[vIndex + 2] = region.Width;
                    v[vIndex + 3] = region.Height;

                    if ((_scissorEnables & mask) == 0)
                    {
                        _scissorEnables |= mask;
                        GL.Enable(IndexedEnableCap.ScissorTest, index);
                    }
                }
                else
                {
                    if ((_scissorEnables & mask) != 0)
                    {
                        _scissorEnables &= ~mask;
                        GL.Disable(IndexedEnableCap.ScissorTest, index);
                    }
                }
            }

            GL.ScissorArray(0, count, ref v[0]);
        }

        public void SetStencilTest(StencilTestDescriptor stencilTest)
        {
            _stencilTestEnable = stencilTest.TestEnable;

            if (!stencilTest.TestEnable)
            {
                GL.Disable(EnableCap.StencilTest);
                return;
            }

            GL.StencilOpSeparate(
                StencilFace.Front,
                stencilTest.FrontSFail.Convert(),
                stencilTest.FrontDpFail.Convert(),
                stencilTest.FrontDpPass.Convert());

            GL.StencilFuncSeparate(
                StencilFace.Front,
                (StencilFunction)stencilTest.FrontFunc.Convert(),
                stencilTest.FrontFuncRef,
                stencilTest.FrontFuncMask);

            GL.StencilMaskSeparate(StencilFace.Front, stencilTest.FrontMask);

            GL.StencilOpSeparate(
                StencilFace.Back,
                stencilTest.BackSFail.Convert(),
                stencilTest.BackDpFail.Convert(),
                stencilTest.BackDpPass.Convert());

            GL.StencilFuncSeparate(
                StencilFace.Back,
                (StencilFunction)stencilTest.BackFunc.Convert(),
                stencilTest.BackFuncRef,
                stencilTest.BackFuncMask);

            GL.StencilMaskSeparate(StencilFace.Back, stencilTest.BackMask);

            GL.Enable(EnableCap.StencilTest);

            _stencilFrontMask = stencilTest.FrontMask;
        }

        public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            SetBuffers(buffers, isStorage: true);
        }

        public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
        {
            if (texture != null)
            {
                if (binding == 0)
                {
                    _unit0Texture = (TextureBase)texture;
                }
                else
                {
                    ((TextureBase)texture).Bind(binding);
                }
            }
            else
            {
                TextureBase.ClearBinding(binding);
            }

            Sampler glSampler = (Sampler)sampler;

            glSampler?.Bind(binding);

            if (binding == 0)
            {
                _unit0Sampler = glSampler;
            }
        }

        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array)
        {
            (array as TextureArray).Bind(binding);
        }

        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array)
        {
            throw new NotSupportedException("OpenGL does not support descriptor sets.");
        }

        public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
        {
            if (_tfEnabled)
            {
                GL.EndTransformFeedback();
            }

            int count = Math.Min(buffers.Length, Constants.MaxTransformFeedbackBuffers);

            for (int i = 0; i < count; i++)
            {
                BufferRange buffer = buffers[i];
                _tfbTargets[i] = buffer;

                if (buffer.Handle == BufferHandle.Null)
                {
                    GL.BindBufferBase(BufferRangeTarget.TransformFeedbackBuffer, i, 0);
                    continue;
                }

                if (_tfbs[i] == BufferHandle.Null)
                {
                    _tfbs[i] = Buffer.Create();
                }

                Buffer.Resize(_tfbs[i], buffer.Size);
                Buffer.Copy(buffer.Handle, _tfbs[i], buffer.Offset, 0, buffer.Size);
                GL.BindBufferBase(BufferRangeTarget.TransformFeedbackBuffer, i, _tfbs[i].ToInt32());
            }

            if (_tfEnabled)
            {
                GL.BeginTransformFeedback(_tfTopology);
            }
        }

        public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            SetBuffers(buffers, isStorage: false);
        }

        public void SetUserClipDistance(int index, bool enableClip)
        {
            if (!enableClip)
            {
                GL.Disable(EnableCap.ClipDistance0 + index);
                return;
            }

            GL.Enable(EnableCap.ClipDistance0 + index);
        }

        public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            EnsureVertexArray();

            _vertexArray.SetVertexAttributes(vertexAttribs);
        }

        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            EnsureVertexArray();

            _vertexArray.SetVertexBuffers(vertexBuffers);
        }

        public void SetViewports(ReadOnlySpan<Viewport> viewports)
        {
            Array.Resize(ref _viewportArray, viewports.Length * 4);
            Array.Resize(ref _depthRangeArray, viewports.Length * 2);

            float[] viewportArray = _viewportArray;
            double[] depthRangeArray = _depthRangeArray;

            for (int index = 0; index < viewports.Length; index++)
            {
                int viewportElemIndex = index * 4;

                Viewport viewport = viewports[index];

                viewportArray[viewportElemIndex + 0] = viewport.Region.X;
                viewportArray[viewportElemIndex + 1] = viewport.Region.Y + (viewport.Region.Height < 0 ? viewport.Region.Height : 0);
                viewportArray[viewportElemIndex + 2] = viewport.Region.Width;
                viewportArray[viewportElemIndex + 3] = MathF.Abs(viewport.Region.Height);

                if (HwCapabilities.SupportsViewportSwizzle)
                {
                    GL.NV.ViewportSwizzle(
                        index,
                        viewport.SwizzleX.Convert(),
                        viewport.SwizzleY.Convert(),
                        viewport.SwizzleZ.Convert(),
                        viewport.SwizzleW.Convert());
                }

                depthRangeArray[index * 2 + 0] = viewport.DepthNear;
                depthRangeArray[index * 2 + 1] = viewport.DepthFar;
            }

            bool flipY = viewports.Length != 0 && viewports[0].Region.Height < 0;

            SetOrigin(flipY ? ClipOrigin.UpperLeft : ClipOrigin.LowerLeft);
            if (viewportArray.Length >= 4)
            {
                NativeStereo.ViewportX = viewportArray[0]; NativeStereo.ViewportY = viewportArray[1];
                NativeStereo.ViewportWidth = viewportArray[2]; NativeStereo.ViewportHeight = viewportArray[3];
            }

            GL.ViewportArray(0, viewports.Length, viewportArray);
            GL.DepthRangeArray(0, viewports.Length, depthRangeArray);
        }

        public void TextureBarrier()
        {
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
            // Diagnostic: guest render-to-texture ordering requires a framebuffer
            // texture barrier, distinct from shader-image memory visibility.
            if (GuestTextureBarrierTest && NativeStereo.ShadowProbe == 7)
            {
                GL.TextureBarrier();
                if (!_guestTextureBarrierLogged)
                {
                    _guestTextureBarrierLogged = true;
                    Logger.Notice.Print(LogClass.Gpu, "TOTK guest framebuffer texture barrier diagnostic active.");
                }
            }
        }

        public void TextureBarrierTiled()
        {
            GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
            // Diagnostic: guest render-to-texture ordering requires a framebuffer
            // texture barrier, distinct from shader-image memory visibility.
            if (GuestTextureBarrierTest && NativeStereo.ShadowProbe == 7)
            {
                GL.TextureBarrier();
                if (!_guestTextureBarrierLogged)
                {
                    _guestTextureBarrierLogged = true;
                    Logger.Notice.Print(LogClass.Gpu, "TOTK guest framebuffer texture barrier diagnostic active.");
                }
            }
        }

        private static unsafe void SetBuffers(ReadOnlySpan<BufferAssignment> buffers, bool isStorage)
        {
            if (NativeStereo.Enabled && NativeStereo.CacheBufferBindings && buffers.Length <= 64)
            {
                Span<BufferAssignment> changed = stackalloc BufferAssignment[64];
                int count = StereoBufferBinding.Filter(buffers, changed, isStorage);
                SetBuffersUncached(changed[..count], isStorage);
                // Preserve the generic binding implied by the original last assignment,
                // including when every indexed binding was already correct.
                if (buffers.Length != 0) StereoBufferBinding.BindGeneric(isStorage ? BufferTarget.ShaderStorageBuffer : BufferTarget.UniformBuffer, buffers[^1].Range.Handle.ToInt32());
                return;
            }
            StereoBufferBinding.Invalidate();
            SetBuffersUncached(buffers, isStorage);
        }

        private static unsafe void SetBuffersUncached(ReadOnlySpan<BufferAssignment> buffers, bool isStorage)
        {
            BufferRangeTarget target = isStorage ? BufferRangeTarget.ShaderStorageBuffer : BufferRangeTarget.UniformBuffer;
            if (!NativeStereo.Enabled || !NativeStereo.BufferMultiBind || buffers.Length > 64)
            {
                foreach (BufferAssignment assignment in buffers)
                {
                    BufferRange range = assignment.Range;
                    StereoBufferBinding.BindRange(target, assignment.Binding, range.Handle.ToInt32(),
                        range.Handle == BufferHandle.Null ? 0 : (nint)range.Offset,
                        range.Handle == BufferHandle.Null ? 0 : range.Size);
                }
                return;
            }
            int* handles = stackalloc int[64];
            nint* offsets = stackalloc nint[64];
            nint* sizes = stackalloc nint[64];
            int index = 0;
            while (index < buffers.Length)
            {
                int end = index + 1;
                while (end < buffers.Length && buffers[index].Range.Handle != BufferHandle.Null && buffers[end].Range.Handle != BufferHandle.Null && buffers[end].Binding == buffers[end - 1].Binding + 1) end++;
                int count = end - index;
                if (count >= 3)
                {
                    for (int i = 0; i < count; i++)
                    {
                        BufferRange range = buffers[index + i].Range;
                        handles[i] = range.Handle.ToInt32();
                        offsets[i] = handles[i] == 0 ? 0 : (nint)range.Offset;
                        sizes[i] = handles[i] == 0 ? 0 : range.Size;
                    }
                    GL.BindBuffersRange(target, buffers[index].Binding, count, handles, offsets, sizes);
                    // Multi-bind leaves the generic binding unchanged; preserve the
                    // previous single-bind behavior for later generic buffer operations.
                    StereoBufferBinding.BindGeneric((BufferTarget)target, handles[count - 1]);
                }
                else
                    for (int i = index; i < end; i++)
                    {
                        BufferRange range = buffers[i].Range;
                        StereoBufferBinding.BindRange(target, buffers[i].Binding, range.Handle.ToInt32(),
                            range.Handle == BufferHandle.Null ? 0 : (nint)range.Offset,
                            range.Handle == BufferHandle.Null ? 0 : range.Size);
                    }
                index = end;
            }
        }

        private void SetOrigin(ClipOrigin origin)
        {
            if (_clipOrigin != origin)
            {
                _clipOrigin = origin;

                GL.ClipControl(origin, _clipDepthMode);

                SetFrontFace(_frontFace);
            }
        }

        private void SetFrontFace(FrontFaceDirection frontFace)
        {
            // Changing clip origin will also change the front face to compensate
            // for the flipped viewport, we flip it again here to compensate as
            // this effect is undesirable for us.
            if (_clipOrigin == ClipOrigin.UpperLeft)
            {
                frontFace = frontFace == FrontFaceDirection.Ccw ? FrontFaceDirection.Cw : FrontFaceDirection.Ccw;
            }

            GL.FrontFace(frontFace);
        }

        private void EnsureVertexArray()
        {
            if (_vertexArray == null)
            {
                _vertexArray = new VertexArray();

                _vertexArray.Bind();
            }
        }

        private void EnsureFramebuffer()
        {
            if (_framebuffer == null)
            {
                _framebuffer = new Framebuffer();

                int boundHandle = _framebuffer.Bind();
                _boundDrawFramebuffer = _boundReadFramebuffer = boundHandle;

                GL.Enable(EnableCap.FramebufferSrgb);
            }
        }

        internal (int drawHandle, int readHandle) GetBoundFramebuffers()
        {
            if (BackgroundContextWorker.InBackground)
            {
                return (0, 0);
            }

            return (_boundDrawFramebuffer, _boundReadFramebuffer);
        }

        private void BindUnitZeroForSampling()
        {
            if (NativeStereo.Enabled && NativeStereo.CacheStereoResources && _unit0Texture is TextureView view &&
                view.Target == Target.Texture2D && _program?.SamplesEyeArray(0) == true)
            {
                SameFrameStereo.TrackTexture(0, view);
                // The rewritten shader only samples the array view. Rebinding its
                // unused conventional 2D target adds a driver call to every draw.
                StereoArrayBinding.Bind(0, view);
            }
            else _unit0Texture?.Bind(0);
        }

        private void PrepareForDispatch()
        {
            BindUnitZeroForSampling();
        }

        private void PreDraw(int vertexCount)
        {
            _vertexArray.PreDraw(vertexCount);
            PreDraw();
        }

        private void PreDrawVbUnbounded()
        {
            _vertexArray.PreDrawVbUnbounded();
            PreDraw();
        }

        private void PreDraw()
        {
            StereoGpuTiming.Begin(_program, "draw");
            DrawCount++;

            BindUnitZeroForSampling();
            SameFrameStereo.AuditShadowBindings(_program);
            SameFrameStereo.TraceDrawState(_program);
        }

        private void PostDraw()
        {
            StereoGpuTiming.End();
            StereoCommandFlush.Tick();
            if (_tfEnabled)
            {
                for (int i = 0; i < Constants.MaxTransformFeedbackBuffers; i++)
                {
                    if (_tfbTargets[i].Handle != BufferHandle.Null)
                    {
                        Buffer.Copy(_tfbs[i], _tfbTargets[i].Handle, 0, _tfbTargets[i].Offset, _tfbTargets[i].Size);
                    }
                }
            }
        }

        public void RestoreComponentMask(int index, bool force = true)
        {
            // If the bound render target is bgra, swap the red and blue masks.
            uint redMask = _fpIsBgra[index].X == 0 ? 1u : 4u;
            uint blueMask = _fpIsBgra[index].X == 0 ? 4u : 1u;

            int shift = index * 4;
            uint componentMask = _componentMasks & _fragmentOutputMap;
            uint checkMask = 0xfu << shift;
            uint componentMaskAtIndex = componentMask & checkMask;

            if (!force && componentMaskAtIndex == (_currentComponentMasks & checkMask))
            {
                return;
            }

            componentMask >>= shift;
            componentMask &= 0xfu;

            GL.ColorMask(
                index,
                (componentMask & redMask) != 0,
                (componentMask & 2u) != 0,
                (componentMask & blueMask) != 0,
                (componentMask & 8u) != 0);

            _currentComponentMasks &= ~checkMask;
            _currentComponentMasks |= componentMaskAtIndex;
        }

        public void RestoreClipControl()
        {
            GL.ClipControl(_clipOrigin, _clipDepthMode);
        }

        public void RestoreScissor0Enable()
        {
            if ((_scissorEnables & 1u) != 0)
            {
                GL.Enable(IndexedEnableCap.ScissorTest, 0);
            }
        }

        public void RestoreRasterizerDiscard()
        {
            if (_rasterizerDiscard)
            {
                GL.Enable(EnableCap.RasterizerDiscard);
            }
        }

        public void RestoreViewport0()
        {
            if (_viewportArray.Length > 0)
            {
                GL.ViewportArray(0, 1, _viewportArray);
            }
        }

        public void RestoreProgram()
        {
            _program?.Bind();
        }

        public void RestoreImages1And2()
        {
            for (int i = 0; i < SavedImages; i++)
            {
                TextureBase texBase = _images[i];

                if (texBase != null)
                {
                    SizedInternalFormat format = FormatTable.GetImageFormat(texBase.Format);

                    if (format != 0)
                    {
                        GL.BindImageTexture(i, texBase.Handle, 0, true, 0, TextureAccess.ReadWrite, format);
                        continue;
                    }
                }

                GL.BindImageTexture(i, 0, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba8);
            }
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            // Compare an event and a constant value.
            if (value is CounterQueueEvent evt)
            {
                // Easy host conditional rendering when the check matches what GL can do:
                //  - Event is of type samples passed.
                //  - Result is not a combination of multiple queries.
                //  - Comparing against 0.
                //  - Event has not already been flushed.

                if (compare == 0 && evt.Type == QueryTarget.SamplesPassed && evt.ClearCounter)
                {
                    if (!value.ReserveForHostAccess())
                    {
                        // If the event has been flushed, then just use the values on the CPU.
                        // The query object may already be repurposed for another draw (eg. begin + end).
                        return false;
                    }

                    GL.BeginConditionalRender(evt.Query, isEqual ? ConditionalRenderType.QueryNoWaitInverted : ConditionalRenderType.QueryNoWait);
                    _activeConditionalRender = evt;

                    return true;
                }
            }

            // The GPU will flush the queries to CPU and evaluate the condition there instead.

            GL.Flush(); // The thread will be stalled manually flushing the counter, so flush GL commands now.
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            GL.Flush(); // The GPU thread will be stalled manually flushing the counter, so flush GL commands now.
            return false; // We don't currently have a way to compare two counters for conditional rendering.
        }

        public void EndHostConditionalRendering()
        {
            GL.EndConditionalRender();

            _activeConditionalRender?.ReleaseHostAccess();
            _activeConditionalRender = null;
        }

        public void Dispose()
        {
            for (int i = 0; i < Constants.MaxTransformFeedbackBuffers; i++)
            {
                if (_tfbs[i] != BufferHandle.Null)
                {
                    Buffer.Delete(_tfbs[i]);
                    _tfbs[i] = BufferHandle.Null;
                }
            }

            _activeConditionalRender?.ReleaseHostAccess();
            _framebuffer?.Dispose();
            _vertexArray?.Dispose();
            _drawTexture.Dispose();
        }
    }
}
