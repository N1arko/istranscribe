using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Interop;
using IsTranscribe.App.Strings;
using IsTranscribe.Host.Settings;

namespace IsTranscribe.App.Lifecycle;

[SupportedOSPlatform("windows")]
public sealed class GlobalHotkeyRuntimeManager : IDisposable
{
    private const int HwndMessage = -3;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    private readonly Dictionary<int, GlobalHotkeyAction> _registrations = [];
    private HwndSource? _source;
    private bool _disposed;

    public GlobalHotkeyRuntimeManager()
    {
    }

    public event EventHandler<GlobalHotkeyAction>? Triggered;

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#hotkeys.runtime
    public bool Apply(HotkeySettings settings, out string error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ClearRegistrations();
        var registrations = EnumerateRegistrations(settings).ToArray();
        if (registrations.Length == 0)
        {
            error = string.Empty;
            return true;
        }

        var source = EnsureSource();
        var nextId = 0x5100;

        foreach (var registration in registrations)
        {
            if (!TryParseGesture(registration.Gesture, out var modifiers, out var virtualKey))
            {
                ClearRegistrations();
                error = string.Format(LocalizationManager.Instance["Hotkey_UnsupportedGesture"], registration.Gesture);
                return false;
            }

            var id = nextId++;
            if (!RegisterHotKey(source.Handle, id, modifiers, virtualKey))
            {
                ClearRegistrations();
                error = string.Format(LocalizationManager.Instance["Hotkey_RegistrationFailed"], registration.Gesture);
                return false;
            }

            _registrations[id] = registration.Action;
        }

        error = string.Empty;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearRegistrations();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private IEnumerable<(string Gesture, GlobalHotkeyAction Action)> EnumerateRegistrations(HotkeySettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ForceRecordToggle))
        {
            yield return (settings.ForceRecordToggle, GlobalHotkeyAction.ForceRecordToggle);
        }

        if (!string.IsNullOrWhiteSpace(settings.PrivacyPauseToggle))
        {
            yield return (settings.PrivacyPauseToggle, GlobalHotkeyAction.PrivacyPauseToggle);
        }

        if (!string.IsNullOrWhiteSpace(settings.DiscardCurrent))
        {
            yield return (settings.DiscardCurrent, GlobalHotkeyAction.DiscardCurrent);
        }

        if (!string.IsNullOrWhiteSpace(settings.OpenMainWindow))
        {
            yield return (settings.OpenMainWindow, GlobalHotkeyAction.OpenMainWindow);
        }
    }

    private void ClearRegistrations()
    {
        if (_source is null)
        {
            _registrations.Clear();
            return;
        }

        foreach (var id in _registrations.Keys.ToArray())
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _registrations.Clear();
    }

    // @spec spec://modules/app/FEAT-003-manual-recording-controls-and-tray#hotkeys.runtime
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && _registrations.TryGetValue(wParam.ToInt32(), out var action))
        {
            Triggered?.Invoke(this, action);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private HwndSource EnsureSource()
    {
        if (_source is not null)
        {
            return _source;
        }

        var parameters = new HwndSourceParameters("IsTranscribe.GlobalHotkeys")
        {
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(HwndMessage),
            WindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        return _source;
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

        if (!Enum.TryParse(parts[^1], ignoreCase: true, out System.Windows.Input.Key key))
        {
            key = parts[^1] switch
            {
                "0" => System.Windows.Input.Key.D0,
                "1" => System.Windows.Input.Key.D1,
                "2" => System.Windows.Input.Key.D2,
                "3" => System.Windows.Input.Key.D3,
                "4" => System.Windows.Input.Key.D4,
                "5" => System.Windows.Input.Key.D5,
                "6" => System.Windows.Input.Key.D6,
                "7" => System.Windows.Input.Key.D7,
                "8" => System.Windows.Input.Key.D8,
                "9" => System.Windows.Input.Key.D9,
                _ => System.Windows.Input.Key.None
            };
        }

        if (key == System.Windows.Input.Key.None)
        {
            return false;
        }

        virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

public enum GlobalHotkeyAction
{
    ForceRecordToggle,
    PrivacyPauseToggle,
    DiscardCurrent,
    OpenMainWindow
}
