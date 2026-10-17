using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PromptGenerator.Models;

/// <summary>模板中的一个字段（标题 + 若干编号子项）。</summary>
public class Field
{
    public string Name { get; set; } = "";
    public List<string> Items { get; set; } = new();
}

/// <summary>一个提示词模板。Name 实现 INotifyPropertyChanged，重命名后下拉项自动刷新。</summary>
public class Template : INotifyPropertyChanged
{
    private string _name = "未命名模板";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public List<Field> Fields { get; set; } = new();

    /// <summary>历史记录编辑产生的临时模板（不序列化）。切走时自动删除，除非用户点了"保存到模板"。</summary>
    [JsonIgnore]
    public bool IsTemp { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string prop = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

/// <summary>一次生成的历史记录。</summary>
public class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TemplateName { get; set; } = "";
    /// <summary>生成时各字段内容快照（用于"编辑"恢复）。</summary>
    public List<Field> Fields { get; set; } = new();
    /// <summary>拼接好的完整提示词。</summary>
    public string Prompt { get; set; } = "";
}

/// <summary>持久化根。ObservableCollection 让 ComboBox 在增删时自动刷新。</summary>
public class TemplateStoreData
{
    public ObservableCollection<Template> Templates { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
}
