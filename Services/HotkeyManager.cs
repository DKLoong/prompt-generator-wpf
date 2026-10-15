using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PromptGenerator.Services;

/// <summary>
/// 全局热键管理：注册 / 注销 / 系统冲突检测 / WM_HOTKEY 消息分发。
/// 热键文本格式如 "Ctrl+Alt+P"（修饰键 Ctrl/Alt/Shift/Win + 单个键）。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly int _id;
    private uint _modifiers;
    private uint _vk;
    private bool _registered;

    /// <summary>热键触发（UI 线程）。</summary>
    public event Action? HotkeyPressed;

    public HotkeyManager(Window window, int id = 0x5001)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)
            ?? throw new InvalidOperationException("无法获取窗口句柄源");
        _id = id;
        _source.AddHook(WndProc);
    }

    /// <summary>当前生效的热键文本（未注册为空串）。</summary>
    public string CurrentHotkey { get; private set; } = "";

    /// <summary>尝试注册热键。成功返回 true；失败时 reason 说明原因（含系统冲突）。</summary>
    public bool TryRegister(string hotkeyText, out string reason)
    {
        Unregister();
        if (!TryParse(hotkeyText, out _modifiers, out _vk))
        {
            reason = $"无法解析热键“{hotkeyText}”";
            return false;
        }
        if (!RegisterHotKey(_hwnd, _id, _modifiers, _vk))
        {
            int err = Marshal.GetLastWin32Error();
            reason = err == ErrorHotkeyAlreadyRegistered
                ? $"热键“{hotkeyText}”已被其他程序占用，请更换"
                : $"注册热键失败（Win32 错误码 {err}）";
            return false;
        }
        _registered = true;
        CurrentHotkey = hotkeyText;
        reason = "";
        return true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, _id);
            _registered = false;
            CurrentHotkey = "";
        }
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>解析 "Ctrl+Alt+P"：修饰键（Ctrl/Alt/Shift/Win）+ 单个键。支持 A-Z、0-9、F1-F12 及常见符号键。</summary>
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+');
        if (parts.Length < 2) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows": modifiers |= ModWin; break;
                default: return false;
            }
        }
        string key = parts[^1].Trim();
        if (key.Length == 1)
        {
            char c = key[0];
            if (c is >= 'a' and <= 'z') { vk = (uint)(c - 32); return true; }
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
            return TryPunctKey(c, out vk);
        }
        return TryNamedKey(key, out vk);
    }

    private static bool TryNamedKey(string key, out uint vk)
    {
        vk = key.ToUpperInvariant() switch
        {
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
            "ESC" => 0x1B, "SPACE" => 0x20, "ENTER" => 0x0D, "TAB" => 0x09,
            "BACK" or "BACKSPACE" => 0x08, "DEL" or "DELETE" => 0x2E,
            "INS" or "INSERT" => 0x2D, "HOME" => 0x24, "END" => 0x23,
            "PGUP" => 0x21, "PGDN" => 0x22, "UP" => 0x26, "DOWN" => 0x28,
            "LEFT" => 0x25, "RIGHT" => 0x27,
            _ => 0
        };
        return vk != 0;
    }

    private static bool TryPunctKey(char c, out uint vk)
    {
        vk = c switch
        {
            '-' => 0xBD, '=' => 0xBB, '[' => 0xDB, ']' => 0xDD,
            '\\' => 0xDC, ';' => 0xBA, '\'' => 0xDE, ',' => 0xBC,
            '.' => 0xBE, '/' => 0xBF, '`' => 0xC0,
            _ => 0
        };
        return vk != 0;
    }
}
