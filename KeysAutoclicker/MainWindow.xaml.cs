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
    AppSettings _settings = new(); GlobalHotkeys? _hotkeys; TrayStatusIcon? _tray; ShortcutRecorder? _recorder; SequenceRecorder? _sequenceRecorder; Macro? _recordingMacro, _recordingTarget; ActionStep? _draggingAction; System.Windows.Point _dragStart; System.Windows.Documents.AdornerLayer? _dragLayer; ActionDragAdorner? _dragPreview; FrameworkElement? _dragRow; System.Windows.Controls.Border? _insertionRow; readonly SortedDictionary<long, (RecordedInput Input, bool Paused)> _pendingRecordedInputs = new(); RecordedInput? _lastLeftClickInput; long _nextRecordedSequence; int _recordedCount; bool _recordingPaused, _allowClose; CancellationTokenSource? _playbackCancellation; readonly SemaphoreSlim _runner = new(1, 1);
    public ObservableCollection<Profile> Profiles => _settings.Profiles;
    public Profile? SelectedProfile { get; set; }
    public MainWindow() => InitializeComponent();
    void Window_Loaded(object s, RoutedEventArgs e)
    {
        _settings = SettingsStore.Load(); if (Profiles.Count == 0) Seed(); DataContext = this; ProfileList.SelectedIndex = 0; MasterToggle.IsChecked = _settings.MasterEnabled; ThemeToggle.IsChecked = _settings.DarkTheme; KeyDelayInput.Text = _settings.NormalKeyDelayMs.ToString(); ShortcutDelayInput.Text = _settings.ShortcutDelayMs.ToString(); MouseMoveDelayInput.Text = _settings.MouseMoveSpeedPercent.ToString(); MouseClickDelayInput.Text = _settings.MouseClickDelayMs.ToString(); ApplyTheme(_settings.DarkTheme);
        StartStopShortcutInput.Text = _settings.StartStopRecordingShortcut; PauseResumeShortcutInput.Text = _settings.PauseResumeRecordingShortcut; EmergencyStopShortcutInput.Text = _settings.EmergencyStopShortcut;
        _tray = new TrayStatusIcon(this, _settings.MasterEnabled, ExitApplication); _hotkeys = new GlobalHotkeys(this, macro => _ = RunMacro(macro)); RefreshRegistry();
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
        _recordingTarget = macro; BeginRecording(macro); if (_sequenceRecorder is not null) return;
        _recorder?.Dispose(); _sequenceRecorder?.Dispose(); _recordedCount = 0;
        StatusText.Text = "Recording actions — press Esc to stop. Keys will not affect other apps while recording.";
        _sequenceRecorder = new SequenceRecorder(input => Dispatcher.BeginInvoke(() =>
        {
            macro.Actions.Add(CreateRecordedStep(input.Value)); _recordedCount++;
            StatusText.Text = $"Recording actions — {_recordedCount} captured. Press Esc to stop.";
        }), () => Dispatcher.BeginInvoke(() => { StatusText.Text = $"Sequence recording complete — {_recordedCount} action(s) added."; _sequenceRecorder = null; }));
        if (!_sequenceRecorder.IsActive) StatusText.Text = "Could not start sequence recording.";
    }
    void StartStopRecording_Click(object s, RoutedEventArgs e) => ToggleGlobalRecording();
    void PauseResumeRecording_Click(object s, RoutedEventArgs e) => ToggleRecordingPause();
    void CancelRecording_Click(object s, RoutedEventArgs e) => FinishGlobalRecording(true);
    void BeginRecording(Macro macro)
    {
        _recordingMacro = macro; _recordingTarget = macro; _recorder?.Dispose(); _sequenceRecorder?.Dispose(); _recordedCount = 0; _recordingPaused = false; _nextRecordedSequence = 1; _lastLeftClickInput = null; _pendingRecordedInputs.Clear();
        _sequenceRecorder = new SequenceRecorder(input => { var paused = _recordingPaused; Dispatcher.BeginInvoke(() => QueueRecordedInput(macro, input, paused)); }, () => Dispatcher.BeginInvoke(() => FinishGlobalRecording(false)), suppress: false, ignore: IsRecordingControlShortcut);
        if (!_sequenceRecorder.IsActive) { _recordingMacro = null; _sequenceRecorder = null; StatusText.Text = "Could not start recording."; return; }
        _tray?.SetRecording(true); StatusText.Text = $"Recording - 0 event(s). {DisplayShortcut(_settings.StartStopRecordingShortcut)} to stop.";
    }
    void QueueRecordedInput(Macro macro, RecordedInput input, bool paused)
    {
        _pendingRecordedInputs[input.Sequence] = (input, paused);
        while (_pendingRecordedInputs.Remove(_nextRecordedSequence, out var queued))
        {
            if (!queued.Paused)
            {
                if (IsDoubleClick(_lastLeftClickInput, queued.Input) && macro.Actions.LastOrDefault() is { Kind: "Mouse Left Click" } previous) previous.Kind = "Mouse Double Click";
                else { macro.Actions.Add(CreateRecordedStep(queued.Input.Value)); _recordedCount++; }
                if (queued.Input.Value.StartsWith("MouseLeftClick:", StringComparison.Ordinal)) _lastLeftClickInput = queued.Input;
            }
            _nextRecordedSequence++;
        }
        if (!_recordingPaused) StatusText.Text = $"Recording - {_recordedCount} event(s). {DisplayShortcut(_settings.StartStopRecordingShortcut)} to stop.";
    }
    static bool IsDoubleClick(RecordedInput? previous, RecordedInput current) => previous is { } prior && prior.Value.StartsWith("MouseLeftClick:", StringComparison.Ordinal) && current.Value.StartsWith("MouseLeftClick:", StringComparison.Ordinal) && current.Timestamp - prior.Timestamp <= System.Windows.Forms.SystemInformation.DoubleClickTime && SameMousePoint(prior.Value, current.Value);
    static bool SameMousePoint(string first, string second) => first[(first.IndexOf(':') + 1)..] == second[(second.IndexOf(':') + 1)..];
    void ToggleGlobalRecording()
    {
        if (_sequenceRecorder is not null) { FinishGlobalRecording(false); return; }
        var macro = _recordingTarget ?? SelectedProfile?.Macros.FirstOrDefault();
        if (macro is null) { StatusText.Text = "Create or select a shortcut before recording."; return; }
        BeginRecording(macro); if (_sequenceRecorder is not null) return;
        _recorder?.Dispose(); _recordedCount = 0; _recordingPaused = false;
        _sequenceRecorder = new SequenceRecorder(input => Dispatcher.BeginInvoke(() =>
        {
            if (_recordingPaused) return;
            macro.Actions.Add(CreateRecordedStep(input.Value)); _recordedCount++;
            StatusText.Text = $"Recording - {_recordedCount} event(s). {DisplayShortcut(_settings.StartStopRecordingShortcut)} to stop.";
        }), () => Dispatcher.BeginInvoke(() => FinishGlobalRecording(false)), suppress: false, ignore: IsRecordingControlShortcut);
        if (!_sequenceRecorder.IsActive) { _recordingMacro = null; _sequenceRecorder = null; StatusText.Text = "Could not start recording."; return; }
        _tray?.SetRecording(true); StatusText.Text = $"Recording - 0 event(s). {DisplayShortcut(_settings.StartStopRecordingShortcut)} to stop.";
    }
    void ToggleRecordingPause()
    {
        if (_sequenceRecorder is null) return;
        _recordingPaused = !_recordingPaused;
        StatusText.Text = _recordingPaused ? $"Recording paused - {DisplayShortcut(_settings.PauseResumeRecordingShortcut)} to resume." : $"Recording - {_recordedCount} event(s). {DisplayShortcut(_settings.StartStopRecordingShortcut)} to stop.";
    }
    void FinishGlobalRecording(bool cancel)
    {
        var recorder = _sequenceRecorder; _sequenceRecorder = null; recorder?.Dispose(); _recordingPaused = false; _tray?.SetRecording(false);
        _recordingMacro = null;
        StatusText.Text = cancel ? "Recording cancelled." : $"Recording stopped - {_recordedCount} action(s) ready to review and edit.";
    }
    void RecordStartStopShortcut_Click(object s, RoutedEventArgs e) => StartRecording(text => { StartStopShortcutInput.Text = text; SaveRecordingShortcuts(); });
    void RecordPauseResumeShortcut_Click(object s, RoutedEventArgs e) => StartRecording(text => { PauseResumeShortcutInput.Text = text; SaveRecordingShortcuts(); });
    void RecordEmergencyStopShortcut_Click(object s, RoutedEventArgs e) => StartRecording(text => { EmergencyStopShortcutInput.Text = text; SaveRecordingShortcuts(); });
    void RecordingShortcut_PreviewKeyDown(object s, System.Windows.Input.KeyEventArgs e) { var text = KeyNotation.Capture(e); if (!string.IsNullOrEmpty(text)) { ((TextBox)s).Text = text; e.Handled = true; SaveRecordingShortcuts(); } }
    void SaveRecordingShortcuts() { _settings.StartStopRecordingShortcut = StartStopShortcutInput.Text.Trim(); _settings.PauseResumeRecordingShortcut = PauseResumeShortcutInput.Text.Trim(); _settings.EmergencyStopShortcut = EmergencyStopShortcutInput.Text.Trim(); RefreshRegistry(); }
    static string DisplayShortcut(string text) => text.Replace("+", " + ");
    bool IsRecordingControlShortcut(string text) => string.Equals(text, _settings.StartStopRecordingShortcut, StringComparison.OrdinalIgnoreCase) || string.Equals(text, _settings.PauseResumeRecordingShortcut, StringComparison.OrdinalIgnoreCase) || string.Equals(text, _settings.EmergencyStopShortcut, StringComparison.OrdinalIgnoreCase);
    static ActionStep CreateRecordedStep(string text)
    {
        var split = text.Split(':', 2); var value = split.Length == 2 ? split[1] : "";
        return split[0] switch
        {
            "MouseMove" => new ActionStep { Kind = "Mouse Move", Value = value },
            "MouseLeftClick" => new ActionStep { Kind = "Mouse Left Click", Value = value },
            "MouseRightClick" => new ActionStep { Kind = "Mouse Right Click", Value = value },
            "MouseMiddleClick" => new ActionStep { Kind = "Mouse Middle Click", Value = value },
            "MouseWheel" => new ActionStep { Kind = "Mouse Wheel", Value = value },
            _ => new ActionStep { Value = text }
        };
    }
    void AddAction_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is Macro m) m.Actions.Add(new ActionStep()); }
    void AddDelay_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is Macro m) m.Actions.Add(new ActionStep { Kind="Delay" }); }
    void DeleteAction_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is ActionStep a && FindMacro(a) is { } m) m.Actions.Remove(a); }
    void DuplicateAction_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is ActionStep a && FindMacro(a) is { } m) m.Actions.Insert(m.Actions.IndexOf(a)+1,a.Clone()); }
    void MoveAction_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.DataContext is not ActionStep a || (s as Button)?.Tag is not string d || FindMacro(a) is not { } m) return; var i=m.Actions.IndexOf(a); var to=d=="Up"?i-1:i+1; if(to>=0&&to<m.Actions.Count)m.Actions.Move(i,to); }
    void DragGrip_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not ActionStep action) return;
        _draggingAction = action; _dragStart = e.GetPosition(this); e.Handled = true;
    }
    void DragGrip_MouseMove(object s, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingAction is null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragRow = FindActionRow(s as FrameworkElement, _draggingAction); _dragLayer = _dragRow is null ? null : System.Windows.Documents.AdornerLayer.GetAdornerLayer(_dragRow);
        if (_dragRow is not null && _dragLayer is not null) { _dragPreview = new ActionDragAdorner(this, _dragRow); _dragPreview.Move(position); _dragLayer.Add(_dragPreview); _dragRow.Opacity = 0.28; }
        try { DragDrop.DoDragDrop((DependencyObject)s, new System.Windows.DataObject(typeof(ActionStep), _draggingAction), System.Windows.DragDropEffects.Move); }
        finally { if (_dragPreview is not null && _dragLayer is not null) _dragLayer.Remove(_dragPreview); if (_dragRow is not null) _dragRow.Opacity = 1; ClearInsertion(); _dragPreview = null; _dragLayer = null; _dragRow = null; _draggingAction = null; }
    }
    void Action_DragOver(object s, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ActionStep)) ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None; _dragPreview?.Move(e.GetPosition(this));
        if (e.Effects == System.Windows.DragDropEffects.Move && e.Data.GetData(typeof(ActionStep)) is ActionStep source && (s as System.Windows.Controls.Border) is { DataContext: ActionStep target } targetRow && source != target && FindMacro(source) is { } macro && FindItemsControl(targetRow) is { } items)
        {
            var sourceIndex = macro.Actions.IndexOf(source); var targetIndex = macro.Actions.IndexOf(target); var insertAfter = e.GetPosition(targetRow).Y > targetRow.ActualHeight / 2; var destination = targetIndex + (insertAfter ? 1 : 0); if (sourceIndex < destination) destination--;
            ShowInsertion(targetRow, insertAfter); if (destination != sourceIndex) MoveActionWithAnimation(items, macro, source, destination);
        }
        e.Handled = true;
    }
    static FrameworkElement? FindActionRow(FrameworkElement? child, ActionStep action)
    {
        while (child is not null) { if (child.DataContext == action && child is System.Windows.Controls.Border border && border.AllowDrop) return border; child = System.Windows.Media.VisualTreeHelper.GetParent(child) as FrameworkElement; }
        return null;
    }
    void Action_Drop(object s, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ActionStep)) is not ActionStep source || FindMacro(source) is not { } macro || !macro.Actions.Contains(source)) return;
        if (FindItemsControl(s as FrameworkElement) is { } items) AnimateActionRows(items, CaptureActionPositions(items, macro));
        ClearInsertion();
        e.Handled = true;
    }
    static System.Windows.Controls.ItemsControl? FindItemsControl(FrameworkElement? child)
    {
        while (child is not null) { if (child is System.Windows.Controls.ItemsControl items && items.ItemsSource is not null) return items; child = System.Windows.Media.VisualTreeHelper.GetParent(child) as FrameworkElement; }
        return null;
    }
    static Dictionary<ActionStep, double> CaptureActionPositions(System.Windows.Controls.ItemsControl items, Macro macro)
    {
        var positions = new Dictionary<ActionStep, double>(); foreach (var action in macro.Actions) if (items.ItemContainerGenerator.ContainerFromItem(action) is UIElement row) positions[action] = row.TransformToAncestor(items).Transform(new System.Windows.Point()).Y; return positions;
    }
    void MoveActionWithAnimation(System.Windows.Controls.ItemsControl items, Macro macro, ActionStep source, int destination)
    {
        var before = CaptureActionPositions(items, macro); macro.Actions.Move(macro.Actions.IndexOf(source), destination); Dispatcher.BeginInvoke(() => AnimateActionRows(items, before), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    static void AnimateActionRows(System.Windows.Controls.ItemsControl items, Dictionary<ActionStep, double> before)
    {
        foreach (var pair in before) if (items.ItemContainerGenerator.ContainerFromItem(pair.Key) is UIElement row) { var now = row.TransformToAncestor(items).Transform(new System.Windows.Point()).Y; var delta = pair.Value - now; if (Math.Abs(delta) < 0.5) continue; var transform = new System.Windows.Media.TranslateTransform(); row.RenderTransform = transform; transform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } }); }
    }
    void ShowInsertion(System.Windows.Controls.Border row, bool after)
    {
        if (_insertionRow == row) return; ClearInsertion(); _insertionRow = row; row.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(82, 151, 255)); row.BorderThickness = after ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 0);
    }
    void ClearInsertion() { if (_insertionRow is null) return; _insertionRow.BorderBrush = System.Windows.Media.Brushes.Transparent; _insertionRow.BorderThickness = new Thickness(0); _insertionRow = null; }
    Macro? FindMacro(ActionStep step) => Profiles.SelectMany(p=>p.Macros).FirstOrDefault(m=>m.Actions.Contains(step));
    void RefreshRegistry()
    {
        EnsureMacroEditor(); if (_hotkeys is null) return; var enabled = _settings.MasterEnabled ? Profiles.Where(p=>p.Enabled).SelectMany(p=>p.Macros).Where(m=>m.Enabled).ToList() : new List<Macro>();
        var controls = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
        if (_settings.MasterEnabled) { controls[_settings.StartStopRecordingShortcut] = ToggleGlobalRecording; controls[_settings.PauseResumeRecordingShortcut] = ToggleRecordingPause; controls[_settings.EmergencyStopShortcut] = EmergencyStop; }
        var reserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); reserved[_settings.StartStopRecordingShortcut] = "Start / Stop Recording"; reserved[_settings.PauseResumeRecordingShortcut] = "Pause / Resume Recording"; reserved[_settings.EmergencyStopShortcut] = "Emergency Stop";
        var reservedConflict = enabled.FirstOrDefault(m => reserved.ContainsKey(m.Trigger.Trim()));
        if (_settings.MasterEnabled && controls.Count != 3) { StatusText.Text = "Recording-control shortcuts must each be different."; _hotkeys.Register(Array.Empty<Macro>()); return; }
        var duplicate=enabled.GroupBy(m=>m.Trigger.Trim(),StringComparer.OrdinalIgnoreCase).Where(g=>!string.IsNullOrWhiteSpace(g.Key)&&g.Count()>1).Select(g=>g.Key).ToList();
        var blocked = enabled.Where(m => reserved.ContainsKey(m.Trigger.Trim()) || duplicate.Contains(m.Trigger.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
        var eligible = enabled.Except(blocked).ToList(); var errors=_hotkeys.Register(eligible, controls);
        var notices = new List<string>(); if (reservedConflict is not null) notices.Add($"{reservedConflict.Trigger} is reserved for {reserved[reservedConflict.Trigger.Trim()]}"); if (duplicate.Count > 0) notices.Add("Trigger conflict: " + string.Join(", ", duplicate)); notices.AddRange(errors);
        StatusText.Text = notices.Count == 0 ? $"Ready — {eligible.Count} enabled shortcut{(eligible.Count==1?"":"s")}." : string.Join("; ", notices) + ". Shortcuts remain visible for editing.";
    }
    void EnsureMacroEditor()
    {
        if (SelectedProfile is null || !Profiles.Contains(SelectedProfile)) { SelectedProfile = Profiles.FirstOrDefault(); if (SelectedProfile is not null) ProfileList.SelectedItem = SelectedProfile; }
        if (SelectedProfile is not null && MacroItems.ItemsSource != SelectedProfile.Macros) MacroItems.ItemsSource = SelectedProfile.Macros;
    }
    async Task RunMacro(Macro macro)
    {
        if (!_settings.MasterEnabled || !Profiles.Any(p=>p.Enabled&&p.Macros.Contains(macro)&&macro.Enabled)) return;
        if (!ReadPlaybackDelays(out var keyDelay, out var shortcutDelay, out var mouseMoveSpeed, out var mouseClickDelay)) return;
        await _runner.WaitAsync(); _playbackCancellation = new(); try { Diagnostics.Write($"MACRO START name={macro.Name}"); await Dispatcher.InvokeAsync(()=>StatusText.Text=$"Running {macro.Name}..."); foreach(var step in macro.Actions.ToList()) { _playbackCancellation.Token.ThrowIfCancellationRequested(); if(step.Kind=="Delay") await Task.Delay(step.DelayMs, _playbackCancellation.Token); else for(var i=0;i<step.Repeat;i++) { _playbackCancellation.Token.ThrowIfCancellationRequested(); var delay = step.Kind == "Mouse Move" ? Math.Max(0, (int)Math.Round(15d * 100 / mouseMoveSpeed)) : step.Kind.StartsWith("Mouse", StringComparison.Ordinal) ? mouseClickDelay : step.Value.Contains('+') ? shortcutDelay : keyDelay; if (step.Kind.StartsWith("Mouse", StringComparison.Ordinal)) MouseSender.Execute(step); else KeyboardSender.Press(step.Value); await Task.Delay(delay, _playbackCancellation.Token); } } Diagnostics.Write($"MACRO COMPLETE name={macro.Name}"); await Dispatcher.InvokeAsync(()=>StatusText.Text=$"Completed {macro.Name}."); } catch (OperationCanceledException) { await Dispatcher.InvokeAsync(()=>StatusText.Text="Macro stopped safely."); } catch (Exception ex) { Diagnostics.Write($"MACRO ERROR {ex}"); KeyboardSender.ReleaseModifiers(); await Dispatcher.InvokeAsync(()=>StatusText.Text="Macro stopped safely."); } finally { KeyboardSender.ReleaseModifiers(); _playbackCancellation?.Dispose(); _playbackCancellation = null; _runner.Release(); }
    }
    void EmergencyStop() { _playbackCancellation?.Cancel(); KeyboardSender.ReleaseModifiers(); StatusText.Text = "Emergency stop - playback cancelled."; }
    bool ReadPlaybackDelays(out int keyDelay, out int shortcutDelay, out int mouseMoveSpeed, out int mouseClickDelay)
    {
        mouseMoveSpeed = mouseClickDelay = 0; if (!int.TryParse(KeyDelayInput.Text, out keyDelay) || keyDelay < 0 || !int.TryParse(ShortcutDelayInput.Text, out shortcutDelay) || shortcutDelay < 0 || !int.TryParse(MouseMoveDelayInput.Text, out mouseMoveSpeed) || mouseMoveSpeed < 10 || mouseMoveSpeed > 5000 || !int.TryParse(MouseClickDelayInput.Text, out mouseClickDelay) || mouseClickDelay < 0) { shortcutDelay = 0; StatusText.Text = "Use a movement speed from 10% to 5000%; other delays must be zero or more milliseconds."; return false; }
        _settings.NormalKeyDelayMs = keyDelay; _settings.ShortcutDelayMs = shortcutDelay; _settings.MouseMoveSpeedPercent = mouseMoveSpeed; _settings.MouseClickDelayMs = mouseClickDelay; return true;
    }
    void Website_RequestNavigate(object s, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
    void ExitApplication() { Dispatcher.Invoke(() => { _allowClose = true; Close(); }); }
    void Window_Closing(object s, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose) { e.Cancel = true; Hide(); StatusText.Text = "Click Away is running in the tray."; return; }
        _recorder?.Dispose(); _sequenceRecorder?.Dispose(); ReadPlaybackDelays(out _, out _, out _, out _); KeyboardSender.ReleaseModifiers(); SettingsStore.Save(_settings); _hotkeys?.Dispose(); _tray?.Dispose();
    }
}

