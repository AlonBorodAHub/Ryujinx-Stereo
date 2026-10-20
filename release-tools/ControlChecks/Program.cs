using System;
using System.IO;
using System.Text.Json.Nodes;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
class Program {
 static void Check(bool v,string m){if(!v)throw new Exception(m);}
 static void Main(){
  StereoKeyboardBindings.Validate(StereoKeyboardBindings.Defaults);
  Check(StereoKeyboardBindings.Parse("Ctrl+Right")==(1,39),"Default keyboard parsing");
  Check(StereoKeyboardBindings.Parse("Alt+Shift+F7")==(6,118),"Custom keyboard parsing");
  bool duplicate=false;try{StereoKeyboardBindings.Validate(new[]{"Ctrl+R","Ctrl+R","Ctrl+Up","Ctrl+Down","Alt+R"});}catch(ArgumentException){duplicate=true;}Check(duplicate,"Duplicate bindings accepted");
  Check(!StereoKeyboardAdjustments.Map(false,true,false,true,false,false,false,false).IsPressed(GamepadButtonInputId.DpadLeft),"Background input accepted");
  Check(!StereoKeyboardAdjustments.Map(true,false,false,true,false,false,false,false).IsPressed(GamepadButtonInputId.DpadLeft),"Unmodified input accepted");
  Check(StereoKeyboardAdjustments.Map(true,true,false,false,false,false,false,true).IsPressed(GamepadButtonInputId.B),"Keyboard reset mapping");
  string dir=Path.Combine(Path.GetTempPath(),"StereoChord-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
  string path=Path.Combine(dir,"0100000000010000.json");
  try {
   File.WriteAllText(path,"{\"separation\":0.02028,\"convergence\":600,\"custom_setting\":42}");
   Environment.SetEnvironmentVariable("RYU_GEOMETRY_STEREO","1");Environment.SetEnvironmentVariable("RYU_STEREO_SETTINGS",path);
   var controller=new StereoControllerAdjustments();var s=new GamepadStateSnapshot();
   s.SetPressed(GamepadButtonInputId.DpadRight,true);
   Check(controller.Process(s,0).IsPressed(GamepadButtonInputId.DpadRight),"Normal D-pad consumed");
   s.SetPressed(GamepadButtonInputId.LeftShoulder,true);controller.Process(s,1);
   Check(JsonNode.Parse(File.ReadAllText(path))["convergence"].GetValue<double>()==600,"One bumper adjusted settings");
   Check(StereoAdjustmentFeedback.Take()==null,"Normal input displayed stereo feedback");
   s.SetPressed(GamepadButtonInputId.RightShoulder,true);
   Check(!controller.Process(s,10).IsPressed(GamepadButtonInputId.DpadRight),"Chord leaked to game");
   controller.Process(s,100);
   Check(JsonNode.Parse(File.ReadAllText(path))["convergence"].GetValue<double>()==625,"Initial step/repeat delay wrong");
   var feedback=StereoAdjustmentFeedback.Take();
   Check(feedback!=null&&feedback.DepthPercent==100&&feedback.Convergence==625&&!feedback.Reset,"Convergence feedback wrong");
   Check(StereoAdjustmentFeedback.Take()==null,"Feedback replayed without a new adjustment");
   controller.Process(s,410);
   Check(JsonNode.Parse(File.ReadAllText(path))["convergence"].GetValue<double>()==650,"Held repeat failed");
   s.SetPressed(GamepadButtonInputId.DpadRight,false);s.SetPressed(GamepadButtonInputId.DpadLeft,true);controller.Process(s,420);
   Check(JsonNode.Parse(File.ReadAllText(path))["convergence"].GetValue<double>()==625,"Left direction wrong");
   s.SetPressed(GamepadButtonInputId.DpadLeft,false);s.SetPressed(GamepadButtonInputId.DpadUp,true);controller.Process(s,430);
   var profile=JsonNode.Parse(File.ReadAllText(path));
   Check(Math.Abs(profile["separation"].GetValue<double>()-.0206856)<1e-9,"Depth increase wrong");
   Check(profile["custom_setting"].GetValue<int>()==42,"Unrelated profile setting lost");
   s.SetPressed(GamepadButtonInputId.DpadUp,false);s.SetPressed(GamepadButtonInputId.DpadDown,true);controller.Process(s,440);
   Check(Math.Abs(JsonNode.Parse(File.ReadAllText(path))["separation"].GetValue<double>()-.02028)<1e-9,"Depth decrease wrong");
   s.SetPressed(GamepadButtonInputId.DpadDown,false);s.SetPressed(GamepadButtonInputId.DpadUp,true);controller.Process(s,450);
   s.SetPressed(GamepadButtonInputId.B,true);
   Check(!controller.Process(s,460).IsPressed(GamepadButtonInputId.B),"Reset B leaked to game");
   profile=JsonNode.Parse(File.ReadAllText(path));
   Check(Math.Abs(profile["separation"].GetValue<double>()-.02028)<1e-9,"Reset failed to restore original depth");
   Check(profile["convergence"].GetValue<double>()==600,"Reset failed to restore default convergence");
   feedback=StereoAdjustmentFeedback.Take();
   Check(feedback!=null&&Math.Abs(feedback.DepthPercent-100)<1e-9&&feedback.Convergence==600&&feedback.Reset,"Reset feedback wrong");
   string resetText=File.ReadAllText(path);controller.Process(s,1000);
   Check(File.ReadAllText(path)==resetText,"Held reset changed profile");
   var custom=new StereoControllerAdjustments();
   profile["separation"]=.013;File.WriteAllText(path,profile.ToJsonString());
   s.SetPressed(GamepadButtonInputId.B,false);custom.Process(s,1010);
   s.SetPressed(GamepadButtonInputId.B,true);custom.Process(s,1020);
   Check(Math.Abs(JsonNode.Parse(File.ReadAllText(path))["separation"].GetValue<double>()-.013)<1e-9,"Reset used factory depth instead of original custom depth");
   profile=JsonNode.Parse(File.ReadAllText(path));profile["default_separation"]=.0231192;profile["default_convergence"]=1775;
   File.WriteAllText(path,profile.ToJsonString());s.SetPressed(GamepadButtonInputId.B,false);custom.Process(s,1100);
   s.SetPressed(GamepadButtonInputId.B,true);custom.Process(s,1110);
   profile=JsonNode.Parse(File.ReadAllText(path));
   Check(Math.Abs(profile["separation"].GetValue<double>()-.0231192)<1e-9&&profile["convergence"].GetValue<double>()==1775,"Saved defaults were not restored");
   Environment.SetEnvironmentVariable("RYU_GEOMETRY_STEREO","0");
   Check(controller.Process(s,500).IsPressed(GamepadButtonInputId.LeftShoulder),"Non-stereo input consumed");
   Console.WriteLine("PASS: chord gating, directions, repeat, profile preservation, input suppression, non-stereo passthrough.");
  }finally{File.Delete(path);Directory.Delete(dir);}
 }
}
