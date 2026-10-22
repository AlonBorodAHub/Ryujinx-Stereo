using System;
namespace Ryujinx.Graphics.OpenGL;
internal static class TotkHudProjection
{
    internal static bool Matches(string s) => string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase) && s.Contains("vp_c3.data[0].w") && s.Contains("vp_c3.data[3].w") && (s.Contains("vp_c3.data[34].w") || s.Contains("vp_s0.data["));
    internal static bool IsPlainColorOverlay(string fragment) =>
        !string.IsNullOrEmpty(fragment) && !fragment.Contains("uniform sampler", StringComparison.Ordinal) &&
        fragment.Contains("in_attr0.x", StringComparison.Ordinal) && fragment.Contains("in_attr0.w", StringComparison.Ordinal) &&
        fragment.Contains("fp_c3.data[0].x", StringComparison.Ordinal) && fragment.Contains("fp_c3.data[0].w", StringComparison.Ordinal) &&
        !fragment.Contains("in_attr1", StringComparison.Ordinal);

    // Edge-touching solid UI panels must continue covering the viewport after
    // the stereo HUD shift. Interior geometry retains its requested pop-out.
    internal const string SaveOverlayBounds = "vec4 ryu_overlay_bounds = gl_Position;\n";
    internal const string CoverOverlayEdges = "if (ryu_overlay_bounds.w > 0.0) { if (ryu_overlay_bounds.x <= -0.9999 * ryu_overlay_bounds.w) gl_Position.x = min(gl_Position.x, ryu_overlay_bounds.x); if (ryu_overlay_bounds.x >= 0.9999 * ryu_overlay_bounds.w) gl_Position.x = max(gl_Position.x, ryu_overlay_bounds.x); }\n";
}
