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

    /// <summary>本次会话内是否被用户显式保存过（不序列化）。保存过的空白新模板切换时不自动舍弃。</summary>
    [JsonIgnore]
    public bool Persisted { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string prop = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

/// <summary>持久化根。ObservableCollection 让 ComboBox 在增删时自动刷新。</summary>
public class TemplateStoreData
{
    public ObservableCollection<Template> Templates { get; set; } = new();
}
