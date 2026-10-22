using OpenTK.Graphics.OpenGL;
using System;
using System.Text.Json;
namespace Ryujinx.Graphics.OpenGL;
internal static class StereoOutput
{
    internal static string Mode = "panel";
    internal static bool SwapEyes;
    internal static bool Legacy => Mode is "panel" or "half_sbs";
    internal static bool Direct => Legacy && !SwapEyes;
    internal static void Load(JsonElement profile)
    {
        string mode = profile.TryGetProperty("output_format", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        Mode = mode is "panel" or "half_sbs" or "full_sbs" or "half_over_under" or "full_over_under" or "interlaced"
            ? mode : profile.TryGetProperty("direct_panel_output", out var panel) && panel.ValueKind == JsonValueKind.True ? "panel" : "half_sbs";
        SwapEyes = profile.TryGetProperty("swap_eyes", out var swap) && swap.ValueKind == JsonValueKind.True;
    }
    internal static int EyeWidth(int width) => Mode == "full_sbs" ? Math.Max(1, width / 2) : width;
    internal static int EyeHeight(int height) => Mode == "full_over_under" ? Math.Max(1, height / 2) : height;
    internal static void Compose(int[] frames, int sourceWidth, int sourceHeight, int width, int height, int inset = 0, bool recording = false, int[] textures = null)
    {
        string mode = recording ? "half_sbs" : Mode;
        if (mode == "interlaced") { Interlace(textures, width, height, inset); return; }
        for (int slot = 0; slot < 2; slot++)
        {
            int eye = SwapEyes ? 1 - slot : slot;
            FramebufferBinding.Bind(FramebufferTarget.ReadFramebuffer, frames[eye]);
            FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, 0);
            if (mode is "half_over_under" or "full_over_under")
            {
                int y0 = slot == 0 ? height / 2 : 0;
                int y1 = slot == 0 ? height : height / 2;
                GL.BlitFramebuffer(inset, 0, sourceWidth - inset, sourceHeight, 0, y0, width, y1,
                    ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
            }
            else
                GL.BlitFramebuffer(inset, 0, sourceWidth - inset, sourceHeight, slot * width / 2, 0, (slot + 1) * width / 2, height,
                    ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
        }
    }
    private static int _program, _vao;
    private static int Compile(ShaderType type, string code)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, code); GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) { string error = GL.GetShaderInfoLog(shader); GL.DeleteShader(shader); throw new InvalidOperationException(error); }
        return shader;
    }
    private static void Initialize()
    {
        if (_program != 0) return;
        int vertex = Compile(ShaderType.VertexShader, "#version 450 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}");
        int fragment = 0;
        try
        {
            fragment = Compile(ShaderType.FragmentShader, "#version 450 core\nlayout(binding=0) uniform sampler2D leftEye;layout(binding=1) uniform sampler2D rightEye;layout(location=0) uniform int height;layout(location=1) uniform int swapEyes;layout(location=2) uniform int inset;layout(location=0) out vec4 color;void main(){ivec2 p=ivec2(gl_FragCoord.xy);int w=textureSize(leftEye,0).x;p.x=inset+int((float(p.x)+0.5)*float(w-2*inset)/float(w));bool right=(((height-1-p.y)&1)^swapEyes)!=0;color=right?texelFetch(rightEye,p,0):texelFetch(leftEye,p,0);color.a=1.0;}");
            _program = GL.CreateProgram(); GL.AttachShader(_program, vertex); GL.AttachShader(_program, fragment); GL.LinkProgram(_program);
            GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) { string error = GL.GetProgramInfoLog(_program); GL.DeleteProgram(_program); _program = 0; throw new InvalidOperationException(error); }
            GL.CreateVertexArrays(1, out _vao);
        }
        finally { GL.DeleteShader(vertex); if (fragment != 0) GL.DeleteShader(fragment); }
    }
    private static void Interlace(int[] textures, int width, int height, int inset)
    {
        FramebufferBinding.Bind(FramebufferTarget.DrawFramebuffer, 0);
        Initialize();
        int program = GL.GetInteger(GetPName.CurrentProgram), vao = GL.GetInteger(GetPName.VertexArrayBinding);
        int pipeline = GL.GetInteger((GetPName)0x825A), active = GL.GetInteger(GetPName.ActiveTexture);
        int origin = GL.GetInteger((GetPName)0x935C), depth = GL.GetInteger((GetPName)0x935D);
        int[] viewport = new int[4], polygon = new int[2], bound = new int[2], samplers = new int[2];
        int[] mask = new int[4]; GL.GetInteger(GetPName.ColorWritemask, mask);
        GL.GetInteger(GetPName.Viewport, viewport); GL.GetInteger(GetPName.PolygonMode, polygon);
        EnableCap[] caps = { EnableCap.DepthTest, EnableCap.StencilTest, EnableCap.CullFace, EnableCap.ColorLogicOp, EnableCap.SampleAlphaToCoverage, EnableCap.SampleMask, EnableCap.ClipDistance0, EnableCap.ClipDistance1, EnableCap.ClipDistance2, EnableCap.ClipDistance3, EnableCap.ClipDistance4, EnableCap.ClipDistance5, EnableCap.ClipDistance6, EnableCap.ClipDistance7 };
        bool[] enabled = new bool[caps.Length];
        bool blend = GL.IsEnabled(IndexedEnableCap.Blend, 0);
        try
        {
            for (int i = 0; i < 2; i++)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + i); bound[i] = GL.GetInteger(GetPName.TextureBinding2D);
                GL.GetInteger((GetIndexedPName)0x8919, i, out samplers[i]);
                GL.BindTexture(TextureTarget.Texture2D, textures[i]); GL.BindSampler(i, 0);
            }
            for (int i = 0; i < caps.Length; i++) { enabled[i] = GL.IsEnabled(caps[i]); GL.Disable(caps[i]); }
            GL.Disable(IndexedEnableCap.Blend, 0); GL.ColorMask(0, true, true, true, true);
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            GL.ClipControl(ClipOrigin.LowerLeft, ClipDepthMode.NegativeOneToOne);
            GL.Viewport(0, 0, width, height); GL.BindProgramPipeline(0); GL.UseProgram(_program); GL.BindVertexArray(_vao);
            GL.ProgramUniform1(_program, 0, height); GL.ProgramUniform1(_program, 1, SwapEyes ? 1 : 0); GL.ProgramUniform1(_program, 2, inset);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        finally
        {
            GL.UseProgram(program); GL.BindProgramPipeline(pipeline); GL.BindVertexArray(vao);
            GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]); GL.ClipControl((ClipOrigin)origin, (ClipDepthMode)depth);
            GL.PolygonMode(MaterialFace.FrontAndBack, (PolygonMode)polygon[0]);
            GL.ColorMask(0, mask[0] != 0, mask[1] != 0, mask[2] != 0, mask[3] != 0); if (blend) GL.Enable(IndexedEnableCap.Blend, 0);
            for (int i = 0; i < caps.Length; i++) if (enabled[i]) GL.Enable(caps[i]);
            for (int i = 0; i < 2; i++) { GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, bound[i]); GL.BindSampler(i, samplers[i]); }
            GL.ActiveTexture((TextureUnit)active);
        }
    }
    internal static void Dispose()
    {
        if (_program != 0) GL.DeleteProgram(_program);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _program = _vao = 0;
    }}
