using System.Diagnostics;
using System.IO;
using System.Text;

namespace PromptGenerator.Services;

/// <summary>日志级别（数值越大越详细）。</summary>
public enum LogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
}

/// <summary>
/// 应用统一运行日志。落点固定在 <c>data\logs\app-YYYYMMDD.log</c>（按天分文件，保留 <see cref="RetainDays"/> 天，
/// 每次启动清理过期）。文件写入线程安全（加锁）；同时通过 <c>Trace.WriteLine</c> 镜像一份到调试器/Output，
/// 便于无 GUI 场景直接从日志文件定位问题。
///
/// 级别控制（取最宽松者生效）：
///   - settings.json 持久化的 LogLevel（默认 Info）
///   - 命令行 <c>--debug</c>：临时提升到 Debug，不写回设置
/// </summary>
public static class Logging
{
    private const string LogsDirName = "logs";
    private const int RetainDays = 7;
    private static readonly object Sync = new();
    private static string _dir = "";
    private static LogLevel _level = LogLevel.Info;
    private static string _today = "";

    /// <summary>日志目录（data\logs）。</summary>
    public static string DirectoryPath
    {
        get
        {
            lock (Sync)
            {
                if (_dir.Length == 0) EnsureDir();
                return _dir;
            }
        }
    }

    /// <summary>当前生效级别。</summary>
    public static LogLevel Level { get { lock (Sync) return _level; } }

    /// <summary>初始化：解析数据目录与日志级别。命令行 --debug 可临时提升到 Debug（不写回设置）。</summary>
    public static void Init(string?[]? args, LogLevel settingsLevel)
    {
        lock (Sync)
        {
            _level = settingsLevel;
            foreach (var a in args ?? Array.Empty<string?>())
            {
                if (string.Equals(a, "--debug", StringComparison.OrdinalIgnoreCase))
                {
                    _level = LogLevel.Debug;
                    break;
                }
            }
            EnsureDir();
            // 启动即清理过期日志（保留 RetainDays 天）
            Cleanup();
        }
        Info($"日志系统初始化完成，级别={_level}，目录={_dir}");
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);
    public static void Error(string msg, Exception ex) => Write(LogLevel.Error, $"{msg} {Describe(ex)}");

    /// <summary>
    /// 记录一次提示词相关事件：默认只记模板名与 prompt 长度（不落工作内容），
    /// 级别为 Debug 时追加 prompt 全文（截断到 <see cref="MaxPromptChars"/>）。
    /// </summary>
    public static void Prompt(LogLevel level, string action, string? templateName, string? prompt)
    {
        int len = prompt?.Length ?? 0;
        if (level > _level) return;
        string detail = _level >= LogLevel.Debug && !string.IsNullOrEmpty(prompt)
            ? $" prompt=[{Truncate(prompt!, MaxPromptChars)}]"
            : "";
        Write(level, $"Prompt[{action}] 模板={templateName ?? "(无)"} 长度={len}{detail}");
    }

    private const int MaxPromptChars = 2000;

    private static void Write(LogLevel level, string msg)
    {
        bool shouldLog;
        string dir;
        string today;
        lock (Sync)
        {
            shouldLog = level <= _level;
            if (!shouldLog) return;
            dir = _dir;
            today = _today;
        }

        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level,-5}] {msg}";
        // 镜像到调试器/Output（不参与文件日志过滤，直接透传）
        Trace.WriteLine(line);

        // 文件写入：独立加锁（避免与级别读取同一把锁造成无谓竞争；日志文件本身线程安全即可）
        lock (Sync)
        {
            if (dir.Length == 0) EnsureDir();
            // 跨天滚动
            string now = DateTime.Now.ToString("yyyyMMdd");
            if (now != _today) _today = now;
            try
            {
                var file = Path.Combine(_dir, $"app-{_today}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志写入失败不影响应用运行（如磁盘满/权限）
            }
        }
    }

    private static void EnsureDir()
    {
        try
        {
            _dir = Path.Combine(AppPaths.DataDir, LogsDirName);
            Directory.CreateDirectory(_dir);
        }
        catch
        {
            _dir = "";
        }
        _today = DateTime.Now.ToString("yyyyMMdd");
    }

    private static void Cleanup()
    {
        try
        {
            if (_dir.Length == 0) return;
            var cutoff = DateTime.Today.AddDays(-RetainDays);
            foreach (var f in Directory.EnumerateFiles(_dir, "app-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
            }
        }
        catch { /* 清理失败不影响使用 */ }
    }

    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e != null; e = e.InnerException)
            sb.Append(e.Message).Append(" | ");
        return sb.Length > 0 ? sb.ToString().TrimEnd(' ', '|') : ex.GetType().Name;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"…(截断，共{s.Length}字符)";
}
