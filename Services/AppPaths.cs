using System.IO;

namespace PromptGenerator.Services;

/// <summary>
/// 应用数据目录统一入口：%LOCALAPPDATA%\PromptGenerator\data。
/// 旧版本把数据写在程序目录 data\（AppContext.BaseDirectory）：封装安装到 Program Files 等只读位置时会
/// 写入失败并静默回退，且数据位置随安装位置漂移。现统一到用户可写目录，首次启动自动迁移旧数据。
/// </summary>
public static class AppPaths
{
    /// <summary>应用数据根目录（data）。首次访问时创建目录，并将旧位置（程序目录 data\）的用户数据迁移过来。</summary>
    public static string DataDir { get; } = Init();

    private static string Init()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PromptGenerator", "data");
        Directory.CreateDirectory(dir);
        TryMigrateFromLegacy(dir);
        return dir;
    }

    /// <summary>
    /// 旧版本数据在程序目录 data\：仅当目标为空（首次切换）且旧位置存在 templates.json 时，
    /// 把模板/设置/日志复制到新目录（已存在的文件不覆盖，保守保留新位置文件）。失败不阻塞启动。
    /// </summary>
    private static void TryMigrateFromLegacy(string targetDir)
    {
        try
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "data");
            if (!Directory.Exists(legacy)) return;
            if (!File.Exists(Path.Combine(legacy, "templates.json"))) return;
            if (File.Exists(Path.Combine(targetDir, "templates.json"))) return;

            CopyFiles(legacy, targetDir);
            var legacyLogs = Path.Combine(legacy, "logs");
            if (Directory.Exists(legacyLogs))
                CopyFiles(legacyLogs, Path.Combine(targetDir, "logs"));
        }
        catch
        {
            // 迁移失败不阻塞启动：新位置从空开始
        }
    }

    private static void CopyFiles(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var f in Directory.EnumerateFiles(srcDir))
        {
            var dest = Path.Combine(dstDir, Path.GetFileName(f));
            if (!File.Exists(dest)) File.Copy(f, dest);
        }
    }
}
