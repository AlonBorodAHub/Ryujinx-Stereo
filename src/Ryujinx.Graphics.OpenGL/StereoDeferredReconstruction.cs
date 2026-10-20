using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.OpenGL
{
    internal static class StereoDeferredReconstruction
    {
        internal static string Rewrite(string fragment, string vertex)
        {
            if (string.IsNullOrEmpty(vertex)) return fragment;
            var defs = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(vertex, @"\b(temp_\d+)\s*=\s*([^;]+);")) defs[m.Groups[1].Value] = m.Groups[2].Value;
            var attributes = new Dictionary<string, (string Scale, string DepthRow)>();
            bool world = string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "010028600ebda000", StringComparison.OrdinalIgnoreCase);
            var layouts = new List<(string Buffer, string RayRow, string DepthRow)> { ("3", "11", "10"), ("4", "23", "22") };
            // 3D World stores inverse-projection rays in row 22 and the linear
            // depth scale/bias across rows 21.y and 20.w, unlike Odyssey/Zelda.
            if (world) layouts.Add(("4", "22", "world-linear-depth"));
            foreach (var layout in layouts)
            {
                string row = $"vp_c{layout.Buffer}.data[{layout.RayRow}]";
                foreach (Match m in Regex.Matches(vertex, @"\b(out_attr\d+\.x)\s*=\s*([^;]+);"))
                {
                    var pending = new Queue<(string Expr, int Depth)>();
                    var visited = new HashSet<string>();
                    pending.Enqueue((m.Groups[2].Value, 0));
                    bool scale = false, offset = false;
                    while (pending.Count > 0 && visited.Count < 80)
                    {
                        var item = pending.Dequeue();
                        scale |= item.Expr.Contains(row + ".x", StringComparison.Ordinal);
                        offset |= item.Expr.Contains(row + ".z", StringComparison.Ordinal);
                        if (item.Depth >= 8) continue;
                        foreach (Match t in Regex.Matches(item.Expr, @"\btemp_\d+\b"))
                            if (visited.Add(t.Value) && defs.TryGetValue(t.Value, out string next)) pending.Enqueue((next, item.Depth + 1));
                    }
                    if (scale && offset && fragment.Contains($"}} fp_c{layout.Buffer};", StringComparison.Ordinal))
                        attributes[m.Groups[1].Value.Replace("out_attr", "in_attr")] = ($"fp_c{layout.Buffer}.data[{layout.RayRow}].x", $"fp_c{layout.Buffer}.data[{layout.DepthRow}]");
                }
            }
            if (attributes.Count == 0) return fragment;
            var rays = new Dictionary<string, (string Scale, string DepthRow, int Sign)>();
            foreach (Match m in Regex.Matches(fragment, @"\b(temp_\d+) = ([^;]+);"))
            {
                string expression = m.Groups[2].Value;
                if (attributes.TryGetValue(expression, out var attr)) rays[m.Groups[1].Value] = (attr.Scale, attr.DepthRow, 1);
                Match neg = Regex.Match(expression, @"^0\.0 - (temp_\d+)$");
                if (neg.Success && rays.TryGetValue(neg.Groups[1].Value, out var ray)) rays[m.Groups[1].Value] = (ray.Scale, ray.DepthRow, -ray.Sign);
            }
            var depthRows = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(fragment, @"\b(temp_\d+) = fma\(temp_\d+, (fp_c\d+\.data\[\d+\])\.[yz], \2\.x\);"))
                depthRows[m.Groups[1].Value] = m.Groups[2].Value;
            if (world)
                foreach (Match m in Regex.Matches(fragment, @"\b(temp_\d+) = fma\(temp_\d+, fp_c4\.data\[21\]\.y, fp_c4\.data\[20\]\.w\);"))
                    depthRows[m.Groups[1].Value] = "fp_c4.data[world-linear-depth]";
            int count = 0;
            fragment = Regex.Replace(fragment, @"\b(temp_\d+) = (temp_\d+) \* (temp_\d+);", m =>
            {
                string a = m.Groups[2].Value, b = m.Groups[3].Value;
                if (!rays.ContainsKey(a)) (a, b) = (b, a);
                if (!rays.TryGetValue(a, out var ray) || !depthRows.TryGetValue(b, out string depthRow) || depthRow != ray.DepthRow) return m.Value;
                count++;
                return m.Value + $" {m.Groups[1].Value} += {ray.Sign}.0 * ryu_stereo_deferred.x * ({b} - ryu_stereo_deferred.y) * {ray.Scale};";
            });
            if (count == 0) return fragment;
            int main = fragment.IndexOf("void main()", StringComparison.Ordinal);
            if (main < 0) return fragment;
            Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo: deferred lighting eye reconstruction corrected in {count} expressions");
            return fragment.Insert(main, "uniform vec2 ryu_stereo_deferred;\n");
        }
    }
}
