using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PromptGenerator.Controls;

/// <summary>
/// 成对符号自动补全（字段框/内容框通用）：
/// 1) 输入左符号 → 自动补右符号，光标停在中间；光标后已是右符号时，输入右符号直接跳过不重复；
/// 2) 有选中文本时输入左符号 → 选中内容被包裹进成对符号（光标停在包裹内容之后、右符号之前）。
/// 兼容两种输入路径：
/// - 直接键盘输入（英文/半角、传统 IMM 输入法）：PreviewTextInput 在文本落地前触发，置 Handled 阻止默认插入；
/// - TSF 输入法提交（搜狗/微软拼音等）：字符已先写入文本，PreviewTextInput 仅作通知，Handled 无法撤销，
///   通过对比 PreviewTextInputStart 时记录的文本识别"已提交"状态并修复（补右符/跳过/用保存的选区恢复包裹）。
/// </summary>
public static class PairCompletion
{
    /// <summary>开符号 → 闭符号（含自配对符号：键=值）。</summary>
    private static readonly Dictionary<char, char> OpenToClose = new()
    {
        { '(', ')' }, { '[', ']' }, { '{', '}' },
        { '"', '"' }, { '\'', '\'' }, { '`', '`' },
        { '《', '》' }, { '「', '」' }, { '『', '』' }, { '【', '】' },
        { '〈', '〉' }, { '“', '”' }, { '‘', '’' }, { '（', '）' },
    };

    /// <summary>非自配对的闭符号集合，用于"光标后是右符号时跳过"。</summary>
    private static readonly HashSet<char> ClosingChars = new(
        OpenToClose.Where(kv => kv.Key != kv.Value).Select(kv => kv.Value));

    /// <summary>编辑结果：NewText=null 且 Caret=null → 不干预；仅 Caret 非空 → 仅移动光标（跳过）；NewText 非空 → 替换文本并定位光标。</summary>
    public readonly record struct PairEdit(string? NewText, int? Caret);

    /// <summary>防重复挂接标记（TextBox 实例级）。</summary>
    private static readonly DependencyProperty AttachedFlagProperty =
        DependencyProperty.RegisterAttached(
            "AttachedFlag", typeof(bool), typeof(PairCompletion), new PropertyMetadata(false));

    /// <summary>单次输入开始时的现场：用于识别输入法"先提交后通知"路径，并保存被提交吞掉的选中文本。</summary>
    private sealed class ImeState
    {
        public string? TextAtStart;      // PreviewTextInputStart 时的文本（null=本次输入未记录）
        public int SelStartAtStart;      // 输入开始时的选区起点
        public int SelLenAtStart;        // 输入开始时的选区长度
        public string? SelectedAtStart;  // 输入开始时的选中文本（无选中为 null）
        /// <summary>最近一次已知的非空选区。TSF 输入法（中文模式）在组合开始前会先清除/移动选区，
        /// 导致 PreviewTextInputStart 记录不到选中现场；此处兜底保存，供 IME 分支恢复包裹（有效性由文本结构校验把关）。</summary>
        public int LastSelStart;
        public int LastSelLen;
        public string? LastSelectedText;
        /// <summary>最近一次 IME"符号替换选中文本"的输入前现场（原文+原选区）。WPF Undo 只恢复文本与光标、
        /// 不恢复选区，且补全/跳过会产生多个撤销单元——保存前后快照由 UI 层 Ctrl+Z 兜底：循环 Undo 到原文 + Select 恢复选中。</summary>
        public string? BeforeText;
        public int BeforeStart;
        public int BeforeLen;
        /// <summary>输入后快照（文本+光标）：包裹/跳过/替换后的最终态，用于识别"符号操作后未做其他操作"。</summary>
        public string? AfterText;
        public int AfterCaret = -1;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State", typeof(ImeState), typeof(PairCompletion), new PropertyMetadata(null));

    public static void Attach(TextBox tb)
    {
        if ((bool)tb.GetValue(AttachedFlagProperty)) return;
        tb.SetValue(AttachedFlagProperty, true);
        TextCompositionManager.AddPreviewTextInputStartHandler(tb, OnPreviewTextInputStart);
        tb.PreviewTextInput += OnPreviewTextInput;
        tb.SelectionChanged += OnSelectionChanged;
    }

    private static ImeState GetState(TextBox tb)
    {
        if (tb.GetValue(StateProperty) is not ImeState s)
        {
            s = new ImeState();
            tb.SetValue(StateProperty, s);
        }
        return s;
    }

