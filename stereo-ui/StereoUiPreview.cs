using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ryujinx.Ava.UI.Windows;
using System;
using System.IO;

namespace Ryujinx.Ava.Stereo;

internal static class StereoUiPreview
{
    internal static void Run(MainWindow owner)
    {
        string output = Environment.GetEnvironmentVariable("RJ_STEREO_PREVIEW_PATH");
        if (string.IsNullOrWhiteSpace(output)) return;
        var window = new StereoSettingsWindow(owner.ViewModel.Applications, StereoRuntime.ActiveId ?? StereoProfiles.Mario);
        window.Show(owner);
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(output);
                using var library = new RenderTargetBitmap(new PixelSize((int)owner.Bounds.Width, (int)owner.Bounds.Height), new Vector(96, 96));
                library.Render(owner);
                library.Save(Path.ChangeExtension(output, ".library.png"));
            }
            finally { window.Close(); owner.Close(); }
        }, TimeSpan.FromSeconds(4));
    }
}
