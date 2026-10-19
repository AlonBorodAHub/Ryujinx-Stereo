using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.GAL.Multithreading.Resources.Programs
{
    class SourceProgramRequest : IProgramRequest
    {
        public ThreadedProgram Threaded { get; set; }

        private readonly ShaderSource[] _shaders;
        private ShaderInfo _info;
        private static readonly SemaphoreSlim CompilationSlots = new(8);
        private Task<IProgram> _backgroundCompilation;
        internal bool CompilationPending => _backgroundCompilation != null && !_backgroundCompilation.IsCompleted;
        internal Task StartBackgroundCompilation(IRenderer renderer)
        {
            _backgroundCompilation = Task.Run(async () =>
            {
                await CompilationSlots.WaitAsync().ConfigureAwait(false);
                long started = Stopwatch.GetTimestamp();
                try
                {
                    void OnContext(Action action)
                    {
                        Exception failure = null;
                        renderer.BackgroundContextAction(() =>
                        {
                            try { action(); }
                            catch (Exception ex) { failure = ex; }
                        }, alwaysBackground: true);
                        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    }
                    IProgram program = null;
                    OnContext(() => program = renderer.CreateProgram(_shaders, _info));
                    while (true)
                    {
                        ProgramLinkStatus status = ProgramLinkStatus.Incomplete;
                        OnContext(() => status = program.CheckProgramLink(false));
                        if (status != ProgramLinkStatus.Incomplete) break;
                        await Task.Delay(1).ConfigureAwait(false);
                    }
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (elapsed > 8) Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo background shader ready: {elapsed:F1} ms");
                    return program;
                }
                finally { CompilationSlots.Release(); }
            });
            return _backgroundCompilation;
        }

        public SourceProgramRequest(ThreadedProgram program, ShaderSource[] shaders, ShaderInfo info)
        {
            Threaded = program;

            _shaders = shaders;
            _info = info;
        }

        public IProgram Create(IRenderer renderer)
        {
            if (_backgroundCompilation != null)
            {
                long started = Stopwatch.GetTimestamp();
                IProgram result = _backgroundCompilation.GetAwaiter().GetResult();
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (elapsed > 8) Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo first-use shader wait: {elapsed:F1} ms");
                return result;
            }
            return renderer.CreateProgram(_shaders, _info);
        }
    }
}
