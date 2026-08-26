using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace KeysAutoclicker;
public static class SettingsStore
{
    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "Data", "settings.json");
    public static AppSettings Load() { try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); } catch { return new(); } }
    public static void Save(AppSettings settings) { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
}
public static class Diagnostics
{
    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "Data", "clickaway.log");
    public static void Write(string message)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); } catch { }
    }
}

public static class KeyNotation
{
    public const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8;
    public static string Capture(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return Format(key, Keyboard.Modifiers);
    }
    public static string Format(Key key, ModifierKeys mods)
    {
        if (IsModifier(key)) return "";
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl"); if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt"); if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift"); if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Friendly(key)); return string.Join("+", parts);
    }
    public static bool TryParse(string text, out uint modifiers, out Key key)
    {
        modifiers = 0; key = Key.None; foreach (var p in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= ModControl; else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ModAlt; else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ModShift; else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) || p.Equals("Windows", StringComparison.OrdinalIgnoreCase)) modifiers |= ModWin; else if (!Enum.TryParse<Key>(p, true, out key) || key == Key.None) return false; }
        return key != Key.None;
    }
    public static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    static string Friendly(Key key) => key.ToString() switch { "LeftCtrl" or "RightCtrl" => "Ctrl", "LeftAlt" or "RightAlt" => "Alt", "LeftShift" or "RightShift" => "Shift", "LWin" or "RWin" => "Win", _ => key.ToString() };
}

public sealed class ShortcutRecorder : IDisposable
{
    const int HookType = 13, KeyDown = 0x0100, SysKeyDown = 0x0104, KeyUp = 0x0101, SysKeyUp = 0x0105, Injected = 0x10;
    readonly Action<string> _completed; readonly HookProc _callback; readonly HashSet<uint> _down = new(); IntPtr _hook;
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct Kbd { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    public ShortcutRecorder(Action<string> completed) { _completed = completed; _callback = Hook; _hook = SetWindowsHookEx(HookType, _callback, GetModuleHandle(null), 0); }
    public bool IsActive => _hook != IntPtr.Zero;
    IntPtr Hook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _hook == IntPtr.Zero) return CallNextHookEx(_hook, code, message, data);
        var info = Marshal.PtrToStructure<Kbd>(data); if ((info.flags & Injected) != 0) return CallNextHookEx(_hook, code, message, data);
        var type = message.ToInt32();
        if (type is KeyUp or SysKeyUp) { _down.Remove(info.vkCode); return new IntPtr(1); }
        if (type is not (KeyDown or SysKeyDown)) return new IntPtr(1);
        if (info.vkCode == 0x1B) { Dispose(); return new IntPtr(1); } // Escape cancels recording
        _down.Add(info.vkCode); var text = KeyNotation.Format(KeyInterop.KeyFromVirtualKey((int)info.vkCode), Modifiers());
        if (!string.IsNullOrEmpty(text)) { Dispose(); _completed(text); }
        return new IntPtr(1);
    }
    ModifierKeys Modifiers()
    {
        ModifierKeys result = ModifierKeys.None;
        if (_down.Overlaps(new uint[] { 0xA2, 0xA3, 0x11 })) result |= ModifierKeys.Control;
        if (_down.Overlaps(new uint[] { 0xA4, 0xA5, 0x12 })) result |= ModifierKeys.Alt;
        if (_down.Overlaps(new uint[] { 0xA0, 0xA1, 0x10 })) result |= ModifierKeys.Shift;
        if (_down.Overlaps(new uint[] { 0x5B, 0x5C })) result |= ModifierKeys.Windows;
        return result;
    }
    public void Dispose() { if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; } }
}

public readonly record struct RecordedInput(long Sequence, long Timestamp, string Value);

