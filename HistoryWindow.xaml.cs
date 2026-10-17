using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PromptGenerator.Models;

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

    private void RefreshList()
    {
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _history.OrderByDescending(h => h.Timestamp).ToList();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Tab 聚焦到关闭按钮时立即弹出"关闭"提示；移出焦点时收起。</summary>
    private void CloseBtn_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (CloseBtn.ToolTip is ToolTip tip) tip.IsOpen = true;
    }

    private void CloseBtn_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (CloseBtn.ToolTip is ToolTip tip) tip.IsOpen = false;
    }

    private void WindowDrag_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        var entry = _history.FirstOrDefault(h => h.Id == id);
        if (entry == null) return;
        try { Clipboard.SetText(entry.Prompt); } catch { }
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
        // 记录删除前在排序列表中的位置，用于删除后确定焦点落点
        var sortedBefore = _history.OrderByDescending(h => h.Timestamp).ToList();
        int idx = sortedBefore.IndexOf(entry);
        _history.Remove(entry);
        _onDelete?.Invoke(entry);
        RefreshList();
        // 删除后焦点：非最后一条 → 下一条的复制；最后一条 → 上一条的复制；仅剩一条 → 关闭
        // （延后到 Loaded 优先级，确保刷新后的条目容器已生成）
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
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
