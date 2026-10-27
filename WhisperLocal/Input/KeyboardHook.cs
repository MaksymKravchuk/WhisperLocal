using System.ComponentModel;
using System.Runtime.InteropServices;
using static WhisperLocal.Native.NativeMethods;

namespace WhisperLocal.Input;

/// <summary>
/// Push-to-talk hotkey via WH_KEYBOARD_LL (RegisterHotKey has no key-up).
/// Supports a single key ("RMenu") or a combination ("LMenu+LWin"): active while all keys are held,
/// released when any of them goes up, cancelled when another key is pressed meanwhile.
/// Must be created on a thread with a message loop; events are posted to the given context.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private static readonly Dictionary<string, Keys> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alt"] = Keys.LMenu,
        ["LeftAlt"] = Keys.LMenu,
        ["RightAlt"] = Keys.RMenu,
        ["AltGr"] = Keys.RMenu,
        ["Ctrl"] = Keys.LControlKey,
        ["Control"] = Keys.LControlKey,
        ["LeftCtrl"] = Keys.LControlKey,
        ["RightCtrl"] = Keys.RControlKey,
        ["Shift"] = Keys.LShiftKey,
        ["LeftShift"] = Keys.LShiftKey,
        ["RightShift"] = Keys.RShiftKey,
        ["Win"] = Keys.LWin,
        ["LeftWin"] = Keys.LWin,
        ["RightWin"] = Keys.RWin,
        ["ScrollLock"] = Keys.Scroll,
        ["CapsLock"] = Keys.CapsLock,
    };

    private static readonly Dictionary<Keys, string> DisplayNames = new()
    {
        [Keys.LMenu] = "Left Alt",
        [Keys.RMenu] = "Right Alt",
        [Keys.LControlKey] = "Left Ctrl",
        [Keys.RControlKey] = "Right Ctrl",
        [Keys.LShiftKey] = "Left Shift",
        [Keys.RShiftKey] = "Right Shift",
        [Keys.LWin] = "Left Win",
        [Keys.RWin] = "Right Win",
        [Keys.Apps] = "Menu",
        [Keys.Scroll] = "Scroll Lock",
    };

    // Unassigned virtual key, sent to "mask" a lone Win/Alt release (Start menu / menu bar)
    private const ushort VK_MASK = 0xE8;

    private readonly LowLevelKeyboardProc _proc; // keep the delegate alive
    private readonly SynchronizationContext _context;
    private IntPtr _hook;
    private int[] _keys = [];
    private bool _active;

    public bool SuppressKey { get; set; }

    public event Action? HotkeyDown;
    public event Action? HotkeyUp;

    /// <summary>Another key was pressed while the hotkey was held (e.g. Ctrl+Win+D).</summary>
    public event Action? HotkeyCancel;

    public KeyboardHook(SynchronizationContext context)
    {
        _context = context;
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed");
    }

    public void SetHotkey(int[] virtualKeys)
    {
        _keys = virtualKeys;
        _active = false;
    }

    /// <summary>Parses "RMenu", "LMenu+LWin", "Ctrl+Win" etc. into virtual key codes.</summary>
    public static int[] ParseHotkey(string text)
    {
        var keys = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseKey)
            .Distinct()
            .ToArray();
        if (keys.Length == 0)
            throw new ArgumentException("Hotkey is empty");
        return keys;
    }

    /// <summary>Human-readable form, e.g. "Left Alt + Left Win".</summary>
    public static string Describe(string text)
    {
        try
        {
            return string.Join(" + ", ParseHotkey(text).Select(vk =>
                DisplayNames.TryGetValue((Keys)vk, out var name) ? name : ((Keys)vk).ToString()));
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static int ParseKey(string name)
    {
        if (Aliases.TryGetValue(name, out var alias))
            return (int)alias;
        // Keys also contains modifier flags (Alt = 0x40000 etc.); only plain virtual keys are valid
        if (Enum.TryParse<Keys>(name, true, out var key) && key != Keys.None && (int)key <= 0xFF)
            return (int)key;
        throw new ArgumentException($"Unknown hotkey key '{name}'. Use System.Windows.Forms.Keys names joined with '+', e.g. RMenu, LMenu+LWin, LControlKey+LWin.");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _keys.Length == 0)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if ((info.flags & LLKHF_INJECTED) != 0)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        var vk = (int)info.vkCode;
        var msg = (int)wParam;
        var isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isUp = msg is WM_KEYUP or WM_SYSKEYUP;

        if (Array.IndexOf(_keys, vk) >= 0)
        {
            // Swallowing a modifier (or any key of a combo) would break other shortcuts like Alt+Tab,
            // so only a single non-modifier key is suppressed; modifiers are masked instead
            var suppress = SuppressKey && _keys.Length == 1 && !IsModifier(vk);

            // Auto-repeat re-sends key-down while held; _active filters it out
            if (isDown && !_active && OtherKeysHeld(vk))
            {
                _active = true;
                _context.Post(_ =>
                {
                    if (!suppress)
                        SendMaskKey();
                    HotkeyDown?.Invoke();
                }, null);
            }
            else if (isUp && _active)
            {
                _active = false;
                _context.Post(_ => HotkeyUp?.Invoke(), null);
            }

            if (suppress)
                return 1;
        }
        else if (isDown && _active)
        {
            _active = false;
            _context.Post(_ => HotkeyCancel?.Invoke(), null);
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Physical state of the other hotkey keys. Asking the OS (instead of tracking downs/ups)
    /// avoids stuck keys when a key-up is missed, e.g. after Win+L.
    /// </summary>
    private bool OtherKeysHeld(int currentVk)
    {
        foreach (var k in _keys)
        {
            if (k != currentVk && (GetAsyncKeyState(k) & 0x8000) == 0)
                return false;
        }
        return true;
    }

    private static bool IsModifier(int vk) =>
        (Keys)vk is Keys.LMenu or Keys.RMenu or Keys.LControlKey or Keys.RControlKey
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LWin or Keys.RWin;

    private static void SendMaskKey()
    {
        INPUT Key(bool up) => new()
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MASK, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
        };
        INPUT[] inputs = [Key(false), Key(true)];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