public sealed class SequenceRecorder : IDisposable
{
    const int HookType = 13, MouseHookType = 14, KeyDown = 0x0100, SysKeyDown = 0x0104, KeyUp = 0x0101, SysKeyUp = 0x0105, Injected = 0x10;
    const int MouseMove = 0x0200, LeftDown = 0x0201, RightDown = 0x0204, MiddleDown = 0x0207, Wheel = 0x020A;
    readonly Action<RecordedInput> _recorded; readonly Action _stopped; readonly Func<string, bool>? _ignore; readonly bool _suppress; readonly HookProc _callback, _mouseCallback; readonly HashSet<uint> _down = new(); IntPtr _hook, _mouseHook; int _lastX = int.MinValue, _lastY; long _lastMoveTick, _sequence;
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct Kbd { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public Point pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    public SequenceRecorder(Action<RecordedInput> recorded, Action stopped, bool suppress = true, Func<string, bool>? ignore = null) { _recorded = recorded; _stopped = stopped; _suppress = suppress; _ignore = ignore; _callback = Hook; _mouseCallback = MouseHook; _hook = SetWindowsHookEx(HookType, _callback, GetModuleHandle(null), 0); _mouseHook = SetWindowsHookEx(MouseHookType, _mouseCallback, GetModuleHandle(null), 0); }
    public bool IsActive => _hook != IntPtr.Zero && _mouseHook != IntPtr.Zero;
    IntPtr Hook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _hook == IntPtr.Zero) return CallNextHookEx(_hook, code, message, data);
        var info = Marshal.PtrToStructure<Kbd>(data); if ((info.flags & Injected) != 0) return CallNextHookEx(_hook, code, message, data);
        var type = message.ToInt32();
        if (type is KeyUp or SysKeyUp) { _down.Remove(info.vkCode); return Next(code, message, data); }
        if (type is not (KeyDown or SysKeyDown)) return Next(code, message, data);
        if (info.vkCode == 0x1B) { Dispose(); _stopped(); return Next(code, message, data); } // Esc ends a sequence
        if (!_down.Add(info.vkCode)) return Next(code, message, data);
        var text = KeyNotation.Format(KeyInterop.KeyFromVirtualKey((int)info.vkCode), Modifiers());
        if (!string.IsNullOrEmpty(text) && !(_ignore?.Invoke(text) ?? false)) Emit(text);
        return Next(code, message, data);
    }
    IntPtr Next(int code, IntPtr message, IntPtr data) => _suppress ? new IntPtr(1) : CallNextHookEx(_hook, code, message, data);
    IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _mouseHook == IntPtr.Zero) return CallNextHookEx(_mouseHook, code, message, data);
        var info = Marshal.PtrToStructure<Mouse>(data); if ((info.flags & 1) != 0) return CallNextHookEx(_mouseHook, code, message, data);
        var type = message.ToInt32(); string? value = type switch
        {
            LeftDown => $"MouseLeftClick:{info.pt.x},{info.pt.y}", RightDown => $"MouseRightClick:{info.pt.x},{info.pt.y}", MiddleDown => $"MouseMiddleClick:{info.pt.x},{info.pt.y}",
            Wheel => $"MouseWheel:{(short)(info.mouseData >> 16)}", _ => null
        };
        if (type == MouseMove)
        {
            var now = Environment.TickCount64;
            if (now - _lastMoveTick >= 15 && (Math.Abs(info.pt.x - _lastX) + Math.Abs(info.pt.y - _lastY) >= 3)) { _lastX = info.pt.x; _lastY = info.pt.y; _lastMoveTick = now; value = $"MouseMove:{info.pt.x},{info.pt.y}"; }
        }
        if (value is not null) Emit(value);
        return CallNextHookEx(_mouseHook, code, message, data);
    }
    void Emit(string value) => _recorded(new RecordedInput(Interlocked.Increment(ref _sequence), Environment.TickCount64, value));
    ModifierKeys Modifiers()
    {
        ModifierKeys result = ModifierKeys.None;
        if (_down.Overlaps(new uint[] { 0xA2, 0xA3, 0x11 })) result |= ModifierKeys.Control;
        if (_down.Overlaps(new uint[] { 0xA4, 0xA5, 0x12 })) result |= ModifierKeys.Alt;
        if (_down.Overlaps(new uint[] { 0xA0, 0xA1, 0x10 })) result |= ModifierKeys.Shift;
        if (_down.Overlaps(new uint[] { 0x5B, 0x5C })) result |= ModifierKeys.Windows;
        return result;
    }
    public void Dispose() { if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; } if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; } }
}

