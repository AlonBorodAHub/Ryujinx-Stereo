using System;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace Ryujinx.Input.HLE;
public static class StereoKeyboardBindings
{
 public static string[] Defaults => new[] {"Ctrl+Left","Ctrl+Right","Ctrl+Up","Ctrl+Down","Ctrl+R"};
 public static string PathName => Path.Combine(Environment.GetEnvironmentVariable("RJ_STEREO_HOME") ?? AppContext.BaseDirectory,"stereo-shortcuts.json");
 public static (int Modifiers,int Key) Parse(string text) {
  string[] parts=(text??"").Split('+',StringSplitOptions.TrimEntries);int mods=0;
  foreach(string part in parts.Take(parts.Length-1)) {int bit=part.ToUpperInvariant() switch {"CTRL"=>1,"ALT"=>2,"SHIFT"=>4,"WIN"=>8,_=>throw new ArgumentException("Use Ctrl, Alt, Shift or Win.")};if((mods&bit)!=0)throw new ArgumentException("Repeated modifier.");mods|=bit;}
  string key=parts.Last().ToUpperInvariant();int code=key switch {"LEFT"=>37,"UP"=>38,"RIGHT"=>39,"DOWN"=>40,"SPACE"=>32,"HOME"=>36,"END"=>35,"PAGEUP"=>33,"PAGEDOWN"=>34,"INSERT"=>45,"DELETE"=>46,_=>0};
  if(key.Length==1&&char.IsAsciiLetterOrDigit(key[0]))code=key[0];
  if(key.StartsWith("F")&&int.TryParse(key.AsSpan(1),out int f)&&f>=1&&f<=24)code=111+f;
  if(code==0||code==123||code==117)throw new ArgumentException("Use a letter, number, arrow or function key. F6 opens 3D settings; F12 is reserved.");
  if(mods==0&&(code<112||code>135))throw new ArgumentException("Add a modifier such as Ctrl.");
  if((mods&2)!=0&&code==115)throw new ArgumentException("Alt+F4 is reserved for closing the game.");
  return (mods,code);
 }
 public static void Validate(string[] values){if(values==null||values.Length!=5)throw new ArgumentException("Five shortcuts are required.");var parsed=values.Select(Parse).ToArray();if(parsed.Distinct().Count()!=5)throw new ArgumentException("Each action needs a different shortcut.");}
 public static string[] Load(){if(!File.Exists(PathName))return Defaults;var values=JsonSerializer.Deserialize<string[]>(File.ReadAllText(PathName));Validate(values);return values;}
 public static void Save(string[] values){Validate(values);string temp=PathName+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(temp,JsonSerializer.Serialize(values));File.Move(temp,PathName,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
}