    /// <summary>查询最近一次 IME"符号替换选中文本"的输入前后快照；非该场景返回 false。
    /// 供内容框 Ctrl+Z 一次性撤销整个符号操作（循环 Undo 到原文 + Select 恢复选中）使用。</summary>
    internal static bool TryGetLastSymbolEditState(TextBox tb,
        out string beforeText, out int beforeStart, out int beforeLen,
        out string afterText, out int afterCaret)
    {
        beforeText = "";
        beforeStart = 0;
        beforeLen = 0;
        afterText = "";
        afterCaret = -1;
        var s = GetState(tb);
        if (s.BeforeText == null || s.AfterText == null || s.AfterCaret < 0) return false;
        beforeText = s.BeforeText;
        beforeStart = s.BeforeStart;
        beforeLen = s.BeforeLen;
        afterText = s.AfterText;
        afterCaret = s.AfterCaret;
        return true;
    }

    /// <summary>记录最近一次非空选区（空选区不覆盖，保留最后已知值供输入法吞选区的兜底）。</summary>
    private static void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        var tb = (TextBox)sender;
        if (tb.SelectionLength <= 0) return;
        var s = GetState(tb);
        s.LastSelStart = tb.SelectionStart;
        s.LastSelLen = tb.SelectionLength;
        s.LastSelectedText = tb.SelectedText;
    }

    /// <summary>记录本次输入开始时的文本与选区。任何文本输入（键盘/输入法）都会先经此事件，早于文本落地。</summary>
    private static void OnPreviewTextInputStart(object sender, TextCompositionEventArgs e)
    {
        var tb = (TextBox)sender;
        var s = GetState(tb);
        s.TextAtStart = tb.Text;
        s.SelStartAtStart = tb.SelectionStart;
        s.SelLenAtStart = tb.SelectionLength;
        s.SelectedAtStart = tb.SelectionLength > 0 ? tb.SelectedText : null;
        // 输入法（TSF）可能在组合开始前已清除选区：用最后已知非空选区兜底（须与当前文本位置一致，
        // 防止过期选区误用——如补闭符号时本无选中，但 LastSel 仍是此前包裹操作留下的选区）；有效性由 IME 分支的文本结构校验把关
        if (s.SelLenAtStart == 0 && s.LastSelLen > 0
            && s.LastSelectedText != null
            && tb.Text.Length >= s.LastSelStart + s.LastSelLen
            && tb.Text.Substring(s.LastSelStart, s.LastSelLen) == s.LastSelectedText)
        {
            s.SelStartAtStart = s.LastSelStart;
            s.SelLenAtStart = s.LastSelLen;
            s.SelectedAtStart = s.LastSelectedText;
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Length != 1) return;   // 多字符（词组/粘贴）不处理
        char ch = e.Text[0];
        var tb = (TextBox)sender;
        var st = GetState(tb);
        string? textAtStart = st.TextAtStart;
        // 文本在事件前已变化 → 输入法（TSF）已把字符写入文本，进入"提交后修复"路径
        bool imeCommitted = textAtStart != null && tb.Text != textAtStart;
        var edit = ComputePairEdit(tb.Text, tb.SelectionStart, tb.SelectionLength, ch,
                                   imeCommitted, st.SelectedAtStart, st.SelStartAtStart, st.SelLenAtStart,
                                   textAtStart);
        st.TextAtStart = null;   // 现场仅用于单次输入
        if (edit is { } e2)
        {
            e.Handled = true;
            if (e2.NewText != null) tb.Text = e2.NewText;
            if (e2.Caret != null) { tb.SelectionStart = e2.Caret.Value; tb.SelectionLength = 0; }
        }
        // IME 符号替换选中文本（含包裹/补右/跳过/直接替换）现场：保存输入前原文+原选区 与 输入后文本+光标，
        // 供内容框 Ctrl+Z 循环 Undo 到原文并手动恢复选中（WPF Undo 不恢复选区、单元数不定）。直接键盘路径不记录。
        if (imeCommitted && st.SelectedAtStart is { Length: > 0 }
            && (OpenToClose.ContainsKey(ch) || ClosingChars.Contains(ch)))
        {
            st.BeforeText = textAtStart;
            st.BeforeStart = st.SelStartAtStart;
            st.BeforeLen = st.SelLenAtStart;
            st.AfterText = tb.Text;
            st.AfterCaret = tb.SelectionStart;
        }
        // 已有符号编辑现场且本次为"跳过/补右"类（结果文本与快照一致，仅光标移动，如《东大》后补》跳过）：
        // 同步快照光标，保证 Ctrl+Z 仍能精确匹配"补全后未做其他操作"并一次撤销到原文。
        if (st.BeforeText != null && st.AfterText != null
            && edit is { } e3 && e3.NewText != null && st.AfterText == e3.NewText
            && e3.Caret is int caret3 && caret3 != st.AfterCaret)
        {
            st.AfterCaret = caret3;
        }
    }

