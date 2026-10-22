using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
namespace Ryujinx.Graphics.OpenGL;
internal static class TotkShadowFiltering
{
    internal static string Rewrite(string source)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase)) return source;
        var names = new HashSet<string>();
        foreach (Match m in Regex.Matches(source, @"uniform sampler2DArrayShadow (fp_t_\w+);")) names.Add(m.Groups[1].Value);
        if (names.Count == 0) return source;
        int count = 0;
        string result = Regex.Replace(source, @"\btextureLod\((fp_t_\w+),", m => {
            if (!names.Contains(m.Groups[1].Value)) return m.Value;
            count++; return "ryu_totk_shadow(" + m.Groups[1].Value + ",";
        });
        int main = result.IndexOf("void main()", StringComparison.Ordinal);
        if (count == 0 || main < 0) return source;
        // On-demand diagnostic only. Zero preserves the original shadow lookup.
        string helper = @"
uniform int ryu_totk_shadow_probe;
float ryu_totk_shadow(sampler2DArrayShadow s, vec4 p, float lod)
{
    if (ryu_totk_shadow_probe == 1) return 1.0;
    // Diagnostic offsets in D16 depth units; zero remains exactly unchanged.
    if (ryu_totk_shadow_probe == 2) p.w += 1.0 / 65535.0;
    if (ryu_totk_shadow_probe == 3) p.w -= 1.0 / 65535.0;
    if (ryu_totk_shadow_probe == 4) p.w += 8.0 / 65535.0;
    if (ryu_totk_shadow_probe == 5) p.w -= 8.0 / 65535.0;
    return textureLod(s, p, lod);
}
";
        return result.Insert(main, helper);
    }
}
