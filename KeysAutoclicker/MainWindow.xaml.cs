using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Diagnostics;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace KeysAutoclicker;
public partial class MainWindow : Window
{
    AppSettings _settings = new(); GlobalHotkeys? _hotkeys; TrayStatusIcon? _tray; ShortcutRecorder? _recorder; SequenceRecorder? _sequenceRecorder; int _recordedCount; readonly SemaphoreSlim _runner = new(1, 1);
    public ObservableCollection<Profile> Profiles => _settings.Profiles;
    public Profile? SelectedProfile { get; set; }
    public MainWindow() => InitializeComponent();
    void Window_Loaded(object s, RoutedEventArgs e)
    {
        _settings = SettingsStore.Load(); if (Profiles.Count == 0) Seed(); DataContext = this; ProfileList.SelectedIndex = 0; MasterToggle.IsChecked = _settings.MasterEnabled; ThemeToggle.IsChecked = _settings.DarkTheme; KeyDelayInput.Text = _settings.NormalKeyDelayMs.ToString(); ShortcutDelayInput.Text = _settings.ShortcutDelayMs.ToString(); ApplyTheme(_settings.DarkTheme);
        _tray = new TrayStatusIcon(this, _settings.MasterEnabled); _hotkeys = new GlobalHotkeys(this, macro => _ = RunMacro(macro)); RefreshRegistry();
    }
    void Seed() { var p = new Profile { Name = "General" }; p.Macros.Add(new Macro { Name = "Copy to Other App", Trigger = "F1", Actions = new ObservableCollection<ActionStep> { new() { Value="Ctrl+C" }, new() { Value="Alt+Tab" }, new() { Kind="Delay", DelayMs=300 }, new() { Value="Ctrl+V" }, new() { Value="Enter" } } }); Profiles.Add(p); }
    void ApplyTheme(bool dark) { Resources["AppBackground"] = Brush(dark ? "#101318" : "#F4F7FB"); Resources["CardBackground"] = Brush(dark ? "#1B2028" : "#FFFFFF"); Resources["InputBackground"] = Brush(dark ? "#252C36" : "#EEF2F8"); Resources["Foreground"] = Brush(dark ? "#F4F7FB" : "#17202D"); Resources["Muted"] = Brush(dark ? "#ABB6C5" : "#66758A"); Resources["BorderColor"] = Brush(dark ? "#384352" : "#D5DDE8"); }
    static System.Windows.Media.Brush Brush(string color) => new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    void ThemeToggle_Changed(object s, RoutedEventArgs e) { if (IsLoaded) { _settings.DarkTheme = ThemeToggle.IsChecked == true; ApplyTheme(_settings.DarkTheme); } }
    void MasterToggle_Changed(object s, RoutedEventArgs e) { if (!IsLoaded) return; _settings.MasterEnabled = MasterToggle.IsChecked == true; _tray?.SetMasterState(_settings.MasterEnabled); RefreshRegistry(); }
    void ProfileList_SelectionChanged(object s, SelectionChangedEventArgs e) { SelectedProfile = ProfileList.SelectedItem as Profile; MacroItems.ItemsSource = SelectedProfile?.Macros; }
    void NewProfile_Click(object s, RoutedEventArgs e) { var p = new Profile { Name = "New profile" }; Profiles.Add(p); ProfileList.SelectedItem = p; }
    void DuplicateProfile_Click(object s, RoutedEventArgs e) { if (SelectedProfile is null) return; var p = SelectedProfile.Clone(); Profiles.Insert(Profiles.IndexOf(SelectedProfile)+1,p); ProfileList.SelectedItem = p; RefreshRegistry(); }
    void DeleteProfile_Click(object s, RoutedEventArgs e) { if (SelectedProfile is null || Profiles.Count == 1) { StatusText.Text="Keep at least one profile."; return; } Profiles.Remove(SelectedProfile); ProfileList.SelectedIndex=0; RefreshRegistry(); }
    void MoveProfile_Click(object s, RoutedEventArgs e) { if (SelectedProfile is null || (s as Button)?.Tag is not string direction) return; var i=Profiles.IndexOf(SelectedProfile); var to=direction=="Up"?i-1:i+1; if(to>=0 && to<Profiles.Count) Profiles.Move(i,to); }
    void ProfileToggle_Changed(object s, RoutedEventArgs e) => RefreshRegistry();
    void AddMacro_Click(object s, RoutedEventArgs e) { if (SelectedProfile is null) return; var m=new Macro(); m.Actions.Add(new ActionStep()); SelectedProfile.Macros.Add(m); RefreshRegistry(); }
    void DeleteMacro_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is Macro m && SelectedProfile is not null) { SelectedProfile.Macros.Remove(m); RefreshRegistry(); } }
    void DuplicateMacro_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is Macro m && SelectedProfile is not null) { var copy=m.Clone(); SelectedProfile.Macros.Insert(SelectedProfile.Macros.IndexOf(m)+1,copy); RefreshRegistry(); } }
    void MacroToggle_Changed(object s, RoutedEventArgs e) => RefreshRegistry();
    void Trigger_PreviewKeyDown(object s, System.Windows.Input.KeyEventArgs e) { var text=KeyNotation.Capture(e); if(string.IsNullOrEmpty(text)) return; ((TextBox)s).Text=text; e.Handled=true; Dispatcher.BeginInvoke(RefreshRegistry); }
    void Action_PreviewKeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is ActionStep { Kind: "Delay" }) return;
        var text=KeyNotation.Capture(e); if(string.IsNullOrEmpty(text)) return; ((TextBox)s).Text=text; e.Handled=true;
    }
    void RecordTrigger_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is Macro macro) StartRecording(text => { macro.Trigger = text; RefreshRegistry(); });
    }
    void RecordAction_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is ActionStep action && action.Kind != "Delay") StartRecording(text => action.EditorValue = text);
    }
    void StartRecording(Action<string> completed)
    {
        _recorder?.Dispose(); StatusText.Text = "Recording — press a key or shortcut (Esc cancels).";
        _recorder = new ShortcutRecorder(text => Dispatcher.BeginInvoke(() => { completed(text); StatusText.Text = $"Recorded {text}."; _recorder = null; }));
        if (!_recorder.IsActive) StatusText.Text = "Could not start shortcut recording.";
    }
    void RecordSequence_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not Macro macro) return;
        _recorder?.Dispose(); _sequenceRecorder?.Dispose(); _recordedCount = 0;
        StatusText.Text = "Recording actions — press Esc to stop. Keys will not affect other apps while recording.";
        _sequenceRecorder = new SequenceRecorder(text => Dispatcher.BeginInvoke(() =>
        {
            macro.Actions.Add(new ActionStep { Value = text }); _recordedCount++;
            StatusText.Text = $"Recording actions — {_recordedCount} captured. Press Esc to stop.";
        }), () => Dispatcher.BeginInvoke(() => { StatusText.Text = $"Sequence recording complete — {_recordedCount} action(s) added."; _sequenceRecorder = null; }));
        if (!_sequenceRecorder.IsActive) StatusText.Text = "Could not start sequence recording.";
    }
    void AddAction_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is Macro m) m.Actions.Add(new ActionStep()); }
    void AddDelay_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is Macro m) m.Actions.Add(new ActionStep { Kind="Delay" }); }
    void DeleteAction_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is ActionStep a && FindMacro(a) is { } m) m.Actions.Remove(a); }
    void DuplicateAction_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is ActionStep a && FindMacro(a) is { } m) m.Actions.Insert(m.Actions.IndexOf(a)+1,a.Clone()); }
    void MoveAction_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is not ActionStep a || (s as Button)?.Tag is not string d || FindMacro(a) is not { } m) return; var i=m.Actions.IndexOf(a); var to=d=="Up"?i-1:i+1; if(to>=0&&to<m.Actions.Count)m.Actions.Move(i,to); }
    Macro? FindMacro(ActionStep step) => Profiles.SelectMany(p=>p.Macros).FirstOrDefault(m=>m.Actions.Contains(step));
    void RefreshRegistry()
    {
        if (_hotkeys is null) return; var enabled = _settings.MasterEnabled ? Profiles.Where(p=>p.Enabled).SelectMany(p=>p.Macros).Where(m=>m.Enabled).ToList() : new List<Macro>();
        var duplicate=enabled.GroupBy(m=>m.Trigger.Trim(),StringComparer.OrdinalIgnoreCase).Where(g=>!string.IsNullOrWhiteSpace(g.Key)&&g.Count()>1).Select(g=>g.Key).ToList();
        if(duplicate.Count>0){ StatusText.Text="Trigger conflict: "+string.Join(", ",duplicate); _hotkeys.Register(Array.Empty<Macro>()); return; }
        var errors=_hotkeys.Register(enabled); StatusText.Text=errors.Count==0 ? $"Ready — {enabled.Count} enabled shortcut{(enabled.Count==1?"":"s")}." : string.Join("; ",errors);
    }
    async Task RunMacro(Macro macro)
    {
        if (!_settings.MasterEnabled || !Profiles.Any(p=>p.Enabled&&p.Macros.Contains(macro)&&macro.Enabled)) return;
        if (!ReadPlaybackDelays(out var keyDelay, out var shortcutDelay)) return;
        await _runner.WaitAsync(); try { Diagnostics.Write($"MACRO START name={macro.Name}"); await Dispatcher.InvokeAsync(()=>StatusText.Text=$"Running {macro.Name}…"); foreach(var step in macro.Actions.ToList()) { if(step.Kind=="Delay") await Task.Delay(step.DelayMs); else for(var i=0;i<step.Repeat;i++) { KeyboardSender.Press(step.Value); await Task.Delay(step.Value.Contains('+') ? shortcutDelay : keyDelay); } } Diagnostics.Write($"MACRO COMPLETE name={macro.Name}"); await Dispatcher.InvokeAsync(()=>StatusText.Text=$"Completed {macro.Name}."); } catch (Exception ex) { Diagnostics.Write($"MACRO ERROR {ex}"); KeyboardSender.ReleaseModifiers(); await Dispatcher.InvokeAsync(()=>StatusText.Text="Macro stopped safely."); } finally { KeyboardSender.ReleaseModifiers(); _runner.Release(); }
    }
    bool ReadPlaybackDelays(out int keyDelay, out int shortcutDelay)
    {
        if (!int.TryParse(KeyDelayInput.Text, out keyDelay) || keyDelay < 0 || !int.TryParse(ShortcutDelayInput.Text, out shortcutDelay) || shortcutDelay < 0) { shortcutDelay = 0; StatusText.Text = "Playback delays must be zero or more milliseconds."; return false; }
        _settings.NormalKeyDelayMs = keyDelay; _settings.ShortcutDelayMs = shortcutDelay; return true;
    }
    void Website_RequestNavigate(object s, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
    void Window_Closing(object s, System.ComponentModel.CancelEventArgs e) { _recorder?.Dispose(); _sequenceRecorder?.Dispose(); ReadPlaybackDelays(out _, out _); KeyboardSender.ReleaseModifiers(); SettingsStore.Save(_settings); _hotkeys?.Dispose(); _tray?.Dispose(); }
}
