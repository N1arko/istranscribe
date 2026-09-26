using System.Runtime.InteropServices;
using System.Windows.Interop;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyConverter = System.Windows.Input.KeyConverter;
using KeyInterop = System.Windows.Input.KeyInterop;
using Keyboard = System.Windows.Input.Keyboard;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using Window = System.Windows.Window;

namespace IsTranscribe.App.Windows;

internal static class GlobalHotkeyProbe
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    public static bool TryBuildGesture(KeyEventArgs e, out string gesture)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            gesture = string.Empty;
            return false;
        }

        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(NormalizeKeyName(key));
        gesture = string.Join("+", parts);
        return true;
    }

    // @spec spec://modules/app/FEAT-001-first-run-setup-and-settings#settings.general.hotkeys
    public static bool TryValidateRegistration(Window window, IEnumerable<string?> gestures, out string error)
    {
        var uniqueGestures = gestures
            .Where(static gesture => !string.IsNullOrWhiteSpace(gesture))
            .Select(static gesture => gesture!)
            .ToArray();

        var duplicates = uniqueGestures
            .GroupBy(static gesture => gesture, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicates is not null)
        {
            error = $"Hotkey conflict: {duplicates.Key}.";
            return false;
        }

        var handle = new WindowInteropHelper(window).EnsureHandle();
        var registrations = new List<int>();
        var nextId = 0x7000;

        try
        {
            foreach (var gesture in uniqueGestures)
            {
                if (!TryParseGesture(gesture, out var modifiers, out var virtualKey))
                {
                    error = $"Unsupported hotkey: {gesture}.";
                    return false;
                }

                var id = nextId++;
                if (!RegisterHotKey(handle, id, modifiers, virtualKey))
                {
                    error = $"Windows could not register hotkey {gesture}. It may already be in use by another application.";
                    return false;
                }

                registrations.Add(id);
            }

            error = string.Empty;
            return true;
        }
        finally
        {
            foreach (var id in registrations)
            {
                UnregisterHotKey(handle, id);
            }
        }
    }

    private static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts[..^1])
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        var converter = new KeyConverter();
        if (converter.ConvertFromString(parts[^1]) is not Key key)
        {
            return false;
        }

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0 && virtualKey != WmHotKey;
    }

    private static string NormalizeKeyName(Key key) => key switch
    {
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        _ => key.ToString()
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