public sealed class GlobalHotkeys : IDisposable
{
    const int WmHotkey = 0x0312;
    readonly HwndSource _source; readonly Action<Macro> _action; readonly Dictionary<int, Action> _registered = new(); int _nextId = 1000;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    public GlobalHotkeys(Window window, Action<Macro> action)
    {
        _action = action; _source = (HwndSource)PresentationSource.FromVisual(window)!; _source.AddHook(WndProc);
    }
    public List<string> Register(IEnumerable<Macro> macros, IReadOnlyDictionary<string, Action>? controls = null)
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id); _registered.Clear(); _nextId = 1000;
        var errors = new List<string>();
        foreach (var item in (controls ?? new Dictionary<string, Action>()).Select(x => (Shortcut: x.Key, Name: "Click Away control", Action: x.Value)).Concat(macros.Select(m => (Shortcut: m.Trigger, Name: m.Name, Action: (Action)(() => _action(m))))))
        {
            if (!KeyNotation.TryParse(item.Shortcut, out var modifiers, out var key)) { errors.Add($"{item.Name}: invalid shortcut"); continue; }
            var id = _nextId++;
            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (!RegisterHotKey(_source.Handle, id, modifiers, vk)) { var error = Marshal.GetLastWin32Error(); errors.Add($"{item.Shortcut} is unavailable (already used by Windows or another app)."); Diagnostics.Write($"REGISTER FAILED shortcut={item.Shortcut} vk={vk} error={error}"); }
            else { _registered[id] = item.Action; Diagnostics.Write($"REGISTERED shortcut={item.Shortcut} id={id} vk={vk}"); }
        }
        return errors;
    }
    IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var action)) { Diagnostics.Write($"HOTKEY RECEIVED id={wParam}"); action(); handled = true; }
        return IntPtr.Zero;
    }
    public void Dispose() { foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id); _source.RemoveHook(WndProc); }
}

public static class KeyboardSender
{
    const uint KeyUp = 2; static readonly IntPtr Marker = new(0x434C4943); // CLIC marker prevents hook-based recursion too
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
    // INPUT's union must retain the mouse member so its x64 size matches Win32's
    // 40-byte INPUT structure. A keyboard-only union causes SendInput error 87.
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    static INPUT Input(Key key, bool up = false) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)KeyInterop.VirtualKeyFromKey(key), dwFlags = up ? KeyUp : 0, dwExtraInfo = Marker } } };
    public static void Press(string notation)
    {
        if (!KeyNotation.TryParse(notation, out var mods, out var key)) return; var modifiers = new List<Key>();
        if ((mods & KeyNotation.ModControl) != 0) modifiers.Add(Key.LeftCtrl); if ((mods & KeyNotation.ModAlt) != 0) modifiers.Add(Key.LeftAlt); if ((mods & KeyNotation.ModShift) != 0) modifiers.Add(Key.LeftShift); if ((mods & KeyNotation.ModWin) != 0) modifiers.Add(Key.LWin);
        var inputs = new List<INPUT>(); inputs.AddRange(modifiers.Select(x => Input(x))); inputs.Add(Input(key)); inputs.Add(Input(key, true)); inputs.AddRange(modifiers.AsEnumerable().Reverse().Select(x => Input(x, true)));
        var result = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        Diagnostics.Write($"SENDINPUT action={notation} sent={result}/{inputs.Count} error={Marshal.GetLastWin32Error()}");
    }
    public static void ReleaseModifiers() => SendInput(8, new[] { Input(Key.LeftCtrl, true), Input(Key.RightCtrl, true), Input(Key.LeftAlt, true), Input(Key.RightAlt, true), Input(Key.LeftShift, true), Input(Key.RightShift, true), Input(Key.LWin, true), Input(Key.RWin, true) }, Marshal.SizeOf<INPUT>());
}

