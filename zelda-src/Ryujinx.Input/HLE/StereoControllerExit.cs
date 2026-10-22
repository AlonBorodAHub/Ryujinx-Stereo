using System.Threading;
namespace Ryujinx.Input.HLE;
// Reads mapped input only; never opens a second HID backend.
public sealed class StereoControllerExit
{
    private long _started = -1;
    private bool _fired;
    private static int _pending;
    public static bool CloseWindowRequested { get; set; }
    public static bool Consume() => Interlocked.Exchange(ref _pending, 0) != 0;
    public void Update(bool left, bool right, long now)
    {
        if (!left || !right) { _started = -1; _fired = false; return; }
        if (_started < 0) _started = now;
        if (!_fired && now - _started >= 600) { _fired = true; Interlocked.Exchange(ref _pending, 1); }
    }
}
