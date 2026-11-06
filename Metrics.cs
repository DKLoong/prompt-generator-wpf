namespace PromptGenerator;

/// <summary>UI 几何常量（按控件分组：尺寸、列宽、行高、偏移）。</summary>
public static class Metrics
{
    // ---- 标题输入框（nameBox） ----
    public const double NameBoxHeight = 24;
    public const double NameBoxWatermarkOffsetX = 10;

    // ---- 字段行 ----
    public const double FieldRowHeadHeight = 28;
    public const double FieldRowNumberWidth = 34;
    public const double FieldRowAddBtnWidth = 20;
    public const double FieldWatermarkOffsetX = 11;

    // ---- Glyph 图标 ----
    public const double GlyphSize = 12;

    // ---- 底部 SplitButton ----
    public const double SplitButtonHeight = 36;
    public const double SplitButtonActionColWidth = 36;
    public const double SplitButtonDividerWidth = 1;

    // ---- RowGap 分隔行 ----
    public const double RowGapHeight = 16;

    // ---- 窗口初始尺寸 ----
    public const double WindowMaxScreenRatio = 0.95;
}
