using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.OpenGL;

internal static class WonderStereoProjection
{
    internal static int EdgeInset(int width, float separation, float convergence)
    {
        if (!Enabled || width < 4) return 0;
        float displacement = MathF.Min(MathF.Abs(separation * 0.072580636f * (600f - convergence)), 0.08f);
        return Math.Min((int)MathF.Ceiling(displacement * width * 0.5f), width / 4);
    }
    internal static bool Enabled => string.Equals(
        Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "010015100b514000", StringComparison.OrdinalIgnoreCase);

    // Identify the scene camera by the data flow into all four position outputs,
    // not by a GL program number, source hash, or a reference to an unrelated UV matrix.
    internal static bool Matches(string code)
        => IsParticle(code) || IsScene(code) || IsUi(code) || CombinedUiOffset(code) >= 0;

    private static int CombinedUiOffset(string code)
    {
        for (int start = 0; start <= 1; start++)
            if (Uses(code, "gl_Position.x", $"vp_c3.data[{start}]") &&
                Uses(code, "gl_Position.y", $"vp_c3.data[{start + 1}]") &&
                Uses(code, "gl_Position.z", $"vp_c3.data[{start + 2}]") &&
                Uses(code, "gl_Position.w", $"vp_c3.data[{start + 3}]"))
                return start;
        return -1;
    }

    private static bool IsScene(string code)
        => Uses(code, "gl_Position.x", "vp_c3.data[7]") &&
           Uses(code, "gl_Position.y", "vp_c3.data[8]") &&
           Uses(code, "gl_Position.z", "vp_c3.data[9]") &&
           Uses(code, "gl_Position.w", "vp_c3.data[10]");

    internal static bool IsUi(string code) =>
        Uses(code, "gl_Position.x", "vp_c3.data[0]") &&
        Uses(code, "gl_Position.y", "vp_c3.data[1]") &&
        Uses(code, "gl_Position.z", "vp_c3.data[2]") &&
        Uses(code, "gl_Position.w", "vp_c3.data[3]") &&
        Uses(code, "gl_Position.w", "vp_c3.data[6]");

    internal static bool IsParticle(string code) =>
        Uses(code, "gl_Position.x", "vp_c8.data[4]") &&
        Uses(code, "gl_Position.y", "vp_c8.data[5]") &&
        Uses(code, "gl_Position.z", "vp_c8.data[6]") &&
        Uses(code, "gl_Position.w", "vp_c8.data[7]");

    [ThreadStatic] private static string _analysisCode;
    [ThreadStatic] private static Dictionary<string, string> _assignments;
    [ThreadStatic] private static Dictionary<string, string> _outputDependencies;
    private static readonly Regex AssignmentsPattern = new(
        @"\b(temp_\d+|gl_Position\.[xyzw])\s*=\s*([^;]+);", RegexOptions.Compiled);
    private static readonly Regex TempsPattern = new(@"\btemp_\d+\b", RegexOptions.Compiled);

