using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RetroClip.Services;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x72C1;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkR = 0x52;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _registered;

    public string RegisteredChord { get; private set; } = "Ctrl+Shift+R";

    public event Action? HotkeyPressed;

    public HotkeyService(Window window)
    {
        _window = window;
    }

    public void Register()
    {
        var helper = new WindowInteropHelper(_window);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);

        _registered = RegisterHotKey(helper.Handle, HotkeyId, ModControl | ModShift, VkR);
        if (!_registered)
        {
            throw new InvalidOperationException("Could not register Ctrl+Shift+R hotkey. It may already be in use.");
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            var helper = new WindowInteropHelper(_window);
            if (helper.Handle != IntPtr.Zero)
            {
                UnregisterHotKey(helper.Handle, HotkeyId);
            }

            _registered = false;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmHotkey = 0x0312;
        if (msg == wmHotkey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
