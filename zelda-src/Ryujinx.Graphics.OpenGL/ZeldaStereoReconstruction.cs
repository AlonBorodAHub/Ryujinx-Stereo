using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.OpenGL
{
    internal static class ZeldaStereoReconstruction
    {
        internal static string Rewrite(string fragment, string vertex)
        {
            // TOTK moved the shared camera block from guest slot 3 to slot 4.
            // Keep BOTW's path unchanged and still require the ray/depth signatures below.
            int cameraSlot = string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase) &&
                (vertex?.Contains("vp_c4.data[17].x", StringComparison.Ordinal) == true ||
                 (vertex?.Contains("vp_c4.data[17].y", StringComparison.Ordinal) == true && vertex.Contains("vp_c4.data[15].z", StringComparison.Ordinal))) ? 4 : 3;
            string vpCamera = $"vp_c{cameraSlot}", fpCamera = $"fp_c{cameraSlot}";
            if (string.IsNullOrEmpty(vertex) || !vertex.Contains("gl_Position.w = 1.0;", StringComparison.Ordinal) ||
                (!vertex.Contains(vpCamera + ".data[17].x", StringComparison.Ordinal) &&
                 !(cameraSlot == 4 && vertex.Contains(vpCamera + ".data[17].y", StringComparison.Ordinal) && vertex.Contains(vpCamera + ".data[15].z", StringComparison.Ordinal))) ||
                !fragment.Contains(fpCamera + ".data[16].x", StringComparison.Ordinal) ||
                !fragment.Contains(fpCamera + ".data[14].x", StringComparison.Ordinal)) return fragment;
            var definitions = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(vertex, @"\b(temp_\d+) = ([^;]+);")) definitions[m.Groups[1].Value] = m.Groups[2].Value;
            bool HorizontalRay(string expression, HashSet<string> visited)
            {
                if (expression.Contains(vpCamera + ".data[17].x", StringComparison.Ordinal)) return true;
                foreach (Match t in Regex.Matches(expression, @"\btemp_\d+\b"))
                    if (visited.Count < 128 && visited.Add(t.Value) && definitions.TryGetValue(t.Value, out string next) && HorizontalRay(next, visited)) return true;
                return false;
            }
            // Some TOTK shadow/AO passes use aspect * inverse-P11 instead of
            // inverse-P00. Require an X input as well: constant ray derivatives
            // must not receive the camera translation/convergence term.
            int RayDependencies(string expression, HashSet<string> visited)
            {
                int flags = expression.Contains(vpCamera + ".data[17].y", StringComparison.Ordinal) ? 1 : 0;
                if (expression.Contains(vpCamera + ".data[15].z", StringComparison.Ordinal)) flags |= 2;
                if (expression.Contains("in_attr0.x", StringComparison.Ordinal) || expression.Contains("in_attr1.x", StringComparison.Ordinal)) flags |= 4;
                foreach (Match t in Regex.Matches(expression, @"\btemp_\d+\b"))
                    if (visited.Count < 128 && visited.Add(t.Value) && definitions.TryGetValue(t.Value, out string next)) flags |= RayDependencies(next, visited);
                return flags;
            }
            var attributes = new HashSet<string>();
            foreach (Match m in Regex.Matches(vertex, @"\b(out_attr\d+\.[xyzw]) = ([^;]+);"))
                if (HorizontalRay(m.Groups[2].Value, new()) || (cameraSlot == 4 && RayDependencies(m.Groups[2].Value, new()) == 7))
                    attributes.Add(m.Groups[1].Value.Replace("out_attr", "in_attr"));
            if (attributes.Count == 0) return fragment;
            var rays = new Dictionary<string, int>();
            var depths = new HashSet<string>();
            var perspectiveCoefficients = new HashSet<string>();
            var nearCoefficients = new HashSet<string>();
            var perspectiveDenominators = new HashSet<string>();
            var perspectiveReciprocals = new HashSet<string>();
            int count = 0;
            fragment = Regex.Replace(fragment, @"\b(temp_\d+) = ([^;]+);", m =>
            {
                string name = m.Groups[1].Value, expression = m.Groups[2].Value;
                Match neg = Regex.Match(expression, @"^0\.0 - (temp_\d+)$");
                if (attributes.Contains(expression)) rays[name] = 1;
                else if (rays.TryGetValue(expression, out int alias)) rays[name] = alias;
                else if (neg.Success && rays.TryGetValue(neg.Groups[1].Value, out int sign)) rays[name] = -sign;
                if (expression.Contains(fpCamera + ".data[16].x", StringComparison.Ordinal) && expression.Contains(fpCamera + ".data[14].x", StringComparison.Ordinal)) depths.Add(name);
                else if (depths.Contains(expression) || (neg.Success && depths.Contains(neg.Groups[1].Value))) depths.Add(name);
                else
                {
                    Match reciprocal = Regex.Match(expression, @"^1\.0 / (temp_\d+)$");
                    if (reciprocal.Success && depths.Contains(reciprocal.Groups[1].Value)) depths.Add(name);
                }
                // Inventory lighting reconstructs negative view Z from hardware depth,
                // while outdoor lighting starts from an already linear depth texture.
                if (expression == "0.0 - " + fpCamera + ".data[14].w") perspectiveCoefficients.Add(name);
                if (expression == "0.0 - " + fpCamera + ".data[14].x") nearCoefficients.Add(name);
                Match perspective = Regex.Match(expression, @"^fma\(temp_\d+, (temp_\d+), 1\.0\)$");
                if (perspective.Success && perspectiveCoefficients.Contains(perspective.Groups[1].Value)) perspectiveDenominators.Add(name);
                Match inverse = Regex.Match(expression, @"^1\.0 / (temp_\d+)$");
                if (inverse.Success && perspectiveDenominators.Contains(inverse.Groups[1].Value)) perspectiveReciprocals.Add(name);
                Match product = Regex.Match(expression, @"^(temp_\d+) \* (temp_\d+)$");
                if (product.Success && ((perspectiveReciprocals.Contains(product.Groups[1].Value) && nearCoefficients.Contains(product.Groups[2].Value)) ||
                    (perspectiveReciprocals.Contains(product.Groups[2].Value) && nearCoefficients.Contains(product.Groups[1].Value)))) depths.Add(name);
                if (!product.Success) product = Regex.Match(expression, @"^fma\((temp_\d+), (temp_\d+), temp_\d+\)$");
                if (!product.Success) return m.Value;
                string a = product.Groups[1].Value, b = product.Groups[2].Value;
                if (!rays.ContainsKey(a)) (a, b) = (b, a);
                if (!rays.TryGetValue(a, out int raySign) || !depths.Contains(b)) return m.Value;
                count++;
                // The unmodified ray is -NDC.x / P00. Undo the eye's clip-X shear
                // before world/light-space reconstruction. Offsets cancel for ray differences.
                // Negating a reconstructed ray negates its complete eye correction,
                // including convergence. Linear outdoor depth is positive; inventory view Z is negative.
                // Remove convergence with the depth sign in both conventions. Preserve BOTW.
                string correction = cameraSlot == 4
                    ? $"{raySign}.0 * ({b} - sign({b}) * ryu_stereo_reconstruction.y)"
                    : $"({raySign}.0 * {b} + ryu_stereo_reconstruction.y)";
                return m.Value + $" {name} += ryu_stereo_reconstruction.x * {correction} * {fpCamera}.data[17].x;";
            });
            // This TOTK outdoor pass uses the reconstructed center first in light
            // space, then against unshifted camera rays for screen-space occlusion.
            // Restore the screen-space center at that boundary; retain the corrected
            // position for shadow-map sampling and the corrected surface normals.
            if (cameraSlot == 4 && fragment.Contains("atomicAdd(fp_s0.data[11]", StringComparison.Ordinal) &&
                fragment.Contains("sampler2DArrayShadow", StringComparison.Ordinal) &&
                fragment.Contains("} fp_c6;", StringComparison.Ordinal))
            {
                // Match data flow instead of compiler-generated temporary numbers.
                // Shader translation variants rename those temporaries.
                Match center = Regex.Match(fragment, @"\b(?<center>temp_\d+) = temp_\d+ \* temp_\d+; \k<center> \+= (?<offset>ryu_stereo_reconstruction\.x[^;]+);");
                if (center.Success)
                {
                    string name = center.Groups["center"].Value;
                    string square = $@"\btemp_\d+ = {name} \* {name};";
                    int histogramEnd = fragment.LastIndexOf("atomicAdd(fp_s0.data[11]", StringComparison.Ordinal);
                    Match boundary = new Regex(square).Match(fragment, histogramEnd);
                    if (boundary.Success && boundary.Index > center.Index)
                    {
                        fragment = fragment.Insert(boundary.Index, $"{name} -= {center.Groups["offset"].Value}; ");
                        Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo: TOTK occlusion center restored before screen-space sampling ({name})");
                    }
                }
            }
            if (count == 0) return fragment;
            int main = fragment.IndexOf("void main()", StringComparison.Ordinal);
            if (main < 0) return fragment;
            Logger.Notice.Print(LogClass.Gpu, $"GeometryStereo: Zelda camera reconstruction corrected in {count} expressions");
            return fragment.Insert(main, "uniform vec2 ryu_stereo_reconstruction;\n");
        }
    }
}
