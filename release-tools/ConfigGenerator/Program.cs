using System.Reflection;
using System.Runtime.Loader;

string runtime = Path.GetFullPath(args[0]);
string destination = Path.GetFullPath(args[1]);
AssemblyLoadContext.Default.Resolving += (_, name) => {
    string path = Path.Combine(runtime, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(runtime,"Ryujinx.dll"));
var type = app.GetType("Ryujinx.Ava.Systems.Configuration.ConfigurationState",true)!;
type.GetMethod("Initialize")!.Invoke(null,null);
var state = type.GetProperty("Instance")!.GetValue(null)!;
type.GetMethod("LoadDefault")!.Invoke(state,null);
var config = type.GetMethod("ToFileFormat")!.Invoke(state,null)!;
void Set(string name, object value) {
    var property = config.GetType().GetProperty(name) ?? throw new Exception("Unknown configuration field: "+name);
    property.SetValue(config, property.PropertyType.IsEnum ? Enum.Parse(property.PropertyType,(string)value) : value);
}
Set("GraphicsBackend","OpenGl");
Set("ResScale",-1); Set("ResScaleCustom",1.5f);
Set("EnableDiscordIntegration",false);
Set("UpdateCheckerType","Off");
Set("LoggingEnableStub",false); Set("LoggingEnableGuest",false);
Set("ShowConsole",false);
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
config.GetType().GetMethod("SaveConfig")!.Invoke(config,[destination]);
Console.WriteLine("Generated a fresh configuration from upstream defaults, with the tested stereo graphics preset.");
