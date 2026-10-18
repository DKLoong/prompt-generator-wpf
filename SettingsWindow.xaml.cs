using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PromptGenerator.Services;

namespace PromptGenerator;

/// <summary>设置弹窗：热键自定义 + 贴边收起开关。</summary>
public partial class SettingsWindow : Window
{
    /// <summary>用户确认后的热键文本（如 "Ctrl+Alt+P"）。</summary>
    public string HotkeyText { get; private set; }

    /// <summary>是否启用贴边收起。</summary>
    public bool DockEnabled { get; private set; }

    /// <summary>是否启用"最近使用置顶"。</summary>
    public bool PinRecentEnabled { get; private set; }

    public SettingsWindow(string currentHotkey, bool dockEnabled, bool pinRecentEnabled)
    {
        InitializeComponent();
        HotkeyText = currentHotkey;
        DockEnabled = dockEnabled;
        PinRecentEnabled = pinRecentEnabled;
        HotkeyBox.Text = currentHotkey;
        DockCheck.IsChecked = dockEnabled;
        PinRecentCheck.IsChecked = pinRecentEnabled;
        // 弹窗不自动聚焦任何控件：等 WPF 默认焦点分配完成后清除，焦点留在窗口（点击热键框才触发全选/按键替换）
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Keyboard.ClearFocus));
        // 焦点不在热键框内时 Esc 关闭窗口；热键框内 Esc 由 PreviewKeyDown 处理（还原文本，不关窗）
        KeyDown += SettingsWindow_KeyDown;
    }

    private void SettingsWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !HotkeyBox.IsKeyboardFocusWithin)
        {
            DialogResult = false;
        }
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        HotkeyBox.SelectAll();
    }

    private void DockCheck_Changed(object sender, RoutedEventArgs e)
    {
        DockEnabled = DockCheck.IsChecked == true;
    }

    private void PinRecentCheck_Changed(object sender, RoutedEventArgs e)
    {
        PinRecentEnabled = PinRecentCheck.IsChecked == true;
    }

    /// <summary>捕获按键组合：直接把按下的键格式化为 "Ctrl+Alt+P"。</summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            HotkeyBox.Text = HotkeyText; // 还原
            ErrorTip.Visibility = Visibility.Collapsed;
            Keyboard.ClearFocus();
            return;
        }

        // 单独按修饰键：只预览当前修饰组合，不记录主键
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            HotkeyBox.Text = ModifiersText(Keyboard.Modifiers) + "…";
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            ShowError("请至少搭配一个修饰键（Ctrl / Alt / Shift）");
            return;
        }

        string? name = KeyToName(key);
        if (name == null)
        {
            ShowError("该按键不支持，请换一个");
            return;
        }

        HotkeyBox.Text = ModifiersText(Keyboard.Modifiers) + name;
        ErrorTip.Visibility = Visibility.Collapsed;
    }

    private static string ModifiersText(ModifierKeys m)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts.Count > 0 ? string.Join("+", parts) + "+" : "";
    }

    /// <summary>Key → 热键文本主键（与 HotkeyManager.TryParse 的解析规则对齐）。</summary>
    private static string? KeyToName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return key.ToString()[1..];
        if (key is >= Key.F1 and <= Key.F12) return key.ToString();
        return key switch
        {
            Key.Escape => "Esc",
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Back => "Back",
            Key.Delete => "Del",
            Key.Insert => "Ins",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PgUp",
            Key.PageDown => "PgDn",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.OemMinus => "-", Key.OemPlus => "=", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemBackslash => "\\", Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/", Key.OemTilde => "`",
            _ => null
        };
    }

    private void ShowError(string msg)
    {
        ErrorTip.Text = msg;
        ErrorTip.Visibility = Visibility.Visible;
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        string text = HotkeyBox.Text.Trim();
        if (!HotkeyManager.TryParse(text, out _, out _))
        {
            ShowError("热键格式无效，请重新按下组合键");
            return;
        }
        HotkeyText = text;
        DialogResult = true;
    }
}
