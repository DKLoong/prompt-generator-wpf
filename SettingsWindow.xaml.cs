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
        // 弹窗打开后焦点留在窗口本身（不自动聚焦热键框）：点击热键框才进入"全选 + 按键捕获"态
        Loaded += (_, _) =>
        {
            Focus();
            Logging.Debug($"设置窗口打开，初始焦点={Keyboard.FocusedElement?.GetType().Name}");
        };
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        HotkeyBox.SelectAll();
        Logging.Debug("热键框获得焦点，已全选");
    }

    /// <summary>点击热键框：立即聚焦并全选；阻止默认点击把光标定位在点击处（否则 GotFocus 的全选会被清除，退化为插入式编辑）。</summary>
    private void HotkeyBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HotkeyBox.Focus();
        HotkeyBox.SelectAll();
        e.Handled = true;
        Logging.Debug("热键框点击：聚焦+全选（拦截默认光标定位）");
    }

    /// <summary>焦点不在热键框内时，Esc 关闭设置窗口；热键框内 Esc 由热键框处理（还原文本、交还焦点，不关窗）。
    /// 用 PreviewKeyDown（隧道事件，先于子元素触发）保证窗口一定能收到按键。</summary>
    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !HotkeyBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            Logging.Debug("Esc：焦点不在热键框，关闭设置窗口");
            DialogResult = false;
        }
        else if (e.Key == Key.Escape)
        {
            Logging.Debug("Esc：焦点在热键框内，跳过（由热键框处理）");
        }
    }

    /// <summary>把键盘焦点交还窗口本身：窗口可承载键盘焦点（打开时即如此），Esc 等按键才能路由到窗口级处理。
    /// 先 SetFocusedElement(this, null) 清空窗口焦点作用域记忆的聚焦元素（ClearFocus 不清除作用域记忆，Focus() 会重聚焦热键框），
    /// 再 Focus() 聚焦窗口——直接对窗口 Focus() 只会重聚焦作用域当前子元素（热键框）；
    /// 也不能只 ClearFocus 不聚焦：焦点为 null 时 WPF 不把按键路由给任何元素，Esc 将失效。</summary>
    private void FocusToWindow()
    {
        FocusManager.SetFocusedElement(this, null);
        Focus();
        Logging.Debug($"焦点交还窗口，FocusedElement={Keyboard.FocusedElement?.GetType().Name ?? "null"}");
    }

    /// <summary>点击热键框外任意处：键盘焦点离开热键框（交还窗口），恢复 Esc 关闭设置窗口。
    /// 热键框内的点击已在 PreviewMouseLeftButtonDown 标记 Handled，不会到达这里。
    /// 鼠标按下期间 WPF 会延迟焦点变更，推迟到 Input 优先级执行。</summary>
    private void SettingsWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (HotkeyBox.IsKeyboardFocusWithin)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusToWindow));
        }
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
        Logging.Debug($"热键框按键: Key={e.Key} SystemKey={e.SystemKey} Modifiers={Keyboard.Modifiers}");
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            HotkeyBox.Text = HotkeyText; // 还原
            ErrorTip.Visibility = Visibility.Collapsed;
            FocusToWindow(); // 焦点交还窗口；再按 Esc 即关闭窗口
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
