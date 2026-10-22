using System;
using Ryujinx.Graphics.GAL;
namespace Ryujinx.Graphics.OpenGL;
// TOTK's soft-particle depth pyramid includes 200x112 and 100x56 RG16F
// targets. They are scene-depth inputs even though they are smaller than 256.
// Callers additionally require per-eye storage and a screen-shaped 2D view.
internal static class TotkSmallDepth
{
    private static readonly bool IsTotk = string.Equals(Environment.GetEnvironmentVariable("RJ_STEREO_GAME"), "0100f2c0115b6000", StringComparison.OrdinalIgnoreCase);
    internal static bool IsDepth(Format format, int width, int height) =>
        IsTotk &&
        width >= 64 && width < 256 && height > 0 &&
        (format == Format.R16G16Float || format == Format.R32Float || format.IsDepthOrStencil());
}
