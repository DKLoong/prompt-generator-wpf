using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PromptGenerator.Models;
using PromptGenerator.Services;

namespace PromptGenerator;

public partial class HistoryWindow : Window
{
    private List<HistoryEntry> _history;
    private readonly System.Action<HistoryEntry> _onEdit;
    private readonly System.Action<HistoryEntry> _onDelete;

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
    /// 包含关键字的历史条目（字符串包含匹配，忽略大小写）。空关键字显示全部。</summary>
    private void RefreshList()
    {
        string keyword = SearchBox.Text?.Trim() ?? "";
        IEnumerable<HistoryEntry> query = _history.OrderByDescending(h => h.Timestamp);
        if (keyword.Length > 0)
            query = query.Where(h =>
                (h.TemplateName?.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                || (h.Prompt?.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = query.ToList();
        // 内容变化（搜索/清空）：列表滚动条回顶（延后到 ItemsSource 重建后的布局完成）
        Dispatcher.BeginInvoke(new System.Action(() => HistoryScroll?.ScrollToTop()),
            System.Windows.Threading.DispatcherPriority.Loaded);
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
        // 输入源区分：鼠标点击删除（Mouse.DirectlyOver 落在该按钮上）→ 删除后焦点落回窗口本身；
        // 键盘触发（Tab/Shift+Tab 导航到按钮后按空格，鼠标不在按钮上）→ 保留"相邻行复制按钮聚焦"，
        // 便于连续键盘操作（既有正确设计，不修改）。
        bool mouseClick = IsPointerOverButton(System.Windows.Input.Mouse.DirectlyOver as DependencyObject, btn);
        // 记录删除前在排序列表中的位置，用于删除后确定焦点落点
        var sortedBefore = _history.OrderByDescending(h => h.Timestamp).ToList();
        int idx = sortedBefore.IndexOf(entry);
        _history.Remove(entry);
        _onDelete?.Invoke(entry);
        RefreshList();
        // 删除后焦点：（延后到 Loaded 优先级，确保刷新后的条目容器已生成）
        // - 鼠标点击：焦点落回窗口本身、不在任何控件上（消除相邻行复制按钮的键盘高亮残留）
        // - 键盘触发：非最后一条 → 下一条的复制；最后一条 → 上一条的复制；仅剩一条 → 关闭
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            if (mouseClick)
            {
                System.Windows.Input.FocusManager.SetFocusedElement(this, null);
                Focus();
                return;
            }
            var remaining = _history.OrderByDescending(h => h.Timestamp).ToList();
            if (remaining.Count == 0)
            {
                CloseBtn.Focus();
                System.Windows.Input.Keyboard.Focus(CloseBtn);
                return;
            }
            var target = idx + 1 < sortedBefore.Count ? remaining[idx] : remaining[idx - 1];
            if (target != null && FindCopyButton(target) is Button copyBtn)
            {
                copyBtn.BringIntoView();
                copyBtn.Focus();
                System.Windows.Input.Keyboard.Focus(copyBtn);
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
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
