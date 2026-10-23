using System;
namespace Ryujinx.Graphics.OpenGL;

// Captured Nintendo layout pane and storage-buffer text families. Keep this
// title-gated: screen-space resolves and scene cameras must retain their depth.
internal static class LinksAwakeningHudProjection
{
    internal static bool Matches(string s) =>
        string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "01006bb00c6f0000", StringComparison.OrdinalIgnoreCase) &&
        s.Contains("vp_c3.data[0].w", StringComparison.Ordinal) &&
        s.Contains("vp_c3.data[3].w", StringComparison.Ordinal) &&
        (MarioKartHudProjection.Pane(s) ||
         (s.Contains("buffer _vp_s0", StringComparison.Ordinal) && s.Contains("vp_s0.data[", StringComparison.Ordinal) &&
          !s.Contains("in vec4 in_attr1", StringComparison.Ordinal)));
}
