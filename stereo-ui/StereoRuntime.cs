using Avalonia.Controls;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Stereo;

internal static class StereoRuntime
{
    public static bool IsChild => Environment.GetEnvironmentVariable("RJ_STEREO_CHILD") == "1";
    public static string ActiveId => Environment.GetEnvironmentVariable("RJ_STEREO_GAME");
    private static int _launching;
    public static void Initialize()
    {
        // Run before any renderer or driver static initialization.
        if (IsChild)
        {
            string id = StereoProfiles.Normalize(ActiveId);
            var profile = StereoProfiles.Load(id);
            Environment.SetEnvironmentVariable("RYU_GEOMETRY_STEREO", StereoProfiles.Enabled(profile) ? "1" : "0");
            Environment.SetEnvironmentVariable("RYU_STEREO_SETTINGS", StereoProfiles.PathFor(id));
            Environment.SetEnvironmentVariable("RYU_LEIA_DIRECT", "1");
        }
        else
        {
            Environment.SetEnvironmentVariable("RJ_STEREO_HOME", AppContext.BaseDirectory);
            Environment.SetEnvironmentVariable("RYU_GEOMETRY_STEREO", "0");
        }
    }
    public static ProcessStartInfo CreateStartInfo(string id, string path, string rootData, bool fullscreen)
    {
        id = StereoProfiles.Normalize(id);
        // Keep validated game renderers isolated behind one library and shared settings UI.
        // Zelda's shader fixes must not be applied to Mario's shaders.
        string executable = id == StereoProfiles.Zelda
            ? Path.Combine(StereoProfiles.Home, "engines", "zelda", "Ryujinx.exe")
            : Path.Combine(StereoProfiles.Home, "Ryujinx.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable), CreateNoWindow = true };
        foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("RYU_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
        start.Environment["RJ_STEREO_HOME"] = StereoProfiles.Home;
        start.Environment["RJ_STEREO_CHILD"] = "1";
        start.Environment["RJ_STEREO_GAME"] = id;
        start.Environment["RYU_STEREO_BACKGROUND_COMPILATION"] = "0";
        start.Environment["RYU_STEREO_DRIVER_THREADING"] = id == StereoProfiles.Zelda ? "1" : "0";
        start.Environment["RYU_OPENGL_BUFFER_PREFLUSH"] = id == StereoProfiles.Zelda ? "1" : "0";
        string pairedEyes = id == "010015100b514000" ? "0" : "1";
        start.Environment["RYU_STEREO_LAYERED"] = pairedEyes;
        start.Environment["RYU_STEREO_INSTANCED"] = pairedEyes;
        start.Environment["RYU_STEREO_ARRAY_SAMPLING"] = pairedEyes;
        start.ArgumentList.Add("--root-data-dir"); start.ArgumentList.Add(rootData);
        if (StereoProfiles.Enabled(StereoProfiles.Load(id)))
        {
            start.ArgumentList.Add("--graphics-backend"); start.ArgumentList.Add("OpenGl");
        }
        if (fullscreen) start.ArgumentList.Add("--fullscreen");
        start.ArgumentList.Add(path);
        return start;
    }
    public static async Task LaunchAsync(ApplicationData application, MainWindow owner, bool fullscreen)
    {
        if (Interlocked.CompareExchange(ref _launching, 1, 0) != 0) return;
        try
        {
            string id = application.IdBaseString;
            if (id == "0000000000000000")
                throw new InvalidOperationException("Please launch the game from the library so its stereo profile can be identified. Add its folder in Options > Settings first.");
            // An existing standalone game owns the panel and controller until it is closed.
            using var current = Process.GetCurrentProcess();
            var otherGames = Process.GetProcessesByName(current.ProcessName);
            try
            {
                if (otherGames.Any(p => p.Id != current.Id))
                    throw new InvalidOperationException("Close the running game before launching another game from Ryujinx Stereo.");
            }
            finally { foreach (var p in otherGames) p.Dispose(); }
            var converters = Process.GetProcessesByName("Odyssey3DPlayer");
            try
            {
                if (converters.Length > 0 && StereoProfiles.Enabled(StereoProfiles.Load(id)))
                    throw new InvalidOperationException("Stop Samsung's video conversion before launching this game. Ryujinx Stereo sends 3D directly to the panel; running both conversion paths can misalign the eyes.");
            }
            finally { foreach (var p in converters) p.Dispose(); }
            if (!File.Exists(StereoProfiles.PathFor(id))) StereoProfiles.Write(id, StereoProfiles.Load(id));
            var start = CreateStartInfo(id, application.Path, AppDataManager.BaseDirPath, fullscreen || owner.ViewModel.StartGamesInFullscreen);
            if (!File.Exists(start.FileName)) throw new FileNotFoundException("The game's renderer is missing.", start.FileName);
            using Process child = Process.Start(start) ?? throw new InvalidOperationException("Game launch failed.");
            Logger.Notice.Print(LogClass.Application, $"Ryujinx Stereo launching {id} with profile {StereoProfiles.PathFor(id)}; PID {child.Id}");
            WindowState previousState = owner.WindowState;
            owner.WindowState = WindowState.Minimized;
            try { await child.WaitForExitAsync(); }
            finally { owner.WindowState = previousState; owner.Activate(); }
            owner.LoadApplications();
            Logger.Notice.Print(LogClass.Application, "Ryujinx Stereo returned to the game library.");
            if (child.ExitCode != 0) throw new InvalidOperationException($"The game exited with code {child.ExitCode}. See the emulator logs for details.");
        }
        catch (Exception error)
        {
            Logger.Error?.Print(LogClass.Application, error.ToString());
            await ContentDialogHelper.CreateErrorDialog(error.Message);
        }
        finally { Volatile.Write(ref _launching, 0); }
    }
}
