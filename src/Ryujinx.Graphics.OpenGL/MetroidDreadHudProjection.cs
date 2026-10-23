using System;
namespace Ryujinx.Graphics.OpenGL;

internal static class MetroidDreadHudProjection
{
    // Shared Mercury 2D sprite/text shader. Its identity matrix is also used
    // for a fullscreen scene resolve: that draw must keep its original depth.
    internal static bool Matches(string code) =>
        string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "010093801237c000", StringComparison.OrdinalIgnoreCase) &&
        code.Contains("fma(temp_0, vp_c3.data[4].z, vp_c3.data[4].x)", StringComparison.Ordinal) &&
        code.Contains("fma(temp_1, vp_c3.data[4].w, vp_c3.data[4].y)", StringComparison.Ordinal) &&
        code.Contains("out_attr2.w =", StringComparison.Ordinal) &&
        code.Contains("fma(0.0, vp_c3.data[3].z", StringComparison.Ordinal) &&
        code.Contains("gl_Position.w =", StringComparison.Ordinal) &&
        !code.Contains("vp_c4", StringComparison.Ordinal);

    internal const string Correction =
        "bool ryu_dread_fullscreen = all(lessThan(abs(vp_c3.data[0] - vec4(1,0,0,0)), vec4(0.00001))) && " +
        "all(lessThan(abs(vp_c3.data[1] - vec4(0,1,0,0)), vec4(0.00001))) && " +
        "all(lessThan(abs(vp_c3.data[3] - vec4(0,0,0,1)), vec4(0.00001)));\n" +
        "if (!ryu_dread_fullscreen) gl_Position.x += ryu_kart_hud_offset * gl_Position.w;\n";
}
