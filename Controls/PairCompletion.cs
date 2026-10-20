using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PromptGenerator.Controls;

/// <summary>
/// 成对符号自动补全（字段框/内容框通用）：
/// 1) 输入左符号 → 自动补右符号，光标停在中间；光标后已是右符号时，输入右符号直接跳过不重复；
/// 2) 有选中文本时输入左符号 → 选中内容被包裹进成对符号（光标停在包裹内容之后、右符号之前）。
/// 通过 PreviewTextInput 拦截单字符输入实现，不影响 IME 组合输入、粘贴与其他按键。
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

    /// <summary>防重复挂接标记（TextBox 实例级）。</summary>
    private static readonly DependencyProperty AttachedFlagProperty =
        DependencyProperty.RegisterAttached(
            "AttachedFlag", typeof(bool), typeof(PairCompletion), new PropertyMetadata(false));

    public static void Attach(TextBox tb)
    {
        if ((bool)tb.GetValue(AttachedFlagProperty)) return;
        tb.SetValue(AttachedFlagProperty, true);
        tb.PreviewTextInput += OnPreviewTextInput;
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Length != 1) return;   // 仅处理单字符输入
        char ch = e.Text[0];
        var tb = (TextBox)sender;
        int selStart = tb.SelectionStart;
        int selLen = tb.SelectionLength;

        if (OpenToClose.TryGetValue(ch, out char close))
        {
            if (close == ch)
            {
                // 自配对符号（" ' `）：有选中→包裹；光标后同符号→跳过；否则补对
                if (selLen > 0) { Wrap(tb, selStart, selLen, ch); e.Handled = true; return; }
                if (selStart < tb.Text.Length && tb.Text[selStart] == ch) { Skip(tb, selStart); e.Handled = true; return; }
                InsertPair(tb, selStart, ch, close);
                e.Handled = true;
                return;
            }
            // 非自配对开符号：有选中→包裹；否则补对
            if (selLen > 0) { Wrap(tb, selStart, selLen, ch); e.Handled = true; return; }
            InsertPair(tb, selStart, ch, close);
            e.Handled = true;
            return;
        }

        // 非自配对闭符号：无选中且光标后紧邻同符号时跳过；其余（含选中替换）保持正常输入
        if (ClosingChars.Contains(ch)
            && selLen == 0
            && selStart < tb.Text.Length && tb.Text[selStart] == ch)
        {
            Skip(tb, selStart);
            e.Handled = true;
        }
    }

    private static void Wrap(TextBox tb, int selStart, int selLen, char open)
    {
        char close = OpenToClose[open];
        string selected = tb.SelectedText;
        tb.SelectedText = open + selected + close;
        tb.SelectionStart = selStart + 1 + selLen;   // 光标停在包裹内容之后、右符号之前
        tb.SelectionLength = 0;
    }

    private static void InsertPair(TextBox tb, int selStart, char open, char close)
    {
        tb.SelectedText = open.ToString() + close;
        tb.SelectionStart = selStart + 1;   // 光标停在左右符号中间
        tb.SelectionLength = 0;
    }

    private static void Skip(TextBox tb, int selStart)
    {
        tb.SelectionStart = selStart + 1;
        tb.SelectionLength = 0;
    }
}
