using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.OpenGL
{
    internal static class MetroidDreadReconstruction
    {
        internal static string Rewrite(string source)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "010093801237c000", StringComparison.OrdinalIgnoreCase) ||
                !source.Contains("gl_FragCoord.x", StringComparison.Ordinal) || source.Contains("ryu_dread_dx", StringComparison.Ordinal)) return source;
            // Dread's deferred light volumes reconstruct world positions with the
            // inverse view-projection in fp_c4 rows 12..15. Undo the stereo NDC
            // translation before that reconstruction; leave G-buffer sampling in
            // the current eye. This also corrects the shadow receiver position.
            var xTerms = new Dictionary<int, string>();
            string ndcX = null;
            foreach (Match m in Regex.Matches(source, @"\b(temp_\d+) = (temp_\d+) \* fp_c4\.data\[(1[2-5])\]\.x;"))
            {
                if (ndcX != null && ndcX != m.Groups[2].Value) return source;
                ndcX = m.Groups[2].Value;
                xTerms[int.Parse(m.Groups[3].Value)] = m.Groups[1].Value;
            }
            if (xTerms.Count != 4) return source;
            var partials = new Dictionary<int, string>();
            string ndcY = null;
            foreach (Match m in Regex.Matches(source, @"\b(temp_\d+) = fma\((temp_\d+), fp_c4\.data\[(1[2-5])\]\.y, (temp_\d+)\);"))
            {
                int row = int.Parse(m.Groups[3].Value);
                if (!xTerms.TryGetValue(row, out string x) || x != m.Groups[4].Value) continue;
                if (ndcY != null && ndcY != m.Groups[2].Value) return source;
                ndcY = m.Groups[2].Value;
                partials[row] = m.Groups[1].Value;
            }
            if (partials.Count != 4) return source;
            Match depth = null;
            foreach (Match candidate in Regex.Matches(source, @"\b(temp_\d+) = fma\((temp_\d+), 2\.0, -1\.0\);"))
            {
                string z = candidate.Groups[1].Value;
                bool matches = true;
                foreach (var item in partials)
                    matches &= Regex.IsMatch(source.Substring(candidate.Index + candidate.Length), $@"\btemp_\d+ = fma\({z}, fp_c4\.data\[{item.Key}\]\.z, {item.Value}\);");
                if (matches) { depth = candidate; break; }
            }
            if (depth == null) return source;
            string ndcZ = depth.Groups[1].Value;
            foreach (var item in partials)
            {
                string expected = $@"\btemp_\d+ = fma\({ndcZ}, fp_c4\.data\[{item.Key}\]\.z, {item.Value}\);";
                if (!Regex.IsMatch(source.Substring(depth.Index + depth.Length), expected)) return source;
                if (source.IndexOf(item.Value + " =", StringComparison.Ordinal) > depth.Index) return source;
            }
            // H.w is 1/clip.w. Solve the homogeneous correction as well, so this
            // remains exact when inverse-VP row 15.x is nonzero.
            string correction = "\nfloat ryu_dread_dx = ryu_stereo_deferred.x * (1.0 - ryu_stereo_deferred.y * " +
                $"(fma({ndcZ}, fp_c4.data[15].z, {partials[15]}) + fp_c4.data[15].w)) / " +
                "(1.0 - ryu_stereo_deferred.x * ryu_stereo_deferred.y * fp_c4.data[15].x);\n";
            foreach (var item in partials) correction += $"{item.Value} -= ryu_dread_dx * fp_c4.data[{item.Key}].x;\n";
            source = source.Insert(depth.Index + depth.Length, correction);
            int main = source.IndexOf("void main()", StringComparison.Ordinal);
            if (main < 0) return source;
            Logger.Notice.Print(LogClass.Gpu, "GeometryStereo: Metroid Dread homogeneous shadow/light receiver corrected");
            return source.Insert(main, "uniform vec2 ryu_stereo_deferred;\n");
        }
    }
}
