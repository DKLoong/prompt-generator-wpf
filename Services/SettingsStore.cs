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
        string tmp = _file + ".tmp";
        try
        {
            // 原子写：先写临时文件、再同卷 Move 覆盖目标（NTFS rename 原子替换）。
            // 便携场景（U 盘拔出/断电/杀软扫描冲突）下避免直接 WriteAllText 写坏 JSON。
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
            File.Move(tmp, _file, overwrite: true);
            _cache = settings;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不影响主流程 */ }
            Logging.Error($"设置保存失败：{ex.Message}");
            throw new IOException($"保存设置失败: {ex.Message}", ex);
        }
    }
}
