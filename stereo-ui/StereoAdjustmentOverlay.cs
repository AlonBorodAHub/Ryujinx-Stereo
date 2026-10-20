using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Input.HLE;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Ava.Stereo;

internal sealed class StereoAdjustmentOverlay : Window
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RectNative rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetStyle(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetStyle(nint window, int index, nint style);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    private readonly TextBlock _text = new()
    {
        FontSize = 34, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center
    };
    private long _expires;
    private readonly MainWindow _owner;
    private readonly DispatcherTimer _timer;

    private StereoAdjustmentOverlay(MainWindow owner)
    {
        _owner = owner;
        Width = 760; Height = 90;
        CanResize = false; ShowActivated = false; ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        Topmost = true; Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        IsHitTestVisible = false;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 15, 20, 29)),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 14),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = _text
        };
        Opened += (_, _) =>
        {
            nint handle = TryGetPlatformHandle()?.Handle ?? 0;
            if (handle != 0)
                SetStyle(handle, -20, GetStyle(handle, -20) | (nint)0x080000A0); // no activation, click-through, tool window
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();
        owner.Closed += (_, _) => { _timer.Stop(); Close(); };
        _timer.Start();
    }

    internal static void Attach(MainWindow owner)
    {
        if (OperatingSystem.IsWindows()) _ = new StereoAdjustmentOverlay(owner);
    }

    private void Tick()
    {
        var update = StereoAdjustmentFeedback.Take();
        nint foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out uint process);
        bool playing = process == Environment.ProcessId && _owner.ViewModel.AppHost != null;
        long now = Environment.TickCount64;
        if (!playing)
        {
            _expires = 0;
            if (IsVisible) Hide();
            return;
        }
        if (update != null)
        {
            _text.Text = $"{(update.Reset ? "Reset  •  " : "")}Depth {update.DepthPercent:0}%    Convergence {update.Convergence:0.##}";
            _expires = now + 2200;
            Opacity = 1;
            if (!IsVisible) Show(_owner);
        }
        if (now >= _expires)
        {
            if (IsVisible) Hide();
            return;
        }
        if (IsVisible && GetWindowRect(foreground, out var game))
        {
            double scale = RenderScaling;
            int width = Math.Min((int)(Width * scale), Math.Max(200, game.Right - game.Left - 32));
            int height = (int)(Height * scale);
            nint handle = TryGetPlatformHandle()?.Handle ?? 0;
            if (handle != 0)
                SetWindowPos(handle, new nint(-1), game.Left + (game.Right - game.Left - width) / 2,
                    game.Top + (int)(28 * scale), width, height, 0x0010);
            Opacity = Math.Clamp((_expires - now) / 400.0, 0, 1);
        }
    }
}
