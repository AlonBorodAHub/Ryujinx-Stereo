using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.OpenGL
{
    internal static class ZeldaStereoEffects
    {
        internal static (string Code, string Correction) ExtendProjectedCoordinates(string source)
        {
            var xs = Regex.Matches(source, @"gl_Position\.x = (temp_\d+);");
            if (xs.Count != 1) return (source, "");
            string final = xs[0].Groups[1].Value;
            var defs = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(source, @"\b(temp_\d+) = ([^;]+);")) defs[m.Groups[1].Value] = m.Groups[2].Value;
            if (!defs.TryGetValue(final, out string expression)) return (source, "");
            Match mul = Regex.Match(expression, @"^(temp_\d+) \* (temp_\d+)$");
            if (!mul.Success) return (source, "");
            for (int i = 0; i < 2; i++)
            {
                string basis = mul.Groups[i + 1].Value, scale = mul.Groups[2 - i].Value;
                // Zelda's particle culling/fade scales clip XYZ after building its
                // projected depth UVs. Differentiate those UVs before that scale.
                if (!defs.TryGetValue(scale, out string scaleExpression) || !scaleExpression.Contains("clamp(", StringComparison.Ordinal)) continue;
                string analysis = source.Replace(xs[0].Value, $"gl_Position.x = {basis};");
                string correction = StereoProjectedVaryings.Correction(analysis);
                if (correction.Length == 0) continue;
                correction = correction.Replace("(gl_Position.w - ryu_stereo_projected_varying.y);", "(gl_Position.w - ryu_stereo_projected_varying.y) / max(ryu_zelda_clip_scale, 0.00001);");
                int main = source.IndexOf("void main()", StringComparison.Ordinal);
                if (main < 0) return (source, "");
                source = source.Insert(main, "float ryu_zelda_clip_scale = 1.0;\n");
                source = source.Replace(xs[0].Value, xs[0].Value + $" ryu_zelda_clip_scale = {scale};");
                Logger.Notice.Print(LogClass.Gpu, "GeometryStereo: Zelda scaled effect projection corrected");
                return (source, correction);
            }
            return (source, "");
        }
    }
}
