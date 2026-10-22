using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL.Multithreading.Commands;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Buffer;
using Ryujinx.Graphics.GAL.Multithreading.Commands.Renderer;
using Ryujinx.Graphics.GAL.Multithreading.Model;
using Ryujinx.Graphics.GAL.Multithreading.Resources;
using Ryujinx.Graphics.GAL.Multithreading.Resources.Programs;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// The ThreadedRenderer is a layer that can be put in front of any Renderer backend to make
    /// its processing happen on a separate thread, rather than intertwined with the GPU emulation.
    /// A new thread is created to handle the GPU command processing, separate from the renderer thread.
    /// Calls to the renderer, pipeline and resources are queued to happen on the renderer thread.
    /// </summary>
    public class ThreadedRenderer : IRenderer
    {
        private const int SpanPoolBytes = 4 * 1024 * 1024;
        private const int MaxRefsPerCommand = 2;
        private const int QueueCount = 10000;

        private readonly int _elementSize;
        private readonly IRenderer _baseRenderer;
        private Thread _gpuThread;
        private Thread _backendThread;
        private bool _running;

        private readonly AutoResetEvent _frameComplete = new(true);

        private readonly ManualResetEventSlim _galWorkAvailable;
        private readonly CircularSpanPool _spanPool;

        private readonly ManualResetEventSlim _invokeRun;
        private readonly AutoResetEvent _interruptRun;

        private bool _lastSampleCounterClear = true;

        private readonly byte[] _commandQueue;
        private readonly object[] _refQueue;

        private int _consumerPtr;
        private int _commandCount;
        public static volatile int IdleSpinMicroseconds;
        private int _publishedPtr;
        private int _waitingForWork;
        private readonly bool _usePublishedQueue = Environment.GetEnvironmentVariable("RYU_PUBLISHED_QUEUE") == "1";
        private bool HasCommands => _usePublishedQueue
            ? Volatile.Read(ref _publishedPtr) != Volatile.Read(ref _consumerPtr)
            : Volatile.Read(ref _commandCount) > 0;

        private int _producerPtr;
        private int _lastProducedPtr;
        private int _invokePtr = -1;

        private int _refProducerPtr;
        private int _refConsumerPtr;

        public uint ProgramCount { get; set; } = 0;

        private Action _interruptAction;
        private readonly Lock _interruptLock = new();

        public event EventHandler<ScreenCaptureImageInfo> ScreenCaptured;

        internal BufferMap Buffers { get; }
        internal SyncMap Sync { get; }
        internal CircularSpanPool SpanPool { get; }
        internal ProgramQueue Programs { get; }

        public IPipeline Pipeline { get; }
        public IWindow Window { get; }

        public IRenderer BaseRenderer => _baseRenderer;

        public bool PreferThreading => _baseRenderer.PreferThreading;

        public ThreadedRenderer(IRenderer renderer)
        {
            _baseRenderer = renderer;

            renderer.ScreenCaptured += (sender, info) => ScreenCaptured?.Invoke(this, info);
            renderer.SetInterruptAction(Interrupt);

            Pipeline = new ThreadedPipeline(this);
            Window = new ThreadedWindow(this, renderer);
            Buffers = new BufferMap();
            Sync = new SyncMap();
            Programs = new ProgramQueue(renderer);

            _galWorkAvailable = new ManualResetEventSlim(false);
            _invokeRun = new ManualResetEventSlim();
            _interruptRun = new AutoResetEvent(false);
            _spanPool = new CircularSpanPool(this, SpanPoolBytes);
            SpanPool = _spanPool;

            _elementSize = BitUtils.AlignUp(CommandHelper.GetMaxCommandSize(), 4);

            _commandQueue = new byte[_elementSize * QueueCount];
            _refQueue = new object[MaxRefsPerCommand * QueueCount];
        }

        public void RunLoop(ThreadStart gpuLoop)
        {
            _running = true;

            _backendThread = Thread.CurrentThread;

            _gpuThread = new Thread(gpuLoop)
            {
                Name = "GPU.MainThread",
            };

            _gpuThread.Start();

            RenderLoop();
        }

        public void RenderLoop()
        {
            // Power through the render queue until the Gpu thread work is done.

            while (_running)
            {
                long idleStart = HandoffTiming.Start();
                if (_usePublishedQueue)
                {
                    int spinUs = IdleSpinMicroseconds;
                    if (spinUs > 0 && !HasCommands)
                    {
                        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * spinUs / 1000000;
                        while (!HasCommands && Volatile.Read(ref _interruptAction) == null && Stopwatch.GetTimestamp() < deadline)
                        {
                            Thread.SpinWait(8);
                        }
                    }
                    // Arm before rechecking publication. The exchange provides the
                    // store/load barrier needed to avoid a lost producer wakeup.
                    Interlocked.Exchange(ref _waitingForWork, 1);
                    if (!HasCommands && Volatile.Read(ref _interruptAction) == null)
                    {
                        _galWorkAvailable.Wait();
                    }
                    Interlocked.Exchange(ref _waitingForWork, 0);
                }
                else
                {
                    _galWorkAvailable.Wait();
                }
                HandoffTiming.Idle(idleStart);
                _galWorkAvailable.Reset();

                if (Volatile.Read(ref _interruptAction) != null)
                {
                    _interruptAction();
                    _interruptRun.Set();

                    Interlocked.Exchange(ref _interruptAction, null);
                }

                // The other thread can only increase the command count.
                // We can assume that if it is above 0, it will stay there or get higher.

                while (HasCommands && Volatile.Read(ref _interruptAction) == null)
                {
                    int commandPtr = _consumerPtr;

                    Span<byte> command = new(_commandQueue, commandPtr * _elementSize, _elementSize);

                    // Run the command.

                    long commandStart = HandoffTiming.Start();
                    CommandHelper.RunCommand(command, this, _baseRenderer);
                    HandoffTiming.Command(command[^1], commandStart);

                    // Most commands are asynchronous. Avoid an exclusive cache-line
                    // operation unless a producer is actually waiting for this slot.
                    if (Volatile.Read(ref _invokePtr) == commandPtr &&
                        Interlocked.CompareExchange(ref _invokePtr, -1, commandPtr) == commandPtr)
                    {
                        _invokeRun.Set();
                    }

                    Volatile.Write(ref _consumerPtr, (_consumerPtr + 1) % QueueCount);
                    if (!_usePublishedQueue) Interlocked.Decrement(ref _commandCount);
                }
            }
        }

        internal SpanRef<T> CopySpan<T>(ReadOnlySpan<T> data) where T : unmanaged
        {
            long copyStart = HandoffTiming.Start();
            SpanRef<T> result = _spanPool.Insert(data);
            HandoffTiming.Copy(copyStart, (long)data.Length * Unsafe.SizeOf<T>());
            return result;
        }

        private TableRef<T> Ref<T>(T reference)
        {
            return new TableRef<T>(this, reference);
        }

        internal ref T New<T>() where T : struct, IGALCommand
        {
            while (_producerPtr == (Volatile.Read(ref _consumerPtr) + QueueCount - 1) % QueueCount)
            {
                // If incrementing the producer pointer would overflow, we need to wait.
                // _consumerPtr can only move forward, so there's no race to worry about here.

                Thread.Sleep(1);
            }

            int taken = _producerPtr;
            _lastProducedPtr = taken;

            _producerPtr = (_producerPtr + 1) % QueueCount;

            Span<byte> memory = new(_commandQueue, taken * _elementSize, _elementSize);
            ref T result = ref Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(memory));

            memory[^1] = (byte)result.CommandType;

            return ref result;
        }

        internal int AddTableRef(object obj)
        {
            // The reference table is sized so that it will never overflow, so long as the references are taken after the command is allocated.

            int index = _refProducerPtr;

            _refQueue[index] = obj;

            _refProducerPtr = (_refProducerPtr + 1) % _refQueue.Length;

            return index;
        }

        internal object RemoveTableRef(int index)
        {
            Debug.Assert(index == _refConsumerPtr);

            object result = _refQueue[_refConsumerPtr];
            _refQueue[_refConsumerPtr] = null;

            _refConsumerPtr = (_refConsumerPtr + 1) % _refQueue.Length;

            return result;
        }

        internal void QueueCommand()
        {
            if (_usePublishedQueue)
            {
                // Publish only after the complete command and its references have
                // been written. A full fence is required before checking the waiter;
                // a release store alone can lose a wakeup on the store/load race.
                Interlocked.Exchange(ref _publishedPtr, _producerPtr);
                if (Volatile.Read(ref _waitingForWork) != 0 &&
                    Interlocked.Exchange(ref _waitingForWork, 0) != 0)
                {
                    _galWorkAvailable.Set();
                }
            }
            else if (Interlocked.Increment(ref _commandCount) == 1)
            {
                _galWorkAvailable.Set();
            }
        }

        internal void InvokeCommand()
        {
            _invokeRun.Reset();
            Volatile.Write(ref _invokePtr, _lastProducedPtr);

            QueueCommand();

            // Wait for the command to complete.
            long invokeStart = HandoffTiming.Start();
            _invokeRun.Wait();
            HandoffTiming.Invoke(invokeStart);
        }

        internal void WaitForFrame()
        {
            _frameComplete.WaitOne();
        }

        internal void SignalFrame()
        {
            _frameComplete.Set();
        }

        internal bool IsGpuThread()
        {
            return Thread.CurrentThread == _gpuThread;
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            if (IsGpuThread() && !alwaysBackground)
            {
                // The action must be performed on the render thread.
                New<ActionCommand>().Set(Ref(action));
                InvokeCommand();
            }
            else
            {
                _baseRenderer.BackgroundContextAction(action, true);
            }
        }

        public BufferHandle CreateBuffer(int size, BufferAccess access)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferAccessCommand>().Set(handle, size, access);
            QueueCommand();

            return handle;
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateHostBufferCommand>().Set(handle, pointer, size);
            QueueCommand();

            return handle;
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            BufferHandle handle = Buffers.CreateBufferHandle();
            New<CreateBufferSparseCommand>().Set(handle, CopySpan(storageBuffers));
            QueueCommand();

            return handle;
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            ThreadedImageArray imageArray = new(this);
            New<CreateImageArrayCommand>().Set(Ref(imageArray), size, isBuffer);
            QueueCommand();

            return imageArray;
        }

        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            ThreadedProgram program = new(this);

            SourceProgramRequest request = new(program, shaders, info);

            Programs.Add(request);

            ProgramCount++;

            New<CreateProgramCommand>().Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            ThreadedSampler sampler = new(this);
            New<CreateSamplerCommand>().Set(Ref(sampler), info);
            QueueCommand();

            return sampler;
        }

        public void CreateSync(ulong id, bool strict)
        {
            Sync.CreateSyncHandle(id);
            New<CreateSyncCommand>().Set(id, strict);
            QueueCommand();
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            if (IsGpuThread())
            {
                ThreadedTexture texture = new(this, info);
                New<CreateTextureCommand>().Set(Ref(texture), info);
                QueueCommand();

                return texture;
            }
            else
            {
                ThreadedTexture texture = new(this, info)
                {
                    Base = _baseRenderer.CreateTexture(info),
                };

                return texture;
            }
        }
        public ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            ThreadedTextureArray textureArray = new(this);
            New<CreateTextureArrayCommand>().Set(Ref(textureArray), size, isBuffer);
            QueueCommand();

            return textureArray;
        }

        public void DeleteBuffer(BufferHandle buffer)
        {
            New<BufferDisposeCommand>().Set(buffer);
            QueueCommand();
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            if (IsGpuThread())
            {
                ResultBox<PinnedSpan<byte>> box = new();
                New<BufferGetDataCommand>().Set(buffer, offset, size, Ref(box));
                InvokeCommand();

                return box.Result;
            }
            else
            {
                return _baseRenderer.GetBufferData(Buffers.MapBufferBlocking(buffer), offset, size);
            }
        }

        public Capabilities GetCapabilities()
        {
            ResultBox<Capabilities> box = new();
            New<GetCapabilitiesCommand>().Set(Ref(box));
            InvokeCommand();

            return box.Result;
        }

        public ulong GetCurrentSync()
        {
            return _baseRenderer.GetCurrentSync();
        }

        public HardwareInfo GetHardwareInfo()
        {
            return _baseRenderer.GetHardwareInfo();
        }

        /// <summary>
        /// Initialize the base renderer. Must be called on the render thread.
        /// </summary>
        /// <param name="logLevel">Log level to use</param>
        public void Initialize(GraphicsDebugLevel logLevel)
        {
            _baseRenderer.Initialize(logLevel);
        }

        public IProgram LoadProgramBinary(byte[] programBinary, bool hasFragmentShader, ShaderInfo info)
        {
            ThreadedProgram program = new(this);

            BinaryProgramRequest request = new(program, programBinary, hasFragmentShader, info);
            Programs.Add(request);

            New<CreateProgramCommand>().Set(Ref((IProgramRequest)request));
            QueueCommand();

            return program;
        }

        public void PreFrame()
        {
            New<PreFrameCommand>();
            QueueCommand();
        }

        public ICounterEvent ReportCounter(CounterType type, EventHandler<ulong> resultHandler, float divisor, bool hostReserved)
        {
            ThreadedCounterEvent evt = new(this, type, _lastSampleCounterClear);
            New<ReportCounterCommand>().Set(Ref(evt), type, Ref(resultHandler), divisor, hostReserved);
            QueueCommand();

            if (type == CounterType.SamplesPassed)
            {
                _lastSampleCounterClear = false;
            }

            return evt;
        }

        public void ResetCounter(CounterType type)
        {
            New<ResetCounterCommand>().Set(type);
            QueueCommand();
            _lastSampleCounterClear = true;
        }

        public void Screenshot()
        {
            _baseRenderer.Screenshot();
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            New<BufferSetDataCommand>().Set(buffer, offset, CopySpan(data));
            QueueCommand();
        }

        public void UpdateCounters()
        {
            New<UpdateCountersCommand>();
            QueueCommand();
        }

        public void WaitSync(ulong id)
        {
            Sync.WaitSyncAvailability(id);

            _baseRenderer.WaitSync(id);
        }

        private void Interrupt(Action action)
        {
            // Interrupt the backend thread from any external thread and invoke the given action.

            if (Thread.CurrentThread == _backendThread)
            {
                // If this is called from the backend thread, the action can run immediately.
                action();
            }
            else
            {
                lock (_interruptLock)
                {
                    while (Interlocked.CompareExchange(ref _interruptAction, action, null) != null)
                    {
                    }

                    _galWorkAvailable.Set();

                    _interruptRun.WaitOne();
                }
            }
        }

        public void SetInterruptAction(Action<Action> interruptAction)
        {
            // Threaded renderer ignores given interrupt action, as it provides its own to the child renderer.
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return _baseRenderer.PrepareHostMapping(address, size);
        }

        public void FlushThreadedCommands()
        {
            SpinWait wait = new();

            while (HasCommands)
            {
                wait.SpinOnce();
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            // Dispose must happen from the render thread, after all commands have completed.

            // The guest GPU loop has stopped, but the backend thread must remain
            // alive to drain resource deletions and own its OpenGL context.
            if (_gpuThread != null && _gpuThread.IsAlive)
            {
                _gpuThread.Join();
            }

            Programs.DrainBackgroundCompilations();
            if (_running && _backendThread != null && Thread.CurrentThread != _backendThread)
            {
                FlushThreadedCommands();
                Interrupt(() =>
                {
                    _baseRenderer.Dispose();
                    _running = false;
                });
            }
            else
            {
                _baseRenderer.Dispose();
                _running = false;
            }

            // Dispose events.
            _frameComplete.Dispose();
            _galWorkAvailable.Dispose();
            _invokeRun.Dispose();
            _interruptRun.Dispose();

            Sync.Dispose();
        }
    }
}
