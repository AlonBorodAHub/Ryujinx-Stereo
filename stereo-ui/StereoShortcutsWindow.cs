using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Ryujinx.Input.HLE;
using System;
using System.Linq;
namespace Ryujinx.Ava.Stereo;
internal sealed class StereoShortcutsWindow : Window
{
 public StereoShortcutsWindow(){
  Title="Stereo 3D keyboard shortcuts";Width=510;Height=530;MinHeight=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  var panel=new StackPanel{Margin=new Thickness(24),Spacing=12};
  panel.Children.Add(new TextBlock{Text="In-game keyboard shortcuts",FontSize=22});
  panel.Children.Add(new TextBlock{Text="Type combinations such as Ctrl+Left, Alt+Up or Shift+F7. Shortcuts work only while the game window is active.",TextWrapping=Avalonia.Media.TextWrapping.Wrap});
  string[] labels={"Decrease convergence","Increase convergence","Increase depth","Decrease depth","Reset depth and convergence"};
  var fields=labels.Select(_=>new TextBox()).ToArray();
  var status=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap};
  string[] values=StereoKeyboardBindings.Defaults;
  try{values=StereoKeyboardBindings.Load();}catch(Exception e){status.Text=e.Message;}
  for(int i=0;i<fields.Length;i++){var row=new Grid{ColumnDefinitions=new ColumnDefinitions("*,180")};row.Children.Add(new TextBlock{Text=labels[i],VerticalAlignment=VerticalAlignment.Center});fields[i].Text=values[i];Grid.SetColumn(fields[i],1);row.Children.Add(fields[i]);panel.Children.Add(row);}
  var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};
  var defaults=new Button{Content="Restore shortcuts"};defaults.Click+=(_,_)=>{var d=StereoKeyboardBindings.Defaults;for(int i=0;i<5;i++)fields[i].Text=d[i];status.Text="Press Save to apply.";};
  var save=new Button{Content="Save"};save.Click+=(_,_)=>{try{StereoKeyboardBindings.Save(fields.Select(f=>f.Text).ToArray());status.Text="Saved. Changes apply in-game within a second.";}catch(Exception e){status.Text=e.Message;}};
  var close=new Button{Content="Close"};close.Click+=(_,_)=>Close();
  buttons.Children.Add(defaults);buttons.Children.Add(save);buttons.Children.Add(close);
  panel.Children.Add(status);panel.Children.Add(buttons);Content=new ScrollViewer{Content=panel};
 }
}
