namespace PromptGenerator.Models;

/// <summary>应用设置。</summary>
public class AppSettings
{
    /// <summary>全局显示/隐藏热键，格式如 "Ctrl+Alt+P"。</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Shift+P";

    /// <summary>日志级别（0=Error,1=Warn,2=Info,3=Debug），默认 Info。</summary>
    public int LogLevel { get; set; } = 2;

    /// <summary>是否启用贴边收起。</summary>
    public bool EdgeDockEnabled { get; set; } = true;

    /// <summary>是否启用"最近使用置顶"：最近使用的 5 个模板自动排到下拉列表最前。</summary>
    public bool PinRecentTemplates { get; set; } = true;

    /// <summary>窗口位置与尺寸记忆。</summary>
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 365;
    public double WindowHeight { get; set; } = 600;
}
