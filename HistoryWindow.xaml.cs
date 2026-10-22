using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PromptGenerator.Models;
using PromptGenerator.Services;

namespace PromptGenerator;

public partial class HistoryWindow : Window
{
    private List<HistoryEntry> _history;
    private readonly System.Action<HistoryEntry> _onEdit;
    private readonly System.Action<HistoryEntry> _onDelete;

    private DispatcherTimer? _toastTimer;
    /// <summary>待撤销操作（删除确认后 3 秒内有效）；点击撤销按钮执行，超时置空。</summary>
    private System.Action? _undoAction;
    /// <summary>确认弹层当前待执行动作（删除）；确认时执行并清空，取消/点外部关闭时清空。</summary>
    private System.Action? _confirmAction;
    /// <summary>确认弹层键盘取消后回焦的按钮（锚点=删除按钮）。</summary>
    private Button? _confirmReturnFocus;
    /// <summary>确认弹层键盘确认后执行的回焦动作（移焦撤销按钮、设置 toast 消失回焦相邻复制按钮）。</summary>
    private System.Action? _confirmKeyboardAction;
    /// <summary>确认弹层鼠标确认后执行的清焦动作（焦点落回窗口本身，消除删除按钮键盘高亮残留）。</summary>
    private System.Action? _confirmMouseAction;
    /// <summary>toast 消失后回焦相邻复制按钮的回焦动作（键盘删除路径设置）；鼠标路径为 null，toast 消失自然失焦。</summary>
    private System.Action? _toastReturnFocusAction;
    /// <summary>键盘撤销后回焦恢复条目复制按钮的回焦动作（延后到 Loaded 执行）。</summary>
    private System.Action? _undoReturnFocusAction;
    /// <summary>弹窗打开入口是否键盘：键盘入口默认聚焦确认按钮显示柔和红；鼠标入口不显示（仅 hover 高亮）。</summary>
    private bool _suppressConfirmFocusBrush;
    /// <summary>本次确认是否键盘触发（Enter）：键盘确认后移焦撤销按钮；鼠标确认不移焦。</summary>
    private bool _confirmByKeyboard;

    public HistoryWindow(List<HistoryEntry> history,
        System.Action<HistoryEntry> onEdit,
        System.Action<HistoryEntry> onDelete)
    {
        InitializeComponent();
        _history = history ?? new List<HistoryEntry>();
        _onEdit = onEdit;
        _onDelete = onDelete;
        RefreshList();
    }

