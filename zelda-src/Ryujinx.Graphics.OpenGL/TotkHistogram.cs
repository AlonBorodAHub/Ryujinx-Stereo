using System;
using System.Text.RegularExpressions;
namespace Ryujinx.Graphics.OpenGL;
internal static class TotkHistogram
{
    internal static string Rewrite(string source)
    {
        if (Environment.GetEnvironmentVariable("RYU_TOTK_HISTOGRAM_TEST") != "1" ||
            !string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase)) return source;
        // These RED results are unused; the statistics buffer is shared across eyes.
        // Keep both image outputs, but accumulate the global histogram once.
        var atomics = new Regex(@"atomicAdd\(fp_s0\.data\[(?:[5-9]|10|11)\], [^;\r\n]+\)");
        if (!atomics.IsMatch(source)) return source;
        source = source.Replace("void main()", "uniform int ryu_totk_histogram_eye;\nvoid main()");
        return atomics.Replace(source, m => "(ryu_totk_histogram_eye == 0 ? " + m.Value + " : 0u)");
    }
}
