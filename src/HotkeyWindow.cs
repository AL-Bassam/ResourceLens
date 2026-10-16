using System.Runtime.InteropServices;

namespace ResourceLens;

/// <summary>Global hotkey Ctrl+Shift+O (works while a game has focus) to show / hide the overlay.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const int Id = 0x524C; // "RL"
    const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_O = 0x4F;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    readonly Action _onPress;
    bool _registered;

    public HotkeyWindow(Action onPress)
    {
        _onPress = onPress;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // message-only window
    }

    public void SetEnabled(bool on)
    {
        if (on && !_registered) _registered = RegisterHotKey(Handle, Id, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_O);
        else if (!on && _registered) { UnregisterHotKey(Handle, Id); _registered = false; }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == Id) _onPress();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        SetEnabled(false);
        DestroyHandle();
    }
}
