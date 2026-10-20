using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Ava.UI.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ryujinx.Ava.Stereo;

internal sealed class StereoSettingsWindow : Window
{
    private sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
    private readonly ComboBox _game = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _enabled = new() { Content = "Enable stereoscopic 3D for this game" };
    private readonly Slider _depth = new() { Minimum = 0, Maximum = 200, TickFrequency = 5, IsSnapToTickEnabled = false };
    private readonly TextBlock _depthValue = new();
    private readonly NumericUpDown _convergence = new() { Minimum = 0, Maximum = 10000, FormatString = "0.###", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private string _id;
    public StereoSettingsWindow(IEnumerable<ApplicationData> applications, string selected)
    {
        Title = "Ryujinx Stereo — Stereo 3D";
        Width = 560; Height = 580; MinWidth = 460; MinHeight = 540;
        CanResize = true; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var choices = new List<Choice> { new(StereoProfiles.Mario, "Super Mario Odyssey"), new(StereoProfiles.Zelda, "The Legend of Zelda: Breath of the Wild"), new(StereoProfiles.Wonder, "Super Mario Bros. Wonder") };
        foreach (var app in applications ?? Enumerable.Empty<ApplicationData>())
        {
            if (app.Id != 0 && choices.All(c => c.Id != app.IdBaseString)) choices.Add(new(app.IdBaseString, app.Name));
        }
        if (!string.IsNullOrWhiteSpace(selected) && choices.All(c => c.Id != selected))
            choices.Add(new(StereoProfiles.Normalize(selected), "Current game (" + selected + ")"));
        _game.ItemsSource = choices;
        _game.SelectionChanged += (_, _) => LoadChoice();
        _depth.ValueChanged += (_, _) => _depthValue.Text = $"{_depth.Value:0}%";
        var apply = new Button { Content = "Apply", MinWidth = 95 };
        apply.Click += (_, _) => ApplyChanges();
        var reset = new Button { Content = "Reset depth & convergence" };
        reset.Click += (_, _) => { _depth.Value = 100; _convergence.Value = (decimal)StereoProfiles.DefaultConvergence(_id); _status.Text = "Press Apply to restore this game's depth and convergence."; };
        var close = new Button { Content = "Close", MinWidth = 80 };
        close.Click += (_, _) => Close();
        var panel = new StackPanel { Margin = new Thickness(28), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = "Stereo 3D", FontSize = 27, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Each game keeps its own depth and convergence.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_game); panel.Children.Add(_enabled);
        panel.Children.Add(new TextBlock { Text = "Depth", FontWeight = FontWeight.SemiBold });
        var depthRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,65") };
        depthRow.Children.Add(_depth); Grid.SetColumn(_depthValue, 1); _depthValue.VerticalAlignment = VerticalAlignment.Center; depthRow.Children.Add(_depthValue);
        panel.Children.Add(depthRow);
        panel.Children.Add(new TextBlock { Text = "Convergence", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(_convergence);
        panel.Children.Add(new TextBlock { Text = "Depth sets the strength of 3D. Convergence sets the distance where the eyes align. Values differ between games.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Apply updates depth and convergence during gameplay. Enabling or disabling 3D takes effect on the next game launch. Press F6 to open these controls while playing.", TextWrapping = TextWrapping.Wrap });
        var shortcuts = new Button { Content = "Keyboard shortcuts…" };
        shortcuts.Click += async (_, _) => await new StereoShortcutsWindow().ShowDialog(this);
        panel.Children.Add(shortcuts);
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(reset); buttons.Children.Add(apply); buttons.Children.Add(close); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _game.SelectedItem = choices.FirstOrDefault(c => c.Id == selected) ?? choices[0];
    }
    private void LoadChoice()
    {
        if (_game.SelectedItem is not Choice choice) return;
        _id = choice.Id;
        try
        {
            var profile = StereoProfiles.Load(_id);
            _enabled.IsChecked = StereoProfiles.Enabled(profile);
            _depth.Value = StereoProfiles.Number(profile, "separation", StereoProfiles.DefaultSeparation(_id)) / StereoProfiles.DefaultSeparation(_id) * 100;
            _convergence.Increment = _id == StereoProfiles.Zelda ? .5m : 25m;
            _convergence.Value = (decimal)StereoProfiles.Number(profile, "convergence", StereoProfiles.DefaultConvergence(_id));
            _status.Text = _id is StereoProfiles.Mario or StereoProfiles.Zelda or StereoProfiles.Wonder ? "Working game profile loaded." : "New game profile. Stereo compatibility needs to be checked in gameplay.";
        }
        catch (Exception error) { _status.Text = error.Message; }
    }
    private void ApplyChanges()
    {
        try
        {
            StereoProfiles.Save(_id, _enabled.IsChecked == true, _depth.Value, (double)(_convergence.Value ?? 0));
            Ryujinx.Input.HLE.StereoAdjustmentFeedback.Publish(_depth.Value, (double)(_convergence.Value ?? 0));
            _status.Text = "Saved for this game. Depth and convergence apply to an active 3D session within a second.";
        }
        catch (Exception error) { _status.Text = "Could not save: " + error.Message; }
    }
    public static async System.Threading.Tasks.Task Open(MainWindow owner)
    {
        string selected = StereoRuntime.ActiveId ?? owner.ViewModel.SelectedApplication?.IdBaseString;
        await new StereoSettingsWindow(owner.ViewModel.Applications, selected).ShowDialog(owner);
    }
}
