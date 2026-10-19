using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.OpenGL
{
    internal static class StereoShadowReconstruction
    {
        internal static string Rewrite(string fragment, string vertex)
        {
            if (string.IsNullOrEmpty(vertex) || !fragment.Contains("} fp_c3;", StringComparison.Ordinal)) return fragment;
            var definitions = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(vertex, @"\b(temp_\d+)\s*=\s*([^;]+);")) definitions[m.Groups[1].Value] = m.Groups[2].Value;
            bool CameraRay(string expression)
            {
                bool scale = false, offset = false;
                var pending = new Queue<(string Expression, int Depth)>();
                var visited = new HashSet<string>();
                pending.Enqueue((expression, 0));
                while (pending.Count > 0 && visited.Count < 200)
                {
                    var item = pending.Dequeue();
                    scale |= item.Expression.Contains("vp_c3.data[0].z", StringComparison.Ordinal);
                    offset |= item.Expression.Contains("vp_c3.data[34].z", StringComparison.Ordinal);
                    if (scale && offset) return true;
                    if (item.Depth >= 12) continue;
                    foreach (Match t in Regex.Matches(item.Expression, @"\btemp_\d+\b"))
                        if (visited.Add(t.Value) && definitions.TryGetValue(t.Value, out string next)) pending.Enqueue((next, item.Depth + 1));
                }
                return false;
            }
            var attributes = new HashSet<string>();
            foreach (Match m in Regex.Matches(vertex, @"\b(out_attr\d+\.[xyzw])\s*=\s*([^;]+);"))
                if (CameraRay(m.Groups[2].Value)) attributes.Add(m.Groups[1].Value.Replace("out_attr", "in_attr"));
            if (attributes.Count == 0) return fragment;
            var rays = new HashSet<string>();
            foreach (Match m in Regex.Matches(fragment, @"\b(temp_\d+)\s*=\s*(in_attr\d+\.[xyzw]);"))
                if (attributes.Contains(m.Groups[2].Value)) rays.Add(m.Groups[1].Value);
            if (rays.Count == 0) return fragment;
            int count = 0;
            string Correct(Match m, int a, int b)
            {
                string depth = rays.Contains(m.Groups[a].Value) ? m.Groups[b].Value : rays.Contains(m.Groups[b].Value) ? m.Groups[a].Value : null;
                if (depth == null) return m.Value;
                count++;
                return m.Value + $" {m.Groups[1].Value} += ryu_stereo_reconstruction.x * ({depth} + ryu_stereo_reconstruction.y) * fp_c3.data[0].z;";
            }
            fragment = Regex.Replace(fragment, @"\b(temp_\d+) = (temp_\d+) \* (temp_\d+);", m => Correct(m, 2, 3));
            fragment = Regex.Replace(fragment, @"\b(temp_\d+) = fma\((temp_\d+), (temp_\d+), (temp_\d+)\);", m => Correct(m, 2, 3));
            if (count == 0) return fragment;
            int main = fragment.IndexOf("void main()", StringComparison.Ordinal);
            if (main < 0) return fragment;
            fragment = fragment.Insert(main, "uniform vec2 ryu_stereo_reconstruction;\n");
            Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo: camera reconstruction corrected in {count} expressions ({attributes.Count} ray attributes)");
            return fragment;
        }
    }
}
