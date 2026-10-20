using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ryujinx.Input.HLE;

// Uses the emulator's existing mapped controller snapshot; no HID ownership.
internal sealed class StereoControllerAdjustments
{
    private int _direction;
    private long _next;
    private double? _originalDepth;
    private bool _resetHeld;
    private string _profilePath;
    internal GamepadStateSnapshot Process(GamepadStateSnapshot state, long now)
    {
        string path = Environment.GetEnvironmentVariable("RYU_STEREO_SETTINGS");
        if (Environment.GetEnvironmentVariable("RYU_GEOMETRY_STEREO") != "1" ||
            string.IsNullOrEmpty(path) ||
            !state.IsPressed(GamepadButtonInputId.LeftShoulder) ||
            !state.IsPressed(GamepadButtonInputId.RightShoulder))
        {
            _direction = 0;
            _resetHeld = false;
            return state;
        }
        if (_profilePath != path) { _profilePath = path; _originalDepth = null; _resetHeld = false; _direction = 0; }
        bool reset = state.IsPressed(GamepadButtonInputId.B);
        int x = (state.IsPressed(GamepadButtonInputId.DpadRight) ? 1 : 0) -
                (state.IsPressed(GamepadButtonInputId.DpadLeft) ? 1 : 0);
        int y = (state.IsPressed(GamepadButtonInputId.DpadUp) ? 1 : 0) -
                (state.IsPressed(GamepadButtonInputId.DpadDown) ? 1 : 0);
        foreach (var key in new[] { GamepadButtonInputId.LeftShoulder, GamepadButtonInputId.RightShoulder,
            GamepadButtonInputId.DpadLeft, GamepadButtonInputId.DpadRight,
            GamepadButtonInputId.DpadUp, GamepadButtonInputId.DpadDown })
            state.SetPressed(key, false);
        state.SetPressed(GamepadButtonInputId.B, false);
        if (reset && _resetHeld) return state;
        _resetHeld = reset;
        int direction = (x + 1) + (y + 1) * 3 + 1;
        if (!reset && x == 0 && y == 0) { _direction = 0; return state; }
        bool first = direction != _direction;
        if (!reset && !first && now < _next) return state;
        _direction = reset ? 0 : direction;
        _next = now + (first ? 400 : 150);
        try
        {
            var profile = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (profile == null) return state;
            bool zelda = string.Equals(Path.GetFileNameWithoutExtension(path), "01007ef00011e000", StringComparison.OrdinalIgnoreCase);
            bool mario = string.Equals(Path.GetFileNameWithoutExtension(path), "0100000000010000", StringComparison.OrdinalIgnoreCase);
            double baseline = zelda ? .016224 : mario ? .02028 : .012;
            double separation = Number(profile, "separation", baseline);
            double convergence = Number(profile, "convergence", zelda ? 6 : 600);
            if (reset)
            {
                profile["separation"] = Number(profile, "default_separation", _originalDepth ?? separation);
                profile["convergence"] = Number(profile, "default_convergence", zelda ? 6 : 600);
            }
            else
            {
                if (y != 0)
                {
                    if (!_originalDepth.HasValue) _originalDepth = separation;
                    profile["separation"] = Math.Clamp(separation + y * baseline * .02, 0, baseline * 2);
                }
                if (x != 0) profile["convergence"] = Math.Clamp(convergence + x * (zelda ? .5 : 25), 0, 10000);
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, true);
                StereoAdjustmentFeedback.Publish(
                    Number(profile, "separation", separation) / baseline * 100,
                    Number(profile, "convergence", convergence), reset);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException || e is InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Stereo controller adjustment: " + e.Message);
        }
        return state;
    }
    private static double Number(JsonObject profile, string key, double fallback) =>
        profile[key] is JsonValue value && value.TryGetValue<double>(out double number) && double.IsFinite(number) ? number : fallback;
}
