using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.OpenGL
{
    internal static class StereoProjectedVaryings
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
        // Differentiate clip-derived outputs with respect to clip X. Ordinary
        // material UVs and world positions do not depend on the final clip-X SSA node.
        internal static string Correction(string source) => Cache.GetOrAdd(source, Analyze);
        private static string Analyze(string source)
        {
            var xs = Regex.Matches(source, @"gl_Position\.x = (temp_\d+);");
            var ws = Regex.Matches(source, @"gl_Position\.w = (temp_\d+);");
            if (xs.Count != 1 || ws.Count != 1) return "";
            string clipX = xs[0].Groups[1].Value, clipW = ws[0].Groups[1].Value;
            var defs = new Dictionary<string, string>();
            var repeated = new HashSet<string>();
            foreach (Match m in Regex.Matches(source, @"\b(temp_\d+) = ([^;]+);"))
            {
                string name = m.Groups[1].Value;
                if (!defs.TryAdd(name, m.Groups[2].Value)) repeated.Add(name);
            }
            var cache = new Dictionary<string, string>();
            var dependencies = new Dictionary<string, bool>();
            bool Depends(string expression, int depth = 0)
            {
                if (expression == clipX) return true;
                if (expression == clipW) return false;
                if (depth > 32) return true;
                if (dependencies.TryGetValue(expression, out bool known)) return known;
                foreach (Match t in Regex.Matches(expression, @"\btemp_\d+\b"))
                {
                    if (t.Value == clipX) return true;
                    if (t.Value != clipW && defs.TryGetValue(t.Value, out string next) && Depends(next, depth + 1)) return true;
                }
                dependencies[expression] = false;
                return false;
            }
            string Expand(string expr, int depth)
            {
                if (depth > 24) return null;
                if (expr == clipW) return "gl_Position.w";
                if (!Regex.IsMatch(expr, @"\btemp_\d+\b")) return expr;
                bool failed = false;
                string result = Regex.Replace(expr, @"\btemp_\d+\b", m =>
                {
                    if (m.Value == clipW) return "gl_Position.w";
                    if (repeated.Contains(m.Value) || !defs.TryGetValue(m.Value, out string next)) { failed = true; return ""; }
                    string value = Expand(next, depth + 1);
                    if (value == null) { failed = true; return ""; }
                    return "(" + value + ")";
                });
                return failed || result.Length > 400 ? null : result;
            }
            static bool Zero(string x) => x == "0.0";
            static string Sum(string a, string b)
            {
                if (a == null || b == null) return null;
                if (Zero(a)) return b;
                if (Zero(b)) return a;
                return $"({a} + {b})";
            }
            string Product(string derivative, string value, int depth)
            {
                if (derivative == null) return null;
                if (Zero(derivative)) return "0.0";
                if (Depends(value)) return null;
                string expanded = Expand(value, depth + 1);
                if (expanded == null) return null;
                if (expanded == "0.0" || expanded == "0") return "0.0";
                return derivative == "1.0" ? expanded : $"({derivative} * ({expanded}))";
            }
            string D(string expr, int depth = 0)
            {
                if (depth > 32) return null;
                if (expr == clipX) return "1.0";
                if (expr == clipW) return "0.0";
                if (!Depends(expr)) return "0.0";
                if (cache.TryGetValue(expr, out string known)) return known;
                if (Regex.IsMatch(expr, @"^temp_\d+$"))
                {
                    if (repeated.Contains(expr) || !defs.TryGetValue(expr, out string next)) return null;
                    string answer = D(next, depth + 1);
                    cache[expr] = answer;
                    return answer;
                }
                if (!Regex.IsMatch(expr, @"\btemp_\d+\b")) return "0.0";
                Match fma = Regex.Match(expr, @"^fma\(([^,()]+), ([^,()]+), ([^,()]+)\)$");
                if (fma.Success)
                {
                    string a = fma.Groups[1].Value, b = fma.Groups[2].Value, c = fma.Groups[3].Value;
                    return Sum(Sum(Product(D(a, depth + 1), b, depth), Product(D(b, depth + 1), a, depth)), D(c, depth + 1));
                }
                Match op = Regex.Match(expr, @"^([^ ()]+) ([+*/-]) ([^ ()]+)$");
                if (!op.Success) return null;
                string lhs = op.Groups[1].Value, rhs = op.Groups[3].Value;
                string da = D(lhs, depth + 1), db = D(rhs, depth + 1);
                switch (op.Groups[2].Value)
                {
                    case "+": return Sum(da, db);
                    case "-": return Sum(da, Product(db, "-1.0", depth));
                    case "*": return Sum(Product(da, rhs, depth), Product(db, lhs, depth));
                    case "/":
                        if (da == null || db == null || !Zero(db)) return null;
                        if (Zero(da)) return "0.0";
                        string denominator = Expand(rhs, depth + 1);
                        return denominator == null ? null : $"({da} / ({denominator}))";
                    default: return null;
                }
            }
            var outputs = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(source, @"\b(out_attr\d+\.[xyzw]) = ([^;]+);"))
            {
                string coefficient = D(m.Groups[2].Value);
                if (coefficient != null && !Zero(coefficient) && coefficient.Length <= 400)
                    outputs[m.Groups[1].Value] = coefficient;
            }
            if (outputs.Count == 0) return "";
            return "if (abs(gl_Position.w - 1.0) > 0.0001) { float ryu_varying_dx = ryu_stereo_projected_varying.x * (gl_Position.w - ryu_stereo_projected_varying.y);\n" +
                string.Join("\n", outputs.Select(o => $"{o.Key} += ryu_varying_dx * ({o.Value});")) + "\n}\n";
        }
    }
}