    /// <summary>按时间倒序刷新列表；搜索框有非空关键字时，仅保留标题（TemplateName）或内容（Prompt）
    /// 包含关键字的历史条目（字符串包含匹配，忽略大小写）。空关键字显示全部。
    /// scrollToTop=false 时（删除路径）不回顶，由调用方恢复滚动位置，保持"滚动条位置不变、下方内容补缺"。</summary>
    private void RefreshList(bool scrollToTop = true)
    {
        string keyword = SearchBox.Text?.Trim() ?? "";
        IEnumerable<HistoryEntry> query = _history.OrderByDescending(h => h.Timestamp);
        if (keyword.Length > 0)
            query = query.Where(h =>
                (h.TemplateName?.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || (h.Prompt?.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = query.ToList();
        if (scrollToTop)
        {
            // 内容变化（搜索/清空）：列表滚动条回顶（延后到 ItemsSource 重建后的布局完成）
            Dispatcher.BeginInvoke(new System.Action(() => HistoryScroll?.ScrollToTop()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>点击清空按钮：清空搜索框内容（触发 TextChanged → 恢复全部列表、占位/清空区同步显隐）。</summary>
    private void SearchClearBtn_Click(object sender, RoutedEventArgs e) => SearchBox.Clear();

    /// <summary>搜索框实时过滤历史条目（无防抖，逐字符刷新；历史量 ≤50，开销可忽略）。
    /// 占位提示：聚焦/输入时隐藏，失焦且文本为空时显示。</summary>
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchWatermark.Visibility = SearchBox.IsKeyboardFocusWithin
            ? Visibility.Collapsed
            : (string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed);
        RefreshList();
    }

    /// <summary>光标插入搜索框：占位提示隐藏。</summary>
    private void SearchBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        => SearchWatermark.Visibility = Visibility.Collapsed;

    /// <summary>光标离开搜索框：文本为空时恢复占位提示。</summary>
    private void SearchBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        => SearchWatermark.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>ESC：关闭历史记录面板（与 ✕ 行为一致）；
    /// Ctrl+D：搜索框非空时快速清空（与点击清空按钮同路径，仅键盘操作，既有鼠标逻辑不变）。</summary>
    private void HistoryWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == System.Windows.Input.Key.D
            && e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control
            && !string.IsNullOrEmpty(SearchBox.Text))
        {
            e.Handled = true;
            SearchBox.Clear();
        }
    }

    /// <summary>Tab 聚焦到关闭按钮时立即弹出"关闭"提示；移出焦点时收起。
    /// 悬停路径由 ToolTipService 自动设置 PlacementTarget；键盘路径需显式指定，否则按鼠标位置摆放。</summary>
    private void CloseBtn_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (CloseBtn.ToolTip is ToolTip tip)
        {
            tip.PlacementTarget = CloseBtn;
            tip.IsOpen = true;
        }
    }

    private void CloseBtn_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (CloseBtn.ToolTip is ToolTip tip) tip.IsOpen = false;
    }

    private void WindowDrag_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            // 搜索框内按下不拖拽窗口：保留文本框光标定位/选中/拖选（标题文字区域仍可拖拽）
            if (e.OriginalSource is DependencyObject src && IsWithinElement(src, SearchBox)) return;
            // 点击搜索框外的标题栏区域：搜索框失去键盘焦点（标题栏非焦点目标，点击默认不抢焦点需手动清焦）
            if (SearchBox.IsKeyboardFocusWithin) System.Windows.Input.Keyboard.ClearFocus();
            DragMove();
        }
    }

    /// <summary>命中测试：元素（或其祖先）是否为指定目标元素。</summary>
    private static bool IsWithinElement(DependencyObject? node, DependencyObject target)
    {
        for (DependencyObject? d = node; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == target) return true;
        return false;
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var entry = _history.FirstOrDefault(h => h.Id == id);
        if (entry == null) return;
        try { Clipboard.SetText(entry.Prompt); }
        catch (Exception ex) { Logging.Warn($"历史条目复制失败：{ex.Message}"); }
    }