    /// <summary>
    /// 计算成对符号输入后的编辑结果（纯函数，便于测试）。
    /// imeCommitted=true：字符已被输入法写入文本（位于光标前），按"提交后"状态修复（补右符/跳过/选区包裹恢复）；
    /// 否则视为直接键盘输入（文本尚未变化）：有选中→包裹，光标后是右符号→跳过，否则补对。
    /// textAtStart：输入开始时的原文（PreviewTextInputStart 现场），仅 imeCommitted 的选区包裹恢复使用，
    /// 用于验证"提交后文本 == 原文删掉选中段后插入 ch"，防止过期选区/未替换场景误包裹。
    /// </summary>
    public static PairEdit? ComputePairEdit(
        string text, int selStart, int selLen, char ch,
        bool imeCommitted, string? selectedAtStart, int selStartAtStart, int selLenAtStart,
        string? textAtStart)
    {
        if (!OpenToClose.TryGetValue(ch, out char close) && !ClosingChars.Contains(ch))
            return null;

        if (imeCommitted)
        {
            // 输入法已把字符写入文本（位于光标前）：核对提交字符确在光标前
            if (selLen != 0 || selStart <= 0 || selStart > text.Length || text[selStart - 1] != ch)
                return null;
            // 本次提交吞掉了输入开始时的选中文本 → 用保存的选区恢复包裹。
            // 验证：提交后文本 == 原文删掉选中段后插入 ch（不依赖光标位置假设，覆盖输入法先清除选区等场景）；
            // 结构不符（过期选区/未替换）时不做包裹，保持原有补右符等行为。
            if (selLenAtStart > 0 && selectedAtStart != null
                && textAtStart != null
                && selStartAtStart >= 0 && selStartAtStart + selLenAtStart <= textAtStart.Length
                && text == textAtStart[..selStartAtStart] + ch + textAtStart[(selStartAtStart + selLenAtStart)..]
                && OpenToClose.TryGetValue(ch, out char wrapClose))
            {
                int openPos = selStart - 1;
                return new PairEdit(
                    text.Substring(0, openPos) + ch + selectedAtStart + wrapClose + text.Substring(selStart),
                    openPos + 1 + selectedAtStart.Length);   // 光标在内容后、右符前
            }
            if (OpenToClose.TryGetValue(ch, out close))
            {
                // 开符号（含自配对）：自配对且光标后同符号 → 跳过（删掉刚写入的字符）；否则补右符
                if (close == ch && selStart < text.Length && text[selStart] == ch)
                    return new PairEdit(text.Remove(selStart - 1, 1), selStart);
                return new PairEdit(text.Insert(selStart, close.ToString()), selStart);
            }
            // 非自配对闭符号：光标后同符号 → 跳过（删掉刚写入的字符）；否则保持原样
            if (selStart < text.Length && text[selStart] == ch)
                return new PairEdit(text.Remove(selStart - 1, 1), selStart);
            return null;
        }

        // 直接键盘输入：文本尚未变化
        if (OpenToClose.TryGetValue(ch, out close))
        {
            if (selLen > 0)
                return new PairEdit(
                    text.Substring(0, selStart) + ch + text.Substring(selStart, selLen) + close + text.Substring(selStart + selLen),
                    selStart + 1 + selLen);   // 包裹选中：光标在内容后、右符前
            if (close == ch && selStart < text.Length && text[selStart] == ch)
                return new PairEdit(null, selStart + 1);   // 自配对：光标后同符号 → 跳过
            return new PairEdit(text.Substring(0, selStart) + ch + close + text.Substring(selStart), selStart + 1);
        }
        // 闭符号：光标后同符号 → 跳过；否则不干预（正常输入）
        if (selStart < text.Length && text[selStart] == ch)
            return new PairEdit(null, selStart + 1);
        return null;
    }
}
