using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PromptGenerator.Models;

namespace PromptGenerator.Services;

/// <summary>应用设置 JSON 读写（同模板存储目录）。</summary>
public class SettingsStore
{
    private readonly string _file;
    private AppSettings? _cache;

    public SettingsStore(string dataDir)
    {
        _file = Path.Combine(dataDir, "settings.json");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // WindowX/Y 默认 double.NaN（未记忆窗口位置），允许 NaN/Infinity 作为 JSON 字面量
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public AppSettings Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(_file))
            {
                _cache = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file), JsonOpts) ?? new AppSettings();
            }
            else
            {
                _cache = new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Logging.Warn($"设置加载失败，使用默认设置：{ex.Message}");
            _cache = new AppSettings();
        }
        return _cache;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(settings, JsonOpts));
            _cache = settings;
        }
        catch (Exception ex)
        {
            Logging.Error($"设置保存失败：{ex.Message}");
            throw new IOException($"保存设置失败: {ex.Message}", ex);
        }
    }
}