sealed class ActionDragAdorner : System.Windows.Documents.Adorner
{
    readonly System.Windows.Media.ImageBrush _snapshot; readonly System.Windows.Size _size; System.Windows.Point _position;
    public ActionDragAdorner(System.Windows.UIElement adornedElement, System.Windows.UIElement row) : base(adornedElement)
    {
        _size = row.RenderSize; var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(_size.Width)), Math.Max(1, (int)Math.Ceiling(_size.Height)), 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(row); _snapshot = new System.Windows.Media.ImageBrush(bitmap) { Stretch = System.Windows.Media.Stretch.Fill }; IsHitTestVisible = false;
    }
    public void Move(System.Windows.Point position) { _position = position; InvalidateVisual(); }
    protected override void OnRender(System.Windows.Media.DrawingContext drawingContext)
    {
        var rect = new System.Windows.Rect(_position.X - _size.Width / 2, _position.Y - _size.Height / 2, _size.Width, _size.Height); var lifted = new System.Windows.Rect(rect.X - 3, rect.Y - 3, rect.Width + 6, rect.Height + 6);
        drawingContext.PushOpacity(0.20); drawingContext.DrawRoundedRectangle(System.Windows.Media.Brushes.Black, null, new System.Windows.Rect(lifted.X + 5, lifted.Y + 8, lifted.Width, lifted.Height), 12, 12); drawingContext.Pop(); drawingContext.PushOpacity(0.98); drawingContext.DrawRoundedRectangle(_snapshot, new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(104, 170, 255)), 2), lifted, 10, 10); drawingContext.Pop();
    }
}
