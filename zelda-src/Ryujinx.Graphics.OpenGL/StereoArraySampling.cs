using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.OpenGL
{
    // Keep the original texture operations and constant offsets; only add an array-layer coordinate.
    internal static class StereoArraySampling
    {
        private static readonly Regex Sampler = new(@"\buniform\s+sampler2D\s+((?:vp|fp|gp|tcp|tep|cp)_t_\w+)");
        private static readonly Regex Call = new(@"\b(\w+)\s*\(");
        private static readonly HashSet<string> Supported = new()
        {
            "texture", "textureLod", "textureOffset", "textureLodOffset", "textureGrad", "textureGradOffset",
            "textureGather", "textureGatherOffset", "textureGatherOffsets", "texelFetch", "texelFetchOffset",
            "textureSize", "textureQueryLod", "textureQueryLevels"
        };

        private static List<string> Arguments(string code, int open, out int end)
        {
            var args = new List<string>();
            int depth = 0, bracket = 0, start = open + 1;
            for (int i = start; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    if (depth == 0) { args.Add(code.Substring(start, i - start).Trim()); end = i + 1; return args; }
                    depth--;
                }
                else if (c == '[') bracket++;
                else if (c == ']') bracket--;
                else if (c == ',' && depth == 0 && bracket == 0)
                {
                    args.Add(code.Substring(start, i - start).Trim()); start = i + 1;
                }
            }
            end = open;
            return args;
        }

        internal static string Rewrite(string code, ShaderStage stage)
        {
            if (!NativeStereo.ArraySampling || !NativeStereo.Enabled) return code;
            var names = Sampler.Matches(code).Select(m => m.Groups[1].Value).ToHashSet();
            Match[] calls = Call.Matches(code).ToArray();
            foreach (Match call in calls)
            {
                var args = Arguments(code, call.Index + call.Length - 1, out _);
                string name = args.Count == 0 ? "" : Regex.Match(args[0], @"^\w+").Value;
                if (names.Contains(name) && !Supported.Contains(call.Groups[1].Value)) names.Remove(name);
            }
            bool reconstruction = stage == ShaderStage.Fragment &&
                (code.Contains("ryu_stereo_reconstruction.x", StringComparison.Ordinal) || code.Contains("ryu_stereo_deferred.x", StringComparison.Ordinal));
            if (names.Count == 0 && !reconstruction) return code;

            // Work right-to-left, reparsing argument ends after nested call edits.
            for (int i = calls.Length - 1; i >= 0; i--)
            {
                Match call = calls[i];
                string function = call.Groups[1].Value;
                var args = Arguments(code, call.Index + call.Length - 1, out int end);
                string name = args.Count == 0 ? "" : Regex.Match(args[0], @"^\w+").Value;
                if (!names.Contains(name) || !Supported.Contains(function)) continue;
                if (function == "textureQueryLod" || function == "textureQueryLevels") continue;
                if (function != "textureSize")
                {
                    if (args.Count < 2) continue;
                    bool integer = function.StartsWith("texelFetch", StringComparison.Ordinal);
                    // Filtered array sampling clamps the layer, but texelFetch does not.
                    // Shared one-layer vertex data must remain on layer zero in both eyes.
                    string eye = integer ? $"min(ryu_array_eye(), textureSize({args[0]}, 0).z - 1)" : "ryu_array_eye()";
                    args[1] = $"{(integer ? "ivec3" : "vec3")}({args[1]}, {eye})";
                }
                string replacement = function + "(" + string.Join(", ", args) + ")";
                if (function == "textureSize") replacement = "(" + replacement + ".xy)";
                code = code.Substring(0, call.Index) + replacement + code.Substring(end);
            }
            code = Sampler.Replace(code, m => names.Contains(m.Groups[1].Value) ? m.Value.Replace("sampler2D", "sampler2DArray") : m.Value);

            string pairEye = stage == ShaderStage.Vertex ? "(gl_InstanceID & 1)" : stage == ShaderStage.Fragment ? "gl_Layer" : "0";
            string helper = "uniform int ryu_stereo_sampling_eye;\nint ryu_array_eye() { return ryu_stereo_sampling_eye == -2 ? " + pairEye + " : max(ryu_stereo_sampling_eye, 0); }\n";
            if (reconstruction)
            {
                helper += "float ryu_array_eye_sign() { return ryu_stereo_sampling_eye == -2 && gl_Layer != 0 ? -1.0 : 1.0; }\n";
                code = code.Replace("ryu_stereo_reconstruction.x", "(ryu_stereo_reconstruction.x * ryu_array_eye_sign())")
                    .Replace("ryu_stereo_deferred.x", "(ryu_stereo_deferred.x * ryu_array_eye_sign())");
            }
            int declaration = 0;
            foreach (string line in code.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#", StringComparison.Ordinal)) break;
                declaration += line.Length + 1;
            }
            return code.Insert(Math.Min(declaration, code.Length), helper);
        }
    }
}
