using System.Runtime.InteropServices;
using Pitstop.Core.Profiles;

namespace Pitstop.App;

/// <summary>Registers global hotkeys on a hidden message-only window.</summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000; // holding the keys down doesn't toggle repeatedly

    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager() => CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ });

    /// <summary>Returns false if another app already owns this key combination.</summary>
    public bool TryRegister(Hotkey hotkey, Action action)
    {
        var id = _nextId++;
        if (!RegisterHotKey(Handle, id, (uint)hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
        _actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && _actions.TryGetValue(m.WParam.ToInt32(), out var action)) action();
        else base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