public static class MouseSender
{
    const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, MiddleDown = 0x0020, MiddleUp = 0x0040, Wheel = 0x0800;
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    public static void Execute(ActionStep step)
    {
        if (step.Kind == "Mouse Move") { if (TryPoint(step.Value, out var x, out var y)) SetCursorPos(x, y); return; }
        if (step.Kind.EndsWith("Click") && TryPoint(step.Value, out var clickX, out var clickY)) SetCursorPos(clickX, clickY);
        if (step.Kind == "Mouse Left Click") mouse_event(LeftDown | LeftUp, 0, 0, 0, UIntPtr.Zero);
        else if (step.Kind == "Mouse Double Click") { mouse_event(LeftDown | LeftUp, 0, 0, 0, UIntPtr.Zero); mouse_event(LeftDown | LeftUp, 0, 0, 0, UIntPtr.Zero); }
        else if (step.Kind == "Mouse Right Click") mouse_event(RightDown | RightUp, 0, 0, 0, UIntPtr.Zero);
        else if (step.Kind == "Mouse Middle Click") mouse_event(MiddleDown | MiddleUp, 0, 0, 0, UIntPtr.Zero);
        else if (step.Kind == "Mouse Wheel" && int.TryParse(step.Value, out var delta)) mouse_event(Wheel, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }
    static bool TryPoint(string text, out int x, out int y)
    {
        x = y = 0; var parts = text.Split(','); return parts.Length == 2 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
    }
}

public sealed class TrayStatusIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon; readonly Window _window; readonly Action _exit;
    public TrayStatusIcon(Window window, bool enabled, Action exit) { _window = window; _exit = exit; var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Show Click Away", null, (_, _) => ShowWindow()); menu.Items.Add("Exit", null, (_, _) => _exit()); _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true }; _icon.DoubleClick += (_, _) => ShowWindow(); SetMasterState(enabled); }
    public void SetRecording(bool recording)
    {
        _icon.Icon?.Dispose(); _icon.Icon = CreateIcon(recording ? Drawing.Color.FromArgb(220, 62, 72) : Drawing.Color.FromArgb(42, 164, 96));
        _icon.Text = recording ? "Click Away - Recording" : "Click Away - ON";
    }
    public void SetMasterState(bool enabled) { _icon.Icon?.Dispose(); _icon.Icon = CreateIcon(enabled ? Drawing.Color.FromArgb(42, 164, 96) : Drawing.Color.FromArgb(190, 65, 72)); _icon.Text = enabled ? "Click Away — ON" : "Click Away — OFF"; }
    void ShowWindow() => _window.Dispatcher.Invoke(() => { _window.Show(); _window.WindowState = WindowState.Normal; _window.Activate(); });
    static Drawing.Icon CreateIcon(Drawing.Color color) { using var b = new Drawing.Bitmap(32,32); using var g = Drawing.Graphics.FromImage(b); g.Clear(Drawing.Color.Transparent); using var brush = new Drawing.SolidBrush(color); using var pen = new Drawing.Pen(Drawing.Color.White,2); g.FillEllipse(brush,3,3,26,26); g.DrawEllipse(pen,3,3,26,26); using var h = Drawing.Icon.FromHandle(b.GetHicon()); return (Drawing.Icon)h.Clone(); }
    public void Dispose() { _icon.Visible = false; _icon.Icon?.Dispose(); _icon.Dispose(); }
}
