using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Collections.Generic;

internal static class StereoLauncher {
    static string Quote(string text) {
        var b=new StringBuilder("\""); int slashes=0;
        foreach(char c in text) {
            if(c=='\\') {slashes++;continue;}
            if(c=='\"') {b.Append('\\',slashes*2+1);b.Append(c);slashes=0;continue;}
            b.Append('\\',slashes);slashes=0;b.Append(c);
        }
        b.Append('\\',slashes*2);b.Append('"');return b.ToString();
    }
    internal static ProcessStartInfo Build(string root,string[] args) {
        string runtime=Path.Combine(root,"emulator");
        var info=new ProcessStartInfo(Path.Combine(runtime,"Ryujinx.exe")) {
            UseShellExecute=false,WorkingDirectory=runtime,CreateNoWindow=true
        };
        bool safe=false;var command=new StringBuilder();
        foreach(string arg in args) {if(arg=="--safe-stereo"){safe=true;continue;}if(command.Length!=0)command.Append(' ');command.Append(Quote(arg));}
        info.Arguments=command.ToString();
        var inherited=new List<string>();
        foreach(string key in info.EnvironmentVariables.Keys)if(key.StartsWith("RYU_STEREO_",StringComparison.OrdinalIgnoreCase)||key=="RYU_INPUT_TRACE")inherited.Add(key);
        foreach(string key in inherited)info.EnvironmentVariables.Remove(key);
        info.EnvironmentVariables["RYU_GEOMETRY_STEREO"]="1";
        info.EnvironmentVariables["RYU_STEREO_LAYERED"]="1";
        info.EnvironmentVariables["RYU_STEREO_INSTANCED"]=safe?"0":"1";
        info.EnvironmentVariables["RYU_STEREO_ARRAY_SAMPLING"]=safe?"0":"1";
        info.EnvironmentVariables["RYU_STEREO_SETTINGS"]=Path.Combine(runtime,"StereoSettings.json");
        info.EnvironmentVariables["RYU_STEREO_BACKGROUND_COMPILATION"]="0";
        info.EnvironmentVariables["RYU_STEREO_SEPARATION"]="0.02028";
        info.EnvironmentVariables["RYU_STEREO_CONVERGENCE"]="600";
        return info;
    }
    [STAThread] static int Main(string[] args) {
        string root=AppDomain.CurrentDomain.BaseDirectory;
        try {
            if(args.Length==1&&args[0]=="--check") {
                string[] required={"Ryujinx.exe","Ryujinx.dll","Ryujinx.runtimeconfig.json","Ryujinx.Graphics.OpenGL.dll","Ryujinx.Graphics.Gpu.dll","SDL2.dll","hostfxr.dll","coreclr.dll","StereoSettings.json","portable/Config.json"};
                foreach(string relative in required)if(!File.Exists(Path.Combine(root,"emulator",relative.Replace('/',Path.DirectorySeparatorChar))))throw new FileNotFoundException("Missing runtime file: "+relative);
                var launch=Build(root,new[]{"--fullscreen",@"C:\Example Games\Odyssey.nsp"});
                if(launch.UseShellExecute||launch.EnvironmentVariables["RYU_STEREO_ARRAY_SAMPLING"]!="1"||launch.Arguments!="\"--fullscreen\" \"C:\\Example Games\\Odyssey.nsp\"")throw new Exception("Launcher environment/argument check failed.");
                var fallback=Build(root,new[]{"--safe-stereo","game.nsp"});
                if(fallback.Arguments!="\"game.nsp\""||fallback.EnvironmentVariables["RYU_STEREO_ARRAY_SAMPLING"]!="0"||fallback.EnvironmentVariables["RYU_STEREO_INSTANCED"]!="0"||fallback.EnvironmentVariables["RYU_GEOMETRY_STEREO"]!="1")throw new Exception("Stereo fallback check failed.");
                File.WriteAllText(Path.Combine(root,"launcher-check.txt"),"PASS: standalone runtime layout, stereo environment and argument quoting. No emulator process or controller was opened.\r\n");
                return 0;
            }
            if(!File.Exists(Path.Combine(root,"emulator","Ryujinx.exe")))throw new FileNotFoundException("Extract the entire ZIP to a writable folder before launching. The emulator folder is missing.");
            Process.Start(Build(root,args));return 0;
        } catch(Exception error) {
            try{File.WriteAllText(Path.Combine(root,"launcher-error.txt"),error.ToString());}catch{}
            if(args.Length==1&&args[0]=="--check")return 1;
            MessageBox.Show(error.Message,"Ryujinx Stereo",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;
        }
    }
}
