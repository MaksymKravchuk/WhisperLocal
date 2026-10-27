using System.Runtime.InteropServices;
using WhisperLocal.Config;
using WhisperLocal.Native;
using static WhisperLocal.Native.NativeMethods;

namespace WhisperLocal.Output;

/// <summary>Inserts text into the active window via clipboard + Ctrl+V. Must run on an STA (UI) thread.</summary>
internal sealed class TextInserter
{
    public async Task InsertAsync(string text, ForegroundApp? target, InsertConfig cfg)
    {
        var current = ForegroundApp.Capture();
        if (target != null && current.Hwnd != target.Hwnd)
            Log.Info($"Foreground window changed during processing ({target.ProcessName} -> {current.ProcessName}), inserting into the current one");

        if (!Environment.IsPrivilegedProcess && current.IsElevated() != false)
            Log.Warn($"Target process '{current.ProcessName}' is (probably) elevated: UIPI may block the paste. Run WhisperLocal as administrator to dictate into elevated windows.");

        var saved = cfg.RestoreClipboard ? SnapshotClipboard() : null;

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        // Keep dictated text out of Win+V clipboard history
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(data, true, 10, 30);

        await WaitForModifiersReleasedAsync();
        SendCtrlV();

        if (saved == null)
            return;

        // The target app reads the clipboard asynchronously after Ctrl+V
        await Task.Delay(cfg.RestoreDelayMs);
        try
        {
            if (saved.GetFormats().Length == 0)
                Clipboard.Clear();
            else
                Clipboard.SetDataObject(saved, true, 10, 30);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to restore the clipboard: {ex.Message}");
        }
    }

    private static DataObject? SnapshotClipboard()
    {
        try
        {
            var source = Clipboard.GetDataObject();
            if (source == null)
                return null;
            var copy = new DataObject();
            foreach (var format in source.GetFormats(false))
            {
                try
                {
                    var value = source.GetData(format, false);
                    if (value != null)
                        copy.SetData(format, false, value);
                }
                catch
                {
                    // Some formats (delay-rendered, private) cannot be read; skip them
                }
            }
            return copy;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read the clipboard: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// With a combination hotkey a modifier may still be held (e.g. Alt released after Win),
    /// which would turn Ctrl+V into Ctrl+Alt+V. Wait briefly until all modifiers are up.
    /// </summary>
    private static async Task WaitForModifiersReleasedAsync()
    {
        int[] modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C]; // Shift, Ctrl, Alt, LWin, RWin
        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
        while (modifiers.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0))
        {
            if (DateTime.UtcNow > deadline)
            {
                Log.Warn("Modifier keys are still held, pasting anyway");
                return;
            }
            await Task.Delay(20);
        }
    }

    private static void SendCtrlV()
    {
        INPUT Key(ushort vk, bool up) => new()
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
        };

        INPUT[] inputs = [Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true)];
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            Log.Warn($"SendInput sent {sent} of {inputs.Length} events (error {Marshal.GetLastWin32Error()}), the paste was probably blocked");
    }
}
