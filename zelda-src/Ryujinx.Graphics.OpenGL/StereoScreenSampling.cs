using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.OpenGL
{
    internal static class StereoScreenSampling
    {
        // Resource bindings, rather than shader names, identify per-eye scene buffers.
        // Perspective fragments must sample these buffers at their rasterized eye position.
        internal static string Rewrite(string code)
        {
            var samplers = new Dictionary<string, int>();
            foreach (Match m in Regex.Matches(code, @"layout\s*\(binding\s*=\s*(\d+)\)\s*uniform\s+sampler2D\s+(\w+)\s*;"))
            {
                int unit = int.Parse(m.Groups[1].Value);
                if (unit < 32) samplers[m.Groups[2].Value] = unit;
            }
            int count = 0;
            code = Regex.Replace(code, @"\btexture\((\w+),\s*vec2\(([^()]*)\)\)", m =>
            {
                if (!samplers.TryGetValue(m.Groups[1].Value, out int unit)) return m.Value;
                count++;
                return $"texture({m.Groups[1].Value}, ryu_stereo_screen_uv(vec2({m.Groups[2].Value}), {unit}))";
            });
            if (count == 0) return code;
            int main = code.IndexOf("void main()", System.StringComparison.Ordinal);
            if (main < 0) return code;
            return code.Insert(main,
                "uniform uint ryu_stereo_screen_mask;\nuniform vec4 ryu_stereo_screen_viewport;\n" +
                "vec2 ryu_stereo_screen_uv(vec2 uv, int unit) {\n" +
                " if ((ryu_stereo_screen_mask & (1u << uint(unit))) != 0u && abs(gl_FragCoord.w - 1.0) > 0.00001)\n" +
                "  return gl_FragCoord.xy * ryu_stereo_screen_viewport.xy + ryu_stereo_screen_viewport.zw;\n" +
                " return uv;\n}\n");
        }
    }
}
