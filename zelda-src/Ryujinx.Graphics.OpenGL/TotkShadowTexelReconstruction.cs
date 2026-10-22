using System;
using System.Text.RegularExpressions;
namespace Ryujinx.Graphics.OpenGL;
internal static class TotkShadowTexelReconstruction
{
    internal static string Rewrite(string code)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase) ||
            !code.Contains("temp_22 = textureGather(fp_t_tcb_8, vec2(temp_7, temp_6), 0).yw;") ||
            !code.Contains("temp_25 = textureGather(fp_t_tcb_A, vec2(temp_7, temp_6), 0).xz;") ||
            !code.Contains("uniform vec2 ryu_stereo_reconstruction;")) return code;
        string center = "ryu_shadow_sample_position(vec2(temp_7, temp_6), vec2(textureSize(fp_t_tcb_8, 0)), vec2(0.0), temp_85)";
        string nextY = "ryu_shadow_sample_position(vec2(temp_7, temp_6), vec2(textureSize(fp_t_tcb_A, 0)), vec2(0.0, 1.0), temp_86)";
        string nextX = "ryu_shadow_sample_position(vec2(temp_7, temp_6), vec2(textureSize(fp_t_tcb_A, 0)), vec2(1.0, 0.0), temp_87)";
        var replacements = new (int Id, string Expression)[] {
            (92, center + ".y"), (96, center + ".x"),
            (104, nextY + ".y - temp_92"), (108, nextY + ".x - temp_96"),
            (111, nextX + ".y - temp_92"), (116, nextX + ".x - temp_96")
        };
        foreach (var item in replacements)
        {
            string pattern = @"\btemp_" + item.Id + @" = [^;]+;(?: temp_" + item.Id + @" \+= ryu_stereo_reconstruction[^;]+;)?";
            if (Regex.Matches(code, pattern).Count != 1) return code;
        }
        foreach (var item in replacements)
        {
            string pattern = @"\btemp_" + item.Id + @" = [^;]+;(?: temp_" + item.Id + @" \+= ryu_stereo_reconstruction[^;]+;)?";
            code = Regex.Replace(code, pattern, "temp_" + item.Id + " = " + item.Expression + ";");
        }
        string helper = @"
vec2 ryu_shadow_sample_position(vec2 uv, vec2 size, vec2 tap, float depth)
{
    vec2 center = (floor(uv * size - 0.5) + tap + 0.5) / size;
    vec2 ray = vec2(2.0 * center.x - 1.0 + fp_c4.data[7].z,
                    1.0 - 2.0 * center.y + fp_c4.data[8].z);
    vec2 position = depth * ray * fp_c4.data[17].xy;
    position.x += ryu_stereo_reconstruction.x * (-depth + ryu_stereo_reconstruction.y) * fp_c4.data[17].x;
    return position;
}
";
        return code.Insert(code.IndexOf("void main()", StringComparison.Ordinal), helper);
    }
}
