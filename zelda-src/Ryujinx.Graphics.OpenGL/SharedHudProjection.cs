using System;
using System.Text.RegularExpressions;
namespace Ryujinx.Graphics.OpenGL;

// Nintendo layout panes and storage-buffer glyphs. Their layout flags distinguish
// them from scene meshes, shadow receivers and fullscreen post-processing.
internal static class SharedHudProjection
{
    internal static bool Pane(string code) =>
        code.Contains("floatBitsToInt(vp_c3.data[34].w)", StringComparison.Ordinal) &&
        code.Contains("vp_c3.data[0].w", StringComparison.Ordinal) &&
        code.Contains("vp_c3.data[3].w", StringComparison.Ordinal) &&
        !code.Contains("in vec4 in_attr1", StringComparison.Ordinal);
    // Font batches index their corner data dynamically; do not depend on a
    // particular constant-buffer row. Skinned meshes have additional attributes.
    internal static bool Glyph(string code) =>
        code.Contains("buffer _vp_s0", StringComparison.Ordinal) &&
        code.Contains("vp_s0.data[", StringComparison.Ordinal) &&
        code.Contains("vp_c1.data[", StringComparison.Ordinal) &&
        code.Contains("vp_c3.data[0].w", StringComparison.Ordinal) &&
        code.Contains("vp_c3.data[3].w", StringComparison.Ordinal) &&
        code.Contains("in vec4 in_attr0", StringComparison.Ordinal) &&
        !Regex.IsMatch(code, @"\bin (?:[ui]?vec[234]|float|int|uint) in_attr[1-9]\d*\b");
    internal static bool Matches(string code) => Pane(code) || Glyph(code);
}
