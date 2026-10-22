using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ryujinx.Ava.Stereo;

// Stores user controls beside the renderer-specific compatibility settings.
// Unknown properties are retained when the user changes depth/convergence.
internal static class StereoProfiles
{
    public const string World = "010028600ebda000";
    public const string Wonder = "010015100b514000";
    public const string Mario = "0100000000010000";
    public const string Zelda = "01007ef00011e000";
    public const string Tears = "0100f2c0115b6000";
    public static bool IsZelda(string id) => Normalize(id) is Zelda or Tears;
    public static string Home => Path.GetFullPath(Environment.GetEnvironmentVariable("RJ_STEREO_HOME") ?? AppContext.BaseDirectory);
    public static string DirectoryPath => Path.Combine(Home, "profiles");
    public static string Normalize(string id)
    {
        if (id == null || id.Length != 16 || !ulong.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong number) || number == 0)
            throw new ArgumentException("Select a game with a valid title ID.");
        return (number & ~0x1FFFUL).ToString("x16");
    }
    public static string PathFor(string id) => Path.Combine(DirectoryPath, Normalize(id) + ".json");
    public static double DefaultSeparation(string id) => IsZelda(id) ? .016224 : Normalize(id) == Mario ? .02028 : .012;
    public static double DefaultConvergence(string id) => IsZelda(id) ? 6 : 600;
    public static JsonObject Load(string id)
    {
        string path = PathFor(id);
        if (File.Exists(path))
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Stereo profile must be a JSON object.");
        return new JsonObject
        {
            ["stereo_enabled"] = id == Mario || IsZelda(id),
            ["separation"] = DefaultSeparation(id), ["convergence"] = DefaultConvergence(id),
            ["direct_output"] = IsZelda(id), ["direct_panel_output"] = true,
            ["runtime_resolution_scale"] = 1.5, ["alternating_eyes"] = true,
            ["layered_framebuffer"] = true, ["instanced_eyes"] = true
        };
    }
    public static double Number(JsonObject profile, string key, double fallback) =>
        profile[key] is JsonValue value && value.TryGetValue<double>(out double n) && double.IsFinite(n) ? n : fallback;
    public static bool UsesPanel(JsonObject profile) => profile["output_format"]?.GetValue<string>() is string mode ? mode == "panel" : profile["direct_panel_output"]?.GetValue<bool>() == true;
    public static bool Enabled(JsonObject profile) => profile["stereo_enabled"]?.GetValue<bool>() ?? true;
    public static void Save(string id, bool enabled, double depthPercent, double convergence)
    {
        if (!double.IsFinite(depthPercent) || depthPercent < 0 || depthPercent > 200 ||
            !double.IsFinite(convergence) || convergence < 0 || convergence > 10000)
            throw new ArgumentOutOfRangeException(nameof(depthPercent), "Depth or convergence is outside the allowed range.");
        JsonObject profile = Load(id);
        profile["stereo_enabled"] = enabled;
        profile["separation"] = DefaultSeparation(id) * depthPercent / 100;
        profile["convergence"] = convergence;
        Write(id, profile);
    }
    public static void Write(string id, JsonObject profile)
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = PathFor(id);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
