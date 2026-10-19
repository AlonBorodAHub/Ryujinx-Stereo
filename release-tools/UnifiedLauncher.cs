using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class UnifiedLauncher
{
    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { text.Append('\\', slashes * 2 + 1); text.Append(c); }
            else { text.Append('\\', slashes); text.Append(c); }
            slashes = 0;
        }
        text.Append('\\', slashes * 2); return text.Append('"').ToString();
    }
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            string home = AppDomain.CurrentDomain.BaseDirectory;
            var arguments = new StringBuilder("--root-data-dir ").Append(Quote(Path.Combine(home, "portable")));
            foreach (string arg in args) arguments.Append(' ').Append(Quote(arg));
            Process.Start(new ProcessStartInfo(Path.Combine(home, "Ryujinx.exe"), arguments.ToString())
            { WorkingDirectory = home, UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Ryujinx Stereo"); }
    }
}
