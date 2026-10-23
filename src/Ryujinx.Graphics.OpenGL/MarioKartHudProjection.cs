using System;
namespace Ryujinx.Graphics.OpenGL;

// MK8 uses separate Nintendo pane shaders and storage-buffer glyph shaders.
// Neither signature matches scene, fullscreen resolve, or shadow shaders.
internal static class MarioKartHudProjection
{
    internal static bool Matches(string code) => MetroidDreadHudProjection.Matches(code) || LinksAwakeningHudProjection.Matches(code) || MatchesMarioKart(code) || SharedHudProjection.Matches(code);
    private static bool MatchesMarioKart(string code) =>
        string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100152000022000", StringComparison.OrdinalIgnoreCase) &&
        (Pane(code) || Glyph(code));
    internal static bool Pane(string code) =>
        code.Contains("floatBitsToInt(vp_c3.data[34].w)", StringComparison.Ordinal) &&
        code.Contains("fma(0.0, vp_c3.data[6].z", StringComparison.Ordinal) &&
        code.Contains("vp_c3.data[3].w", StringComparison.Ordinal) &&
        !code.Contains("in vec4 in_attr1", StringComparison.Ordinal);
    private static bool Glyph(string code) =>
        code.Contains("buffer _vp_s0", StringComparison.Ordinal) &&
        code.Contains("vp_s0.data[", StringComparison.Ordinal) &&
        code.Contains("vp_c1.data[12].x", StringComparison.Ordinal) &&
        code.Contains("fma(0.0, vp_c3.data[3].z", StringComparison.Ordinal) &&
        !code.Contains("in vec4 in_attr1", StringComparison.Ordinal);
}
