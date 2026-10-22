using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Graphics.OpenGL;

namespace Ryujinx.Ava.Stereo;

internal static class StereoRecordingUi
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    internal static void Attach(MainWindow owner)
    {
        StereoAdjustmentOverlay.Attach(owner);
        // Native OpenGL fullscreen focus can bypass Avalonia's routed key events.
        // Recording keys require foreground focus; the controller exit chord does not.
        if (OperatingSystem.IsWindows())
        {
            bool previousStart = false, previousStop = false;
            var keys = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            keys.Tick += (_, _) =>
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
                bool focused = foreground == Environment.ProcessId && owner.ViewModel.AppHost != null;
                if (Ryujinx.Input.HLE.StereoControllerExit.Consume() && owner.ViewModel.AppHost != null)
                {
                    // dEPTH owns its cover-before-exit sequence. Never race it.
                    var frontends = Process.GetProcessesByName("Depth");
                    bool frontendOwnsExit = frontends.Length != 0;
                    foreach (var frontend in frontends) frontend.Dispose();
                    if (!frontendOwnsExit)
                    {
                        Ryujinx.Input.HLE.StereoControllerExit.CloseWindowRequested = true;
                        if (SbsRecording.IsRecording) Stop(owner);
                        else owner.ViewModel.AppHost?.Stop();
                    }
                }
                bool start = focused && (GetAsyncKeyState(0x78) & 0x8000) != 0;
                bool stop = focused && (GetAsyncKeyState(0x79) & 0x8000) != 0;
                if (stop && !previousStop) Stop(owner);
                else if (start && !previousStart) Start(owner);
                previousStart = start; previousStop = stop;
            };
            owner.Closed += (_, _) => keys.Stop();
            keys.Start();
        }
        SbsRecording.StatusChanged += (message, active) => Dispatcher.UIThread.Post(() =>
        {
            string title = owner.ViewModel.Title?.Replace(" [REC]", "") ?? "Ryujinx Stereo";
            owner.ViewModel.Title = active ? title + " [REC]" : title;
        });
    }
    internal static void Start(MainWindow owner)
    {
        if (owner?.ViewModel.AppHost != null && Volatile.Read(ref _finishing) == 0) SbsRecording.Start();
    }
    private static int _finishing;
    internal static async void Stop(MainWindow owner)
    {
        var host = owner?.ViewModel.AppHost;
        if (host == null || !SbsRecording.IsRecording || Interlocked.CompareExchange(ref _finishing, 1, 0) != 0) return;
        try
        {
            string output = await SbsRecording.StopAndSaveAsync();
            if (output == null) return;
            // Do not close gameplay until the finished MP4 is safely on the desktop.
            if (owner.ViewModel.AppHost == host) host.Stop();
            var showFile = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            showFile.Arguments = "/select,\"" + output + "\"";
            try { Process.Start(showFile); } catch { }
        }
        finally { Volatile.Write(ref _finishing, 0); }
    }
}
