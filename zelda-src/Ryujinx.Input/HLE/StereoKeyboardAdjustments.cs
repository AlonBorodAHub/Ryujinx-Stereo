using System;
using System.Runtime.InteropServices;
namespace Ryujinx.Input.HLE;
internal sealed class StereoKeyboardAdjustments
{
    private readonly StereoControllerAdjustments _adjustments = new();
    private long _reload;
    private (int Modifiers,int Key)[] _bindings = Array.ConvertAll(StereoKeyboardBindings.Defaults,StereoKeyboardBindings.Parse);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static GamepadStateSnapshot Map(bool active, bool ctrl, bool otherModifier,
        bool left, bool right, bool up, bool down, bool reset)
    {
        var state = new GamepadStateSnapshot();
        if (!active || !ctrl || otherModifier) return state;
        state.SetPressed(GamepadButtonInputId.LeftShoulder, true);
        state.SetPressed(GamepadButtonInputId.RightShoulder, true);
        state.SetPressed(GamepadButtonInputId.DpadLeft, left);
        state.SetPressed(GamepadButtonInputId.DpadRight, right);
        state.SetPressed(GamepadButtonInputId.DpadUp, up);
        state.SetPressed(GamepadButtonInputId.DpadDown, down);
        state.SetPressed(GamepadButtonInputId.B, reset);
        return state;
    }
    // External focus is allowed; owned emulator dialogs remain excluded while editing settings.
    internal static bool CanAdjust(bool sameProcess, bool ownedWindow) => !sameProcess || !ownedWindow;
    public void Update()
    {
        if (!OperatingSystem.IsWindows()) return;
        IntPtr window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint pid);
        bool active = CanAdjust(pid == Environment.ProcessId, GetWindow(window, 4) != IntPtr.Zero);
        long now=Environment.TickCount64;
        if(now>=_reload){_reload=now+1000;try{_bindings=Array.ConvertAll(StereoKeyboardBindings.Load(),StereoKeyboardBindings.Parse);}catch(Exception e) when(e is System.IO.IOException||e is UnauthorizedAccessException||e is System.Text.Json.JsonException||e is ArgumentException){}}
        int modifiers=(Down(0x11)?1:0)|(Down(0x12)?2:0)|(Down(0x10)?4:0)|((Down(0x5B)||Down(0x5C))?8:0);
        bool Pressed(int i)=>active&&modifiers==_bindings[i].Modifiers&&Down(_bindings[i].Key);
        _adjustments.Process(Map(active,true,false,Pressed(0),Pressed(1),Pressed(2),Pressed(3),Pressed(4)),now);
    }
}
