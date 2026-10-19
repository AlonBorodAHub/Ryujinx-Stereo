using Ryujinx.Common.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.OpenGL;

public static class SbsRecording
{
    public const int Width = 3840, Height = 2160, FramesPerSecond = 60;
    internal const int FrameBytes = Width * Height * 4;
    private static readonly object Gate = new();
    internal static RecordingSession Current;
    public static event Action<string, bool> StatusChanged;
    public static bool IsRecording => Current != null && !Current.Completed;
    public static Task Completion => Current?.Completion.Task ?? Task.CompletedTask;
    public static void Start(string outputPath = null)
    {
        lock (Gate)
        {
            if (Current != null && !Current.Completed) return;
            if (Environment.GetEnvironmentVariable("RYU_GEOMETRY_STEREO") != "1")
            {
                Notify("Recording requires an active stereo game.", false); return;
            }
            string game = Environment.GetEnvironmentVariable("RJ_STEREO_GAME") switch
            {
                "01007ef00011e000" => "Zelda Breath of the Wild", "0100000000010000" => "Super Mario Odyssey", _ => "Ryujinx Stereo"
            };
            outputPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                game + " SBS 4K60 " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".mp4");
            Current = new RecordingSession(outputPath);
            Current.Start();
        }
    }
    public static void Stop() { Current?.Stop(); }
    public static async Task<string> StopAndSaveAsync()
    {
        var session = Current;
        if (session == null) return null;
        session.Stop();
        await session.Completion.Task.ConfigureAwait(false);
        return session.SavedOutput;
    }
    internal static void Notify(string message, bool active)
    {
        Logger.Notice.Print(LogClass.Gpu, "Stereo recording: " + message);
        try { StatusChanged?.Invoke(message, active); } catch { }
    }
}

internal sealed record RecordingFrame(byte[] Pixels, long Timestamp);

