using System.IO;

namespace PromptGenerator.Services;

/// <summary>
/// 应用数据目录统一入口，便携优先：
///   1) exe 所在目录 data\（单文件发布时即 exe 旁）——可写则用，数据跟随程序文件，点击即用；
///   2) 不可写（如被放到 Program Files 等受保护位置）回退 %LOCALAPPDATA%\PromptGenerator\data。
/// 回退时若 exe 旁存在历史数据（只读也可读），自动复制一份到回退目录，避免"数据消失"。
/// </summary>
public static class AppPaths
{
    /// <summary>应用数据根目录（data）。首次访问时解析并创建。</summary>
    public static string DataDir { get; } = ResolveDataDir();

    /// <summary>便携优先：exe 所在目录 data\，可写即用；否则回退 %LOCALAPPDATA%\PromptGenerator\data。</summary>
    private static string ResolveDataDir()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (TryMakeWritable(portable)) return portable;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PromptGenerator", "data");
        Directory.CreateDirectory(fallback);
        TryCopyFromPortable(fallback);
        return fallback;
    }

    /// <summary>确保目录存在且可写：创建目录并写入/删除探针文件，避免"目录存在但只读"漏判。</summary>
    private static bool TryMakeWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>回退场景：若 exe 旁存在历史用户数据（只读也可读），按文件逐个补齐到回退目录——
    /// templates.json / settings.json 各自独立判断（目标已存在同名文件则不覆盖），logs 整目录补齐。失败不阻塞。</summary>
    private static void TryCopyFromPortable(string targetDir)
    {
        try
        {
            var portable = Path.Combine(AppContext.BaseDirectory, "data");
            if (!Directory.Exists(portable)) return;
            bool hasData = File.Exists(Path.Combine(portable, "templates.json"))
                        || File.Exists(Path.Combine(portable, "settings.json"));
            if (!hasData) return;

            CopyFiles(portable, targetDir);
            var logs = Path.Combine(portable, "logs");
            if (Directory.Exists(logs))
                CopyFiles(logs, Path.Combine(targetDir, "logs"));
        }
        catch
        {
            // 复制失败不阻塞启动：回退目录从空开始
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
