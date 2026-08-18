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

public sealed class SequenceRecorder : IDisposable
{
    const int HookType = 13, KeyDown = 0x0100, SysKeyDown = 0x0104, KeyUp = 0x0101, SysKeyUp = 0x0105, Injected = 0x10;
    readonly Action<string> _recorded; readonly Action _stopped; readonly HookProc _callback; readonly HashSet<uint> _down = new(); IntPtr _hook;
    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct Kbd { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    public SequenceRecorder(Action<string> recorded, Action stopped) { _recorded = recorded; _stopped = stopped; _callback = Hook; _hook = SetWindowsHookEx(HookType, _callback, GetModuleHandle(null), 0); }
    public bool IsActive => _hook != IntPtr.Zero;
    IntPtr Hook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _hook == IntPtr.Zero) return CallNextHookEx(_hook, code, message, data);
        var info = Marshal.PtrToStructure<Kbd>(data); if ((info.flags & Injected) != 0) return CallNextHookEx(_hook, code, message, data);
        var type = message.ToInt32();
        if (type is KeyUp or SysKeyUp) { _down.Remove(info.vkCode); return new IntPtr(1); }
        if (type is not (KeyDown or SysKeyDown)) return new IntPtr(1);
        if (info.vkCode == 0x1B) { Dispose(); _stopped(); return new IntPtr(1); } // Esc ends a sequence
        if (!_down.Add(info.vkCode)) return new IntPtr(1);
        var text = KeyNotation.Format(KeyInterop.KeyFromVirtualKey((int)info.vkCode), Modifiers());
        if (!string.IsNullOrEmpty(text)) _recorded(text);
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

public sealed class GlobalHotkeys : IDisposable
{
    const int WmHotkey = 0x0312;
    readonly HwndSource _source; readonly Action<Macro> _action; readonly Dictionary<int, Macro> _registered = new(); int _nextId = 1000;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    public GlobalHotkeys(Window window, Action<Macro> action)
    {
        _action = action; _source = (HwndSource)PresentationSource.FromVisual(window)!; _source.AddHook(WndProc);
    }
    public List<string> Register(IEnumerable<Macro> macros)
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id); _registered.Clear(); _nextId = 1000;
        var errors = new List<string>();
        foreach (var macro in macros)
        {
            if (!KeyNotation.TryParse(macro.Trigger, out var modifiers, out var key)) { errors.Add($"{macro.Name}: invalid trigger"); continue; }
            var id = _nextId++;
            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (!RegisterHotKey(_source.Handle, id, modifiers, vk)) { var error = Marshal.GetLastWin32Error(); errors.Add($"{macro.Trigger} is unavailable (already used by Windows or another app)."); Diagnostics.Write($"REGISTER FAILED trigger={macro.Trigger} vk={vk} error={error}"); }
            else { _registered[id] = macro; Diagnostics.Write($"REGISTERED trigger={macro.Trigger} id={id} vk={vk}"); }
        }
        return errors;
    }
    IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var macro)) { Diagnostics.Write($"HOTKEY RECEIVED id={wParam} macro={macro.Name}"); _action(macro); handled = true; }
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

public sealed class TrayStatusIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon; readonly Window _window;
    public TrayStatusIcon(Window window, bool enabled) { _window = window; var menu = new Forms.ContextMenuStrip(); menu.Items.Add("Show Click Away", null, (_, _) => ShowWindow()); menu.Items.Add("Exit", null, (_, _) => _window.Dispatcher.Invoke(_window.Close)); _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true }; _icon.DoubleClick += (_, _) => ShowWindow(); SetMasterState(enabled); }
    public void SetMasterState(bool enabled) { _icon.Icon?.Dispose(); _icon.Icon = CreateIcon(enabled ? Drawing.Color.FromArgb(42, 164, 96) : Drawing.Color.FromArgb(190, 65, 72)); _icon.Text = enabled ? "Click Away — ON" : "Click Away — OFF"; }
    void ShowWindow() => _window.Dispatcher.Invoke(() => { _window.Show(); _window.WindowState = WindowState.Normal; _window.Activate(); });
    static Drawing.Icon CreateIcon(Drawing.Color color) { using var b = new Drawing.Bitmap(32,32); using var g = Drawing.Graphics.FromImage(b); g.Clear(Drawing.Color.Transparent); using var brush = new Drawing.SolidBrush(color); using var pen = new Drawing.Pen(Drawing.Color.White,2); g.FillEllipse(brush,3,3,26,26); g.DrawEllipse(pen,3,3,26,26); using var h = Drawing.Icon.FromHandle(b.GetHicon()); return (Drawing.Icon)h.Clone(); }
    public void Dispose() { _icon.Visible = false; _icon.Icon?.Dispose(); _icon.Dispose(); }
}