internal sealed class RecordingSession
{
    private readonly ConcurrentQueue<RecordingFrame> _frames = new();
    private readonly string _requestedOutput;
    private int _queued, _stop;
    private long _stopAt;
    internal volatile bool Prepared, Completed;
    internal string SavedOutput;
    internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Stopping => Volatile.Read(ref _stop) != 0;
    internal long Dropped;
    internal RecordingSession(string output) { _requestedOutput = Path.GetFullPath(output); }
    internal void Start() => new Thread(Run) { Name = "Stereo MP4 encoder", IsBackground = false }.Start();
    internal void Stop()
    {
        if (Interlocked.CompareExchange(ref _stop, 1, 0) == 0)
        {
            Volatile.Write(ref _stopAt, Stopwatch.GetTimestamp());
            SbsRecording.Notify("Finishing MP4…", false);
        }
    }
    internal void Offer(byte[] pixels, long timestamp)
    {
        if (Completed || Stopping) { ArrayPool<byte>.Shared.Return(pixels); return; }
        _frames.Enqueue(new RecordingFrame(pixels, timestamp)); Interlocked.Increment(ref _queued);
        while (Volatile.Read(ref _queued) > 10 && Take(out var discarded))
        {
            ArrayPool<byte>.Shared.Return(discarded.Pixels); Interlocked.Increment(ref Dropped);
        }
    }
    private bool Take(out RecordingFrame frame)
    {
        if (_frames.TryDequeue(out frame)) { Interlocked.Decrement(ref _queued); return true; }
        return false;
    }
    private static void Release(RecordingFrame frame) { if (frame != null) ArrayPool<byte>.Shared.Return(frame.Pixels); }
    private void Run()
    {
        string temp = Path.Combine(Path.GetTempPath(), "ryujinx-sbs-record-" + Guid.NewGuid().ToString("N"));
        string video = Path.Combine(temp, "video.mp4"), audio = Path.Combine(temp, "audio.wav"), muxed = Path.Combine(temp, "finished.mp4");
        Process encoder = null; LoopbackCapture loopback = null;
        RecordingFrame current = null, pending = null;
        bool success = false;
        long written = 0, repeated = 0;
        try
        {
            Directory.CreateDirectory(temp);
            if (Stopping) return;
            string ffmpeg = FindFfmpeg();
            using (var probe = Spawn(ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=256x256:rate=1", "-frames:v", "1", "-c:v", "h264_nvenc", "-f", "null", "NUL" }, Path.Combine(temp, "probe.log"), false))
            {
                if (!probe.WaitForExit(10000)) { probe.Kill(); throw new IOException("NVIDIA encoder probe timed out."); }
                if (probe.ExitCode != 0) throw new IOException("NVIDIA H.264 recording is unavailable. See " + temp);
            }
            if (Stopping) return;
            Prepared = true;
            var wait = Stopwatch.StartNew();
            while (!Take(out current))
            {
                if (Stopping) return;
                if (wait.Elapsed.TotalSeconds > 20) throw new IOException("No stereo frames arrived. Keep the game running while starting recording.");
                Thread.Sleep(2);
            }
            if (Stopping) return;
            long origin = current.Timestamp;
            loopback = new LoopbackCapture(audio); loopback.Start();
            double audioOffset = Math.Max(0, (loopback.StartTimestamp - origin) / (double)Stopwatch.Frequency);
            encoder = Spawn(ffmpeg, new[] { "-hide_banner", "-loglevel", "warning", "-y", "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "3840x2160", "-framerate", "60", "-i", "pipe:0", "-an", "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull", "-rc", "constqp", "-qp", "21", "-bf", "0", "-zerolatency", "1", "-r", "60", "-fps_mode", "cfr", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-metadata:s:v:0", "stereo_mode=left_right", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-video_track_timescale", "60000", "-f", "mp4", video }, Path.Combine(temp, "encoder.log"), true);
            SbsRecording.Notify("Recording SBS 3840×2160 at 60 fps with system audio (F10 to save).", true);
            var pipe = encoder.StandardInput.BaseStream;
            while (true)
            {
                long end = Volatile.Read(ref _stopAt);
                if (Stopping && end != 0 && written >= Math.Max(1, (long)Math.Ceiling((end - origin) * 60.0 / Stopwatch.Frequency))) break;
                long target = origin + (long)(written * (double)Stopwatch.Frequency / 60);
                // Delay the encoder's wall clock, not gameplay, to let asynchronous
                // GPU readbacks for this presentation time reach the queue.
                long due = target + Stopwatch.Frequency / 10;
                while (Stopwatch.GetTimestamp() < due)
                {
                    if (encoder.HasExited) throw new IOException("The video encoder stopped unexpectedly. See " + temp);
                    Thread.Sleep(1);
                }
                bool changed = false;
                while (true)
                {
                    if (pending == null && !Take(out pending)) break;
                    if (pending.Timestamp > target) break;
                    Release(current); current = pending; pending = null; changed = true;
                }
                if (!changed && written > 0) repeated++;
                pipe.Write(current.Pixels, 0, SbsRecording.FrameBytes); written++;
            }
            encoder.StandardInput.Close();
            if (!encoder.WaitForExit(15000)) { encoder.Kill(); throw new IOException("The video encoder did not finish. See " + temp); }
            if (encoder.ExitCode != 0) throw new IOException("Video encoding failed. See " + temp);
            encoder.Dispose(); encoder = null;
            loopback.Stop(); loopback.Dispose(); loopback = null;
            double duration = written / 60.0;
            using (var mux = Spawn(ffmpeg, new[] { "-hide_banner", "-loglevel", "warning", "-y", "-i", video, "-itsoffset", audioOffset.ToString("F6", CultureInfo.InvariantCulture), "-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-t", duration.ToString("F6", CultureInfo.InvariantCulture), "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-af", "aresample=async=1:first_pts=0", "-video_track_timescale", "60000", "-movflags", "+faststart", muxed }, Path.Combine(temp, "mux.log"), false))
            {
                if (!mux.WaitForExit(120000)) { mux.Kill(); throw new IOException("MP4 finalization timed out. Recording parts remain in " + temp); }
                if (mux.ExitCode != 0) throw new IOException("MP4 finalization failed. Recording parts remain in " + temp);
            }
            string output = Unique(_requestedOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.Move(muxed, output);
            File.WriteAllText(Path.Combine(temp, "result.json"), JsonSerializer.Serialize(new { output, width = 3840, height = 2160, fps = 60, frames = written, repeated, dropped = Dropped, audioOffset, duration }));
            SavedOutput = output;
            success = true;
            SbsRecording.Notify("Saved " + output, false);
        }
        catch (Exception error)
        {
            try { File.WriteAllText(Path.Combine(temp, "error.txt"), error.ToString()); } catch { }
            SbsRecording.Notify("Recording failed: " + error.Message + " Diagnostic folder: " + temp, false);
        }
        finally
        {
            Prepared = false;
            if (encoder != null)
            {
                try { encoder.StandardInput.Close(); if (!encoder.WaitForExit(5000)) encoder.Kill(); } catch { }
                encoder.Dispose();
            }
            if (loopback != null) { try { loopback.Stop(); } catch { } loopback.Dispose(); }
            Release(current); Release(pending);
            while (Take(out var frame)) Release(frame);
            Completed = true; Completion.TrySetResult();
            if (success)
            {
                foreach (string part in new[] { video, audio }) try { File.Delete(part); } catch { }
            }
            if (!success) SbsRecording.Notify("Recorder stopped; gameplay remains open.", false);
        }
    }
    private static string FindFfmpeg()
    {
        string[] candidates = { Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"), Path.Combine(Environment.GetEnvironmentVariable("RJ_STEREO_HOME") ?? AppContext.BaseDirectory, "tools", "ffmpeg.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Jellyfin", "Server", "ffmpeg.exe") };
        foreach (string path in candidates) if (File.Exists(path)) return path;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            try { string path = Path.Combine(folder.Trim(), "ffmpeg.exe"); if (File.Exists(path)) return path; } catch { }
        throw new FileNotFoundException("FFmpeg is not installed. The existing Jellyfin FFmpeg or tools/ffmpeg.exe is required.");
    }
    private static Process Spawn(string exe, string[] arguments, string log, bool input)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = input, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) try { lock (log) File.AppendAllText(log, e.Data + Environment.NewLine); } catch { } };
        if (!process.Start()) throw new IOException("Cannot start the video encoder.");
        process.BeginErrorReadLine(); return process;
    }
    private static string Unique(string requested)
    {
        if (!File.Exists(requested)) return requested;
        for (int n = 2; ; n++)
        {
            string path = Path.Combine(Path.GetDirectoryName(requested), Path.GetFileNameWithoutExtension(requested) + " (" + n + ")" + Path.GetExtension(requested));
            if (!File.Exists(path)) return path;
        }
    }
}