    private void EditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var entry = _history.FirstOrDefault(h => h.Id == id);
        if (entry == null) return;
        _onEdit?.Invoke(entry);
        Close();
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var entry = _history.FirstOrDefault(h => h.Id == id);
        if (entry == null) return;
        // 输入源区分：鼠标点击删除（Mouse.DirectlyOver 落在该按钮上）→ 鼠标路径；
        // 键盘触发（Tab/Shift+Tab 导航到按钮后按空格，鼠标不在按钮上）→ 键盘路径
        bool byKeyboard = !IsPointerOverButton(System.Windows.Input.Mouse.DirectlyOver as DependencyObject, btn);
        ShowConfirm(btn, "删除历史记录", $"删除历史记录「{entry.TemplateName}」？",
            () => DeleteHistoryConfirmed(entry), byKeyboard);
    }

    /// <summary>删除确认后的执行：移除条目 + 落盘 + 刷新（保持滚动位置、下方内容补缺）+ 带撤销按钮的 toast。
    /// 焦点路径由 ConfirmOk_Click 按确认输入源分发（键盘 Enter 确认→移焦撤销按钮、toast 消失回焦相邻复制按钮；
    /// 鼠标确认→焦点落回窗口本身，原逻辑不变），此处只预置对应动作。</summary>
    private void DeleteHistoryConfirmed(HistoryEntry entry)
    {
        // 记录删除前在排序列表中的位置（toast 消失后回焦相邻行用）与滚动位置（删除后保持）
        var sortedBefore = _history.OrderByDescending(h => h.Timestamp).ToList();
        int idx = sortedBefore.IndexOf(entry);
        double vOffset = HistoryScroll?.VerticalOffset ?? 0;
        _history.Remove(entry);
        _onDelete?.Invoke(entry);
        RefreshList(scrollToTop: false);
        if (vOffset > 0)
            Dispatcher.BeginInvoke(new System.Action(() => HistoryScroll?.ScrollToVerticalOffset(vOffset)),
                DispatcherPriority.Loaded);
        Logging.Info($"删除历史记录：{entry.TemplateName}");
        ShowToast($"已删除「{entry.TemplateName}」", true, () =>
        {
            // 撤销删除：原位置插回 + 落盘 + 刷新（保持滚动位置）
            _history.Insert(Math.Min(idx, _history.Count), entry);
            _onDelete?.Invoke(entry);
            double v2 = HistoryScroll?.VerticalOffset ?? 0;
            RefreshList(scrollToTop: false);
            if (v2 > 0)
                Dispatcher.BeginInvoke(new System.Action(() => HistoryScroll?.ScrollToVerticalOffset(v2)),
                    DispatcherPriority.Loaded);
            Logging.Info($"撤销删除历史记录：{entry.TemplateName}");
            ShowToast("已撤销删除");
        });
        // 键盘确认后：移焦撤销按钮；toast 消失回焦相邻复制按钮；键盘撤销后回焦恢复条目的复制按钮
        _confirmKeyboardAction = () =>
        {
            _toastReturnFocusAction = () => FocusAdjacentCopy(entry, idx);
            _undoReturnFocusAction = () => Dispatcher.BeginInvoke(
                new System.Action(() => FocusEntryCopy(entry)), DispatcherPriority.Loaded);
            if (UndoBtn.Visibility == Visibility.Visible) UndoBtn.Focus();
        };
        // 鼠标确认后：焦点落回窗口本身、不在任何控件上（popup 关闭后 WPF 会把焦点恢复到锚点按钮，
        // 恢复时机不固定——先清一次，再延后至最低优先级兜底清焦，避免删除按钮键盘高亮残留）
        _confirmMouseAction = () =>
        {
            System.Windows.Input.Keyboard.ClearFocus();
            Focus();
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new System.Action(() =>
            {
                if (System.Windows.Input.Keyboard.FocusedElement is Button)
                    System.Windows.Input.Keyboard.ClearFocus();
            }));
        };
    }

    /// <summary>toast 消失后回焦相邻历史记录的"复制"按钮（键盘路径）：
    /// 删除的不是最后一条 → 下一条；删除的是最后一条 → 上一条；仅剩一条 → 关闭按钮。</summary>
    private void FocusAdjacentCopy(HistoryEntry deletedEntry, int sortedIndexBefore)
    {
        var remaining = _history.OrderByDescending(h => h.Timestamp).ToList();
        if (remaining.Count == 0)
        {
            CloseBtn.Focus();
            System.Windows.Input.Keyboard.Focus(CloseBtn);
            return;
        }
        // 删除前位置 idx：idx < 剩余数 → 下一条（原 idx+1 位）；否则 → 上一条
        var target = sortedIndexBefore < remaining.Count ? remaining[sortedIndexBefore] : remaining[sortedIndexBefore - 1];
        if (target != null && FindCopyButton(target) is Button copyBtn)
        {
            copyBtn.BringIntoView();
            copyBtn.Focus();
            System.Windows.Input.Keyboard.Focus(copyBtn);
        }
    }

    /// <summary>聚焦指定条目的"复制"按钮（键盘撤销后：聚焦恢复的条目）。</summary>
    private void FocusEntryCopy(HistoryEntry entry)
    {
        if (FindCopyButton(entry) is Button copyBtn)
        {
            copyBtn.BringIntoView();
            copyBtn.Focus();
            System.Windows.Input.Keyboard.Focus(copyBtn);
        }
    }

    // ---------------- 轻提示（toast） ----------------

    private void ShowToast(string msg, bool ok = true) => ShowToast(msg, ok, null);

    /// <summary>显示 3 秒轻提示。带 <paramref name="undoAction"/> 时显示"撤销"按钮并允许点击（toast 层此时可命中），
    /// 点击撤销立即隐藏并执行；否则不挡点击（IsHitTestVisible=False）。</summary>
    private void ShowToast(string msg, bool ok, System.Action? undoAction)
    {
        Toast.Text = msg;
        Toast.Foreground = ok ? (Brush)FindResource("OkBrush") : (Brush)FindResource("DangerBrush");
        _undoAction = undoAction;
        UndoBtn.Visibility = undoAction != null ? Visibility.Visible : Visibility.Collapsed;
        ToastLayer.IsHitTestVisible = undoAction != null;
        ToastLayer.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) =>
        {
            ToastLayer.Visibility = Visibility.Collapsed;
            UndoBtn.Visibility = Visibility.Collapsed;
            ToastLayer.IsHitTestVisible = false;
            _undoAction = null;
            // 键盘路径：toast 消失后回焦相邻复制按钮；鼠标路径为 null 自然失焦
            var returnAction = _toastReturnFocusAction;
            _toastReturnFocusAction = null;
            if (returnAction != null) returnAction();
            _toastTimer!.Stop();
        };
        _toastTimer.Start();
    }

    private void UndoBtn_Click(object sender, RoutedEventArgs e)
    {
        var action = _undoAction;
        ToastLayer.Visibility = Visibility.Collapsed;
        UndoBtn.Visibility = Visibility.Collapsed;
        ToastLayer.IsHitTestVisible = false;
        _toastTimer?.Stop();
        _undoAction = null;
        // 撤销已处理删除回焦：清除"toast 消失回焦相邻复制按钮"；"已撤销删除"toast 无回焦动作
        _toastReturnFocusAction = null;
        action?.Invoke();
        // 键盘撤销（Tab 聚焦+Enter/Space 触发，IsMouseOver=false）：立即回焦恢复条目的复制按钮；
        // 鼠标点击撤销不回焦（焦点自然回到点击前位置）
        bool byKeyboard = !(sender is Button b && b.IsMouseOver);
        if (byKeyboard)
        {
            var undoReturn = _undoReturnFocusAction;
            _undoReturnFocusAction = null;
            if (undoReturn != null) undoReturn();
        }
    }

    // ---------------- 确认弹层（popconfirm） ----------------

    /// <summary>以 <paramref name="anchor"/> 为锚点打开确认弹层；<paramref name="byKeyboard"/> 为 true 时
    /// 默认聚焦确认按钮并显示柔和红，否则（鼠标入口）聚焦但不显示红底（仅 hover 高亮）。</summary>
    private void ShowConfirm(FrameworkElement? anchor, string title, string message, System.Action confirm, bool byKeyboard)
    {
        ConfirmTitle.Text = title;
        ConfirmMsg.Text = message;
        _confirmAction = confirm;
        _confirmReturnFocus = anchor as Button;   // 键盘取消后回焦的按钮（删除按钮）
        _suppressConfirmFocusBrush = !byKeyboard;
        _confirmByKeyboard = byKeyboard;   // 初始输入源；popconfirm 内按 Enter 确认时覆盖为 true（键盘路径）
        ConfirmPopup.PlacementTarget = anchor ?? this;
        ConfirmPopup.IsOpen = true;
    }

    private void ConfirmPopup_Opened(object? sender, EventArgs e)
    {
        // Popup 完成打开后才可聚焦内容，确保 Enter=确认、Tab 切到取消后 Enter=取消
        Dispatcher.BeginInvoke(() => ConfirmOk.Focus(), DispatcherPriority.Loaded);
    }

    private void ConfirmOk_Click(object sender, RoutedEventArgs e)
    {
        ConfirmPopup.IsOpen = false;
        var action = _confirmAction;
        _confirmAction = null;
        bool byKeyboard = _confirmByKeyboard;
        _confirmByKeyboard = false;
        action?.Invoke();
        // 焦点分发：键盘 Enter 确认 → 移焦撤销按钮（toast 消失回焦相邻复制按钮）；
        // 鼠标确认 → 焦点落回窗口本身（原逻辑不变）
        var kbAction = _confirmKeyboardAction;
        var mouseAction = _confirmMouseAction;
        _confirmKeyboardAction = null;
        _confirmMouseAction = null;
        if (byKeyboard) kbAction?.Invoke();
        else mouseAction?.Invoke();
    }

    /// <summary>确认按钮键盘聚焦视觉：仅程序化（键盘入口）/键盘导航聚焦显示柔和红；鼠标点击聚焦（IsMouseOver）不显示。</summary>
    private void ConfirmOk_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Button b || b.IsMouseOver) return;
        if (_suppressConfirmFocusBrush) { _suppressConfirmFocusBrush = false; return; }
        b.Background = (Brush)FindResource("ConfirmBrush");
    }

    private void ConfirmOk_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Button b) return;
        b.ClearValue(Button.BackgroundProperty);
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        // 键盘取消（Esc，或 Tab 到取消后 Enter：sender 均非按钮）后焦点回锚点按钮（删除）；
        // 鼠标点击取消（IsMouseOver=true）失焦
        bool byKeyboard = !(sender is Button b && b.IsMouseOver);
        var returnTo = byKeyboard ? _confirmReturnFocus : null;
        var mouseAnchor = byKeyboard ? null : _confirmReturnFocus;   // 鼠标路径：保留锚点引用用于延后清焦判断
        _confirmReturnFocus = null;
        _confirmAction = null;
        _confirmByKeyboard = false;
        _confirmKeyboardAction = null;
        _confirmMouseAction = null;
        ConfirmPopup.IsOpen = false;
        if (returnTo != null)
            returnTo.Focus();   // 键盘取消：回焦锚点
        else
        {
            // 鼠标取消：Popup 关闭后 WPF 会把焦点恢复到锚点按钮，恢复发生的时机不固定
            // （实测同步与 Loaded 延后均被覆盖）。先同步清焦一次，再延后至最低优先级
            // （ContextIdle，必晚于一切焦点恢复）兜底清焦——仅当焦点确实落在锚点上才清
            System.Windows.Input.Keyboard.ClearFocus();
            var anchor = mouseAnchor;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new System.Action(() =>
            {
                if (anchor != null && System.Windows.Input.Keyboard.FocusedElement == anchor)
                    System.Windows.Input.Keyboard.ClearFocus();
            }));
        }
    }

    private void ConfirmPopup_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            ConfirmCancel_Click(sender, new RoutedEventArgs());
        }
        else if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            if (ConfirmCancel.IsKeyboardFocused)
                ConfirmCancel_Click(sender, new RoutedEventArgs());
            else
            {
                _confirmByKeyboard = true;
                ConfirmOk_Click(sender, new RoutedEventArgs());
            }
        }
    }

    /// <summary>鼠标当前是否悬停/按在指定按钮上（沿视觉树向上命中该按钮）。用于区分鼠标点击与键盘触发（空格/Enter）。</summary>
    private static bool IsPointerOverButton(DependencyObject? node, Button btn)
    {
        for (DependencyObject? d = node; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == btn) return true;
        return false;
    }

    /// <summary>按条目定位其行内 Tag 与 Id 匹配的"复制"按钮。</summary>
    private Button? FindCopyButton(HistoryEntry item)
    {
        if (HistoryList.ItemContainerGenerator.ContainerFromItem(item) is not DependencyObject container)
            return null;
        return FindButtonByTag(container, item.Id);
    }

    private static Button? FindButtonByTag(DependencyObject root, string id)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b && b.Tag is string s && s == id) return b;
            if (FindButtonByTag(child, id) is Button hit) return hit;
        }
        return null;
    }
}
