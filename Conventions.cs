namespace PromptGenerator;

/// <summary>跨文件共享的业务字符串常量（拖拽格式名、主题名、Tag 状态值、日志文件名）。</summary>
public static class Conventions
{
    /// <summary>字段行拖拽 DataFormat 名。</summary>
    public static class DragFormats
    {
        public const string Item = "PGItemDrag";
    }

    /// <summary>主题标识（与 Themes/*.xaml 文件名后缀一致）。</summary>
    public static class Themes
    {
        public const string Cool = "Cool";
        public const string Warm = "Warm";
        public const string Graphite = "Graphite";
    }

    /// <summary>置顶按钮 Tag 状态值。</summary>
    public static class PinState
    {
        public const string Pinned = "pinned";
    }

    /// <summary>日志/数据文件名。</summary>
    public static class LogFile
    {
        public const string Crash = "crash.log";
    }

    /// <summary>半透明背景叠加层 alpha 值（0x26 ≈ 15% 不透明度）。</summary>
    public const byte OverlayAlpha = 0x26;

    /// <summary>Toast 提示消息（避免多处硬编码不一致）。</summary>
    public static class Messages
    {
        /// <summary>编辑操作未退出时的拦截提示。</summary>
        public const string ExitEditFirst = "请先退出编辑操作，按Enter（保存）或ESC（取消）";

        /// <summary>模板名已存在提示。</summary>
        public static string TemplateNameExists(string name) => $"模板名「{name}」已存在";

        /// <summary>保存失败提示。</summary>
        public static string SaveFailed(string reason) => $"保存失败：{reason}";
    }
}
