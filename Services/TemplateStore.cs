using System.IO;
using System.Text.Json;
using PromptGenerator.Models;

namespace PromptGenerator.Services;

/// <summary>模板 JSON 读写（程序目录，失败回退 %LOCALAPPDATA%）。</summary>
public class TemplateStore
{
    private readonly string _dir;
    private readonly string _file;
    private TemplateStoreData? _cache;

    public TemplateStore()
    {
        string baseDir;
        try
        {
            baseDir = AppContext.BaseDirectory;
        }
        catch
        {
            baseDir = Environment.CurrentDirectory;
        }

        var dataDir = Path.Combine(baseDir, "data");
        try
        {
            Directory.CreateDirectory(dataDir);
            _dir = dataDir;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PromptGenerator", "data");
            Directory.CreateDirectory(fallback);
            _dir = fallback;
        }
        _file = Path.Combine(_dir, "templates.json");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public TemplateStoreData Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(_file))
            {
                var json = File.ReadAllText(_file);
                _cache = JsonSerializer.Deserialize<TemplateStoreData>(json, JsonOpts) ?? new TemplateStoreData();
            }
            else
            {
                _cache = new TemplateStoreData();
            }
        }
        catch (Exception ex)
        {
            Logging.Warn($"模板加载失败，使用默认模板：{ex.Message}");
            _cache = new TemplateStoreData();
        }
        // 保证至少有一个模板（默认模板）
        if (_cache.Templates.Count == 0)
        {
            _cache.Templates.Add(CreateDefaultTemplate());
        }
        return _cache;
    }

    public void Save(TemplateStoreData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, JsonOpts);
            File.WriteAllText(_file, json);
            _cache = data;
        }
        catch (Exception ex)
        {
            Logging.Error($"模板保存失败：{ex.Message}");
            throw new IOException($"保存模板失败: {ex.Message}", ex);
        }
    }

    private static Template CreateDefaultTemplate()
    {
        var t = new Template { Name = "通用任务" };
        t.Fields.Add(new Field
        {
            Name = "任务描述",
            Items = { "" }
        });
        t.Fields.Add(new Field
        {
            Name = "背景",
            Items = { "" }
        });
        t.Fields.Add(new Field
        {
            Name = "约束条件",
            Items = { "" }
        });
        t.Fields.Add(new Field
        {
            Name = "验收标准",
            Items = { "" }
        });
        return t;
    }
}
