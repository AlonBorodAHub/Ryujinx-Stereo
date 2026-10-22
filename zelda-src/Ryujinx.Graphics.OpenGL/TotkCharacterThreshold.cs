using System;
namespace Ryujinx.Graphics.OpenGL;
internal static class TotkCharacterThreshold
{
    internal static string Rewrite(string source)
    {
        if (Environment.GetEnvironmentVariable("RYU_TOTK_THRESHOLD_WORKAROUND") != "1" ||
            !string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase)) return source;
        const string read = "temp_6 = uintBitsToFloat(fp_s0.data[56]);";
        if (!source.Contains(read) || !source.Contains("temp_10 = temp_6 < temp_9;") ||
            !source.Contains("out_attr0.z = intBitsToFloat(temp_11);") || !source.Contains("uniform sampler2DArray fp_t_tcb_8;")) return source;
        // Explicit diagnostic workaround: stabilise only the character-lighting
        // classification at the observed good-frame threshold. Not a shadow fix.
        return source.Replace("void main()", "uniform int ryu_totk_shadow_probe;\nvoid main()")
            .Replace(read, read + "\n    if (ryu_totk_shadow_probe == 9) temp_6 = max(temp_6, 3.0);");
    }
}
