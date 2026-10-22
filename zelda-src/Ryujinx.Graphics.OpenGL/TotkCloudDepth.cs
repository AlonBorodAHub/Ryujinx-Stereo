using System;
using System.Text.RegularExpressions;
namespace Ryujinx.Graphics.OpenGL;
internal static class TotkCloudDepth
{
    internal static bool Matches(string vertex) =>
        string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase) &&
        vertex.Contains("texelFetch(vp_t_tcb_C,") && vertex.Contains("vp_t_tcb_2C") && vertex.Contains("vp_t_tcb_30") &&
        vertex.Contains("vp_c8.data[4].w") && vertex.Contains("vp_c8.data[7].w");
    internal static string ProjectedCorrection(string vertex)
    {
        if (!Matches(vertex)) return "";
        // Analyze the same projected depth UV used by BOTW. Zero-weighted
        // matrix terms otherwise force dependency traversal through the entire
        // particle simulation and exceed its conservative recursion limit.
        // This simplifies only the analysis input, never the guest shader.
        string analysis = Regex.Replace(vertex, @"fma\(0\.0, [^,()]+, ([^,()]+)\)", "$1");
        analysis = Regex.Replace(analysis, @"0\.0 \* temp_\d+", "0.0");
        return StereoProjectedVaryings.Correction(analysis);
    }
    internal static string Rewrite(string fragment, string vertex)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(vertex) || !vertex.Contains("vp_c8.data[4].w") || !vertex.Contains("vp_c8.data[7].w") ||
            !fragment.Contains("in_attr3.w") || !fragment.Contains("textureQueryLod(fp_t_tcb_A,") ||
            !fragment.Contains("textureQueryLod(fp_t_tcb_8,")) return fragment;
        // This particle pass samples scene depth using projected clip XY/W.
        // Some generated vertex variants have early culling exits, which defeat
        // the conservative single-position-write varying analysis. Raster X is
        // already shifted for the current eye. Preserve the guest's Y mapping.
        var sample = new Regex(@"texture\(fp_t_tcb_10,\s*vec2\((temp_\d+),\s*(temp_\d+)\)\)");
        if (!sample.IsMatch(fragment)) return fragment;
        fragment = sample.Replace(fragment, m => "texture(fp_t_tcb_10, vec2(gl_FragCoord.x * ryu_stereo_depth_viewport.x + ryu_stereo_depth_viewport.z, " + m.Groups[2].Value + "))");
        int main = fragment.IndexOf("void main()", StringComparison.Ordinal);
        if (main < 0) return fragment;
        Ryujinx.Common.Logging.Logger.Notice.Print(Ryujinx.Common.Logging.LogClass.Gpu, "GeometryStereo: TOTK particle scene-depth X follows raster position");
        return fragment.Insert(main, "uniform vec4 ryu_stereo_depth_viewport;\n");
    }
}