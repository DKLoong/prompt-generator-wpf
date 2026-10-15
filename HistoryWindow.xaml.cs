using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        _history.Remove(entry);
        _onDelete?.Invoke(entry);
        RefreshList();
    }
}
