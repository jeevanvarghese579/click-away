using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KeysAutoclicker;

public abstract class NotifyModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ActionStep : NotifyModel
{
    string _kind = "Key", _value = "A"; int _repeat = 1, _delayMs = 300;
    public string Kind { get => _kind; set { _kind = value; Changed(); Changed(nameof(Display)); } }
    public string Value { get => _value; set { _value = value; Changed(); Changed(nameof(Display)); } }
    public int Repeat { get => _repeat; set { _repeat = Math.Max(1, value); Changed(); Changed(nameof(Display)); } }
    public int DelayMs { get => _delayMs; set { _delayMs = Math.Max(0, value); Changed(); Changed(nameof(Display)); } }
    public string EditorValue
    {
        get => Kind == "Delay" ? DelayMs.ToString() : Value;
        set { if (Kind == "Delay" && int.TryParse(value, out var milliseconds)) DelayMs = milliseconds; else Value = value; Changed(); }
    }
    public string Display => Kind == "Delay" ? $"Wait {DelayMs} ms" : $"{Value} ×{Repeat}";
    public ActionStep Clone() => new() { Kind = Kind, Value = Value, Repeat = Repeat, DelayMs = DelayMs };
}

public sealed class Macro : NotifyModel
{
    string _name = "New shortcut", _trigger = "F1"; bool _enabled = true;
    public string Name { get => _name; set { _name = value; Changed(); } }
    public string Trigger { get => _trigger; set { _trigger = value; Changed(); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Changed(); } }
    public ObservableCollection<ActionStep> Actions { get; set; } = new();
    public string Summary => $"{Actions.Count} action{(Actions.Count == 1 ? "" : "s")}";
    public Macro Clone() => new() { Name = Name + " copy", Trigger = "", Enabled = false, Actions = new ObservableCollection<ActionStep>(Actions.Select(x => x.Clone())) };
}

public sealed class Profile : NotifyModel
{
    string _name = "New profile"; bool _enabled = true;
    public string Name { get => _name; set { _name = value; Changed(); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Changed(); } }
    public ObservableCollection<Macro> Macros { get; set; } = new();
    public Profile Clone() => new() { Name = Name + " copy", Enabled = false, Macros = new ObservableCollection<Macro>(Macros.Select(x => x.Clone())) };
}

public sealed class AppSettings
{
    public bool MasterEnabled { get; set; } = true;
    public bool DarkTheme { get; set; } = true;
    public int NormalKeyDelayMs { get; set; } = 55;
    public int ShortcutDelayMs { get; set; } = 100;
    public int MouseMoveDelayMs { get; set; } = 15;
    public int MouseClickDelayMs { get; set; } = 75;
    public string StartStopRecordingShortcut { get; set; } = "Ctrl+Shift+F9";
    public string PauseResumeRecordingShortcut { get; set; } = "Ctrl+Shift+F10";
    public string EmergencyStopShortcut { get; set; } = "Ctrl+Shift+F12";
    public ObservableCollection<Profile> Profiles { get; set; } = new();
}
