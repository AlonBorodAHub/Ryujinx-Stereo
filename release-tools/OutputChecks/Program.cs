using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using Ryujinx.Graphics.OpenGL;
namespace Ryujinx.Graphics.OpenGL {
 internal static class FramebufferBinding { internal static void Bind(FramebufferTarget target,int handle) => GL.BindFramebuffer(target,handle); }
}
class Bindings : IBindingsContext { public nint GetProcAddress(string name) => Checks.SDL_GL_GetProcAddress(name); }
class Checks {
 [DllImport("SDL2")] static extern int SDL_Init(uint flags);
 [DllImport("SDL2")] static extern int SDL_GL_SetAttribute(int attribute,int value);
 [DllImport("SDL2")] static extern nint SDL_CreateWindow(string title,int x,int y,int w,int h,uint flags);
 [DllImport("SDL2")] static extern nint SDL_GL_CreateContext(nint window);
 [DllImport("SDL2")] internal static extern nint SDL_GL_GetProcAddress(string name);
 [DllImport("SDL2")] static extern void SDL_GL_DeleteContext(nint context);
 [DllImport("SDL2")] static extern void SDL_DestroyWindow(nint window);
 [DllImport("SDL2")] static extern void SDL_Quit();
 static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
 static void Load(string json) { using var doc=JsonDocument.Parse(json); StereoOutput.Load(doc.RootElement); }
 static void Main(string[] args) {
  Check(SDL_Init(32)==0,"SDL init"); SDL_GL_SetAttribute(17,4); SDL_GL_SetAttribute(18,5); SDL_GL_SetAttribute(21,1);
  nint window=SDL_CreateWindow("Stereo output checks",0,0,64,48,2|8), context=SDL_GL_CreateContext(window);
  Check(context!=0,"OpenGL 4.5 context"); GL.LoadBindings(new Bindings());
  try {
   Load("{\"direct_panel_output\":true}"); Check(StereoOutput.Mode=="panel","legacy panel profile");
   Load("{\"direct_panel_output\":false}"); Check(StereoOutput.Mode=="half_sbs","legacy SBS profile");
   Load("{\"output_format\":\"frame_packing\"}"); Check(StereoOutput.Mode=="half_sbs","unsupported mode fallback");
   foreach(string mode in new[]{"panel","half_sbs","full_sbs","half_over_under","full_over_under","interlaced"})
   foreach(bool swap in new[]{false,true}) foreach(bool record in new[]{false,true}) foreach(var size in new[]{(32,16),(17,9)}) {
    Load("{\"output_format\":\""+mode+"\",\"swap_eyes\":"+(swap?"true":"false")+"}");
    int width=size.Item1,height=size.Item2,sw=StereoOutput.EyeWidth(width),sh=StereoOutput.EyeHeight(height);
    Check(sw==(mode=="full_sbs"?width/2:width),"full SBS eye dimensions");
    Check(sh==(mode=="full_over_under"?height/2:height),"full top/bottom eye dimensions");
    int[] textures=new int[2],frames=new int[2];
    for(int eye=0;eye<2;eye++) {
     GL.CreateTextures(TextureTarget.Texture2D,1,out textures[eye]); GL.TextureStorage2D(textures[eye],1,SizedInternalFormat.Rgba8,sw,sh);
     byte[] pixels=new byte[sw*sh*4]; for(int y=0;y<sh;y++) for(int x=0;x<sw;x++) { int i=(y*sw+x)*4; pixels[i]=eye==0?(byte)240:(byte)0; pixels[i+1]=(byte)(y*200/Math.Max(1,sh-1)); pixels[i+2]=eye==1?(byte)240:(byte)0; pixels[i+3]=255; }
     GL.TextureSubImage2D(textures[eye],0,0,0,sw,sh,PixelFormat.Rgba,PixelType.UnsignedByte,pixels);
     GL.CreateFramebuffers(1,out frames[eye]); GL.NamedFramebufferTexture(frames[eye],FramebufferAttachment.ColorAttachment0,textures[eye],0);
    }
    GL.BindFramebuffer(FramebufferTarget.Framebuffer,0); GL.Disable(EnableCap.ScissorTest); GL.Disable(EnableCap.FramebufferSrgb);
    GL.ColorMask(true,true,true,true); GL.ClearColor(0,1,0,1); GL.Clear(ClearBufferMask.ColorBufferBit);
    GL.Enable(IndexedEnableCap.Blend,0); GL.Enable(EnableCap.DepthTest); GL.Enable(EnableCap.CullFace); GL.Enable(EnableCap.ClipDistance0);
    GL.Viewport(3,4,7,8); GL.ColorMask(0,false,true,false,true);
    StereoOutput.Compose(frames,sw,sh,width,height,recording:record,textures:textures);
    Check(GL.IsEnabled(IndexedEnableCap.Blend,0)&&GL.IsEnabled(EnableCap.DepthTest)&&GL.IsEnabled(EnableCap.CullFace)&&GL.IsEnabled(EnableCap.ClipDistance0),"draw enable state restored");
    int[] mask=new int[4];GL.GetInteger(GetPName.ColorWritemask,mask);Check(mask[0]==0&&mask[1]==1&&mask[2]==0&&mask[3]==1,$"{mode}: color mask restored ({string.Join(",",mask)})");
    int[] vp=new int[4];GL.GetInteger(GetPName.Viewport,vp);Check(vp[0]==3&&vp[1]==4&&vp[2]==7&&vp[3]==8,"viewport restored");
    GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer,0); byte[] result=new byte[width*height*4]; GL.ReadPixels(0,0,width,height,PixelFormat.Rgba,PixelType.UnsignedByte,result);
    for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
     int slot= !record&&mode=="interlaced" ? (height-1-y)&1 : !record&&(mode=="half_over_under"||mode=="full_over_under") ? (y>=height/2?0:1) : (x<width/2?0:1);
     int eye=swap?1-slot:slot, i=(y*width+x)*4;
     Check(result[i+(eye==0?0:2)]==240&&result[i+(eye==0?2:0)]==0,$"{mode}, swap={swap}, record={record}, {width}x{height}, pixel {x},{y}: wrong eye RGBA={result[i]},{result[i+1]},{result[i+2]},{result[i+3]}, GL={GL.GetError()}");
     if(!record&&mode=="interlaced") Check(Math.Abs(result[i+1]-y*200/Math.Max(1,sh-1))<=1,"interlaced row orientation");
    }
    Check(GL.GetError()==ErrorCode.NoError,"GL error");
    foreach(int f in frames)GL.DeleteFramebuffer(f);foreach(int t in textures)GL.DeleteTexture(t);
   }
   if(args.Length>0) Integration(args[0]);
   Console.WriteLine("PASS: 48 GPU cases: six formats, both eye orders, SBS recording, even/odd sizes, eye dimensions, row orientation and graphics state.");
  } finally { StereoOutput.Dispose(); SDL_GL_DeleteContext(context); SDL_DestroyWindow(window); SDL_Quit(); }
 }
 static void Integration(string directory) {
  string stage=System.IO.Path.GetFullPath(directory);
  System.Runtime.Loader.AssemblyLoadContext.Default.Resolving+=(context,name)=>{string path=System.IO.Path.Combine(stage,name.Name+".dll");return System.IO.File.Exists(path)?context.LoadFromAssemblyPath(path):null;};
  Environment.SetEnvironmentVariable("RYU_GEOMETRY_STEREO","1"); Environment.SetEnvironmentVariable("RYU_LEIA_DIRECT","0");
  Environment.SetEnvironmentVariable("RYU_STEREO_SETTINGS",System.IO.Path.Combine(stage,"missing-test-settings.json"));
  var assembly=System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(stage,"Ryujinx.Graphics.OpenGL.dll"));
  var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
  var native=assembly.GetType("Ryujinx.Graphics.OpenGL.NativeStereo"); var output=assembly.GetType("Ryujinx.Graphics.OpenGL.StereoOutput");
  var bind=assembly.GetType("Ryujinx.Graphics.OpenGL.FramebufferBinding").GetMethod("Bind",flags);
  object instance=Activator.CreateInstance(native,true);
  var present=native.GetMethod("Present",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
  try {
   foreach(string mode in new[]{"panel","half_sbs","full_sbs","half_over_under","full_over_under","interlaced"}) foreach(bool swap in new[]{false,true}) {
    output.GetField("Mode",flags).SetValue(null,mode);output.GetField("SwapEyes",flags).SetValue(null,swap);
    int width=32,height=16;
    Action<int> render=fbo=>{int eye=(int)native.GetProperty("CurrentEye",flags).GetValue(null);bind.Invoke(null,new object[]{FramebufferTarget.Framebuffer,fbo});GL.Disable(EnableCap.ScissorTest);GL.ColorMask(true,true,true,true);GL.ClearColor(eye==0?1:0,0,eye==1?1:0,1);GL.Clear(ClearBufferMask.ColorBufferBit);};
    Action read=()=>{
     bind.Invoke(null,new object[]{FramebufferTarget.ReadFramebuffer,0});byte[] pixels=new byte[width*height*4];GL.ReadPixels(0,0,width,height,PixelFormat.Rgba,PixelType.UnsignedByte,pixels);
     for(int y=0;y<height;y++)for(int x=0;x<width;x++) {int slot=mode=="interlaced"?(height-1-y)&1:mode.Contains("over_under")?(y>=height/2?0:1):(x<width/2?0:1);int eye=swap?1-slot:slot;Check(pixels[(y*width+x)*4+(eye==0?0:2)]==255,"actual engine output "+mode);}
    };
    present.Invoke(instance,new object[]{render,width,height,read,false});
    var instanceFlags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
    Check((int)native.GetField("_width",instanceFlags).GetValue(instance)==(mode=="full_sbs"?width/2:width),"engine eye target width");
    Check((int)native.GetField("_height",instanceFlags).GetValue(instance)==(mode=="full_over_under"?height/2:height),"engine eye target height");
    Check(GL.GetError()==ErrorCode.NoError,"engine GL error");
   }
   Console.WriteLine("PASS: actual engine presentation, target resizing and live format/eye-order transitions: "+directory);
  } finally {((IDisposable)instance).Dispose();}
 }}