    private static Dictionary<string, string> Analyze(string code)
    {
        if (!ReferenceEquals(_analysisCode, code))
        {
            _analysisCode = code;
            _assignments = new Dictionary<string, string>();
            _outputDependencies = new Dictionary<string, string>();
            foreach (Match m in AssignmentsPattern.Matches(code))
                _assignments[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return _assignments;
    }

    private static bool Uses(string code, string output, string field)
    {
        var assignments = Analyze(code);
        if (!_outputDependencies.TryGetValue(output, out string dependencies))
        {
            var text = new System.Text.StringBuilder();
            var visited = new HashSet<string>();
            var pending = new Stack<string>();
            pending.Push(output);
            while (pending.Count > 0 && visited.Count < 2048)
            {
                string name = pending.Pop();
                if (!visited.Add(name) || !assignments.TryGetValue(name, out string value)) continue;
                text.Append(value).Append('\n');
                foreach (Match temp in TempsPattern.Matches(value)) pending.Push(temp.Value);
            }
            dependencies = text.ToString();
            _outputDependencies[output] = dependencies;
        }
        return dependencies.Contains(field, StringComparison.Ordinal);
    }

    // Verified Wonder 1.0.1 terrain variants: each tile has two vec4 records,
    // beginning at c7[4]; the first record's W is a drawing-order offset.
    // Follow the actual address calculation; resource binding numbers are allocated
    // differently by the live translator and the offline shader inspector.
    internal static bool IsTerrain(string code)
    {
        if (!code.Contains("vp_c7.data[", StringComparison.Ordinal) ||
            !code.Contains("in_attr1.z", StringComparison.Ordinal)) return false;
        var assignments = new Dictionary<string, string>();
        foreach (var pair in Analyze(code))
            assignments[pair.Key] = Regex.Replace(pair.Value, @"\s+", "");
        string Find(string value)
        {
            foreach (var pair in assignments)
                if (pair.Value == value) return pair.Key;
            return "__missing";
        }
        string input = Find("in_attr1.z");
        string truncated = Find($"trunc({input})");
        string index = Find($"int({truncated})");
        string stride = Find($"{index}<<5");
        string address = Find($"{stride}+72");
        string word = Find($"uint({address})>>2");
        string next = Find($"int({word})+1");
        string row = Find($"uint({next})>>2");
        string component = Find($"{next}&3");
        string bias = Find($"vp_c7.data[int({row})][{component}]");
        if (bias == "__missing") return false;
        var pending = new Stack<string>();
        var visited = new HashSet<string>();
        pending.Push("gl_Position.z");
        while (pending.Count > 0 && visited.Count < 2048)
        {
            string current = pending.Pop();
            if (current == bias) return true;
            if (!visited.Add(current) || !assignments.TryGetValue(current, out string expression)) continue;
            foreach (Match temp in Regex.Matches(expression, @"\btemp_\d+\b")) pending.Push(temp.Value);
        }
        return false;
    }
    internal const string TerrainBias = "ryu_wonder_view_z -= vp_c7.data[4 + int(in_attr1.z) * 2].w * vp_c3.data[2].z;";
    internal static string GetCorrection(string code)
    {
        int combinedUi = CombinedUiOffset(code);
        if (combinedUi >= 0 && !IsUi(code) && !IsScene(code) && !IsParticle(code))
        {
            int wRow = combinedUi + 3;
            return $$"""
                if (abs(vp_c3.data[{{wRow}}].z + 1.0) < 0.0001 &&
                    abs(vp_c3.data[{{wRow}}].w - 967.99915) < 0.1 &&
                    gl_Position.w > 0.0)
                {
                    float ryu_wonder_ui_offset = ryu_geometry_stereo.x * 0.072580636 *
                        (600.0 - ryu_geometry_stereo.y);
                    gl_Position.x += clamp(ryu_wonder_ui_offset, -0.08, 0.08) * gl_Position.w;
                }
                else if (abs(gl_Position.w - 1.0) > 0.0001)
                    gl_Position.x += ryu_geometry_stereo.x * (gl_Position.w - ryu_geometry_stereo.y);
                """;
        }
        if (IsUi(code) && !IsScene(code) && !IsParticle(code))
        {
            // UI uses a separate perspective camera with a fixed 1920-wide canvas.
            // Calibrated against the captured gameplay projection (27.55556 units wide).
            // Shift the whole UI plane, not its original Z or individual glyphs.
            return """
                if (((abs(vp_c3.data[3].z + 1.0) < 0.0001 &&
                    abs(vp_c3.data[3].w) < 0.0001 &&
                    abs(vp_c3.data[6].w + 967.99915) < 0.1) ||
                    (abs(vp_c3.data[0].x - 0.0010416667) < 0.0000001 &&
                     abs(vp_c3.data[1].y - 0.0018518518) < 0.0000001 &&
                     abs(vp_c3.data[3].z) < 0.0001 &&
                     abs(vp_c3.data[3].w - 1.0) < 0.0001)) &&
                    gl_Position.w > 0.0)
                {
                    float ryu_wonder_ui_offset = ryu_geometry_stereo.x * 0.072580636 *
                        (600.0 - ryu_geometry_stereo.y);
                    gl_Position.x += clamp(ryu_wonder_ui_offset, -0.08, 0.08) * gl_Position.w;
                }
                else if (abs(gl_Position.w - 1.0) > 0.0001)
                    gl_Position.x += ryu_geometry_stereo.x * (gl_Position.w - ryu_geometry_stereo.y);
                """;
        }
        if (IsParticle(code))
        {
            string particleBias = "";
            if (Uses(code, "gl_Position.z", "vp_c14.data[1].w"))
                particleBias += "ryu_wonder_view_z -= vp_c14.data[1].w * vp_c8.data[2].z;\n";
            if (Uses(code, "gl_Position.z", "vp_c9.data[18].w"))
                particleBias += "ryu_wonder_view_z -= vp_c9.data[18].w;\n";
            Ryujinx.Common.Logging.Logger.Notice.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                "Wonder stereo: particle camera correction applied.");
            return Correction.Replace("vp_c3.data[10]", "vp_c8.data[7]")
                .Replace("vp_c3.data[9]", "vp_c8.data[6]")
                .Replace("vp_c3.data[7]", "vp_c8.data[4]")
                .Replace("vp_c3.data[2]", "vp_c8.data[2]")
                .Replace("// SORTING_BIAS", particleBias);
        }
        bool terrain = IsTerrain(code);
        if (terrain)
            Ryujinx.Common.Logging.Logger.Notice.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                "Wonder stereo: terrain sorting-depth correction applied (tile record W).");
        string bias = terrain ? TerrainBias :
            Uses(code, "gl_Position.z", "vp_c4.data[8].x")
            ? "ryu_wonder_view_z -= vp_c4.data[8].x * vp_c3.data[2].z;"
            : "";
        return Correction.Replace("// SORTING_BIAS", bias);
    }

    internal const string Correction = """
        // Wonder scene projection: view rows 0..2, projection rows 7..10.
        if (length(vp_c3.data[10].xyz) < 0.000001 &&
            abs(vp_c3.data[10].w - 1.0) < 0.0001 &&
            abs(vp_c3.data[9].z) > 0.00000001 &&
            abs(vp_c3.data[9].x) + abs(vp_c3.data[9].y) < 0.000001)
        {
            float ryu_wonder_view_z = (gl_Position.z - vp_c3.data[9].w) / vp_c3.data[9].z;
            // Remove the game's world-Z sorting offset from stereo only.
            // Keep gl_Position.z intact so occlusion and drawing order are unchanged.
            // SORTING_BIAS
            // Use the world-origin camera plane at the initial convergence of 600.
            // Unlike perspective, an orthographic camera's clip W carries no depth.
            float ryu_wonder_relative_depth = -ryu_wonder_view_z + vp_c3.data[2].w;
            float ryu_wonder_offset = ryu_geometry_stereo.x * abs(vp_c3.data[7].x) *
                (ryu_wonder_relative_depth - (ryu_geometry_stereo.y - 600.0));
            gl_Position.x += clamp(ryu_wonder_offset, -0.08, 0.08);
        }
        else if (abs(gl_Position.w - 1.0) > 0.0001)
            gl_Position.x += ryu_geometry_stereo.x * (gl_Position.w - ryu_geometry_stereo.y);

        """;
}
