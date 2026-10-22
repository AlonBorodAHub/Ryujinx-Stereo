using System;
namespace Ryujinx.Graphics.OpenGL;
internal static class TotkCharacterLighting
{
    internal static string Rewrite(string source)
    {
        if (Environment.GetEnvironmentVariable("RYU_TOTK_CHARACTER_LIGHTING_TEST") != "1" ||
            !string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase)) return source;
        // Isolate the HDR-luminance threshold that is later used by character
        // lighting. This is a reversible diagnostic, not a shadow-map rewrite.
        if (!source.Contains("temp_6 = uintBitsToFloat(fp_s0.data[56]);") ||
            !source.Contains("temp_10 = temp_6 < temp_9;") ||
            !source.Contains("out_attr0.z = intBitsToFloat(temp_11);") ||
            !source.Contains("uniform sampler2DArray fp_t_tcb_8;")) return source;
        return source.Replace("void main()", "uniform int ryu_totk_shadow_probe;\nvoid main()")
            .Replace("out_attr0.z = intBitsToFloat(temp_11);",
                "out_attr0.z = ryu_totk_shadow_probe == 6 ? 1.0 : intBitsToFloat(temp_11);");
    }
}
