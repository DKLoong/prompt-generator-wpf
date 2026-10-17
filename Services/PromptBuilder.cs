using System.Text;
using PromptGenerator.Models;

namespace PromptGenerator.Services;

/// <summary>
/// 按定稿格式与标点规则 v2 拼接提示词。
/// 定稿规则：
///   - 格式：## 字段名：1. 内容；2. 内容。## 下一字段：…
///   - 字段间、内容间不换行；空内容字段整体跳过
///   - 子项编号（1. 2. 3.）拼入内容
///   - 非末条子项：结尾无标点加"；"（已有任意标点则不加）
///   - 末条子项：无标点加"。"；已"。"不加；"！？…"/".!?…"结尾不加；
///     "，、；："/",;:"替换为"。"；引号/括号结尾先剥离包裹符号再判断，标点加在最外层
/// </summary>
public static class PromptBuilder
{
    private static readonly char[] ReplaceWithPeriod = { '，', '、', '；', '：', ',', ';', ':' };
    private static readonly char[] NoAppendPunct = { '。', '！', '？', '…', '.', '!', '?' };
    private static readonly char[] OpenWrapper = { '“', '‘', '（', '「', '『', '【', '《', '<', '"', '\'', '(' };
    private static readonly char[] CloseWrapper = { '”', '’', '）', '」', '』', '】', '》', '>', '"', '\'', ')' };

    public static string? Build(IEnumerable<Field> fields)
    {
        var sb = new StringBuilder();
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name)) continue;
            var items = field.Items
                .Select(i => i?.Trim() ?? "")
                .Where(i => i.Length > 0)
                .ToList();
            if (items.Count == 0) continue;

            // 标准 Markdown：## 字段名：换行，每条编号项单独一行；字段间不留空行（TUI 友好）
            sb.Append("## ").Append(field.Name.Trim()).Append('：').AppendLine();

            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(i + 1).Append(". ").Append(items[i]);
                bool isLast = i == items.Count - 1;
                ApplyEndPunct(sb, isLast);
                sb.AppendLine();
            }
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    private static void ApplyEndPunct(StringBuilder sb, bool isLast)
    {
        string s = sb.ToString();
        string tail = s;
        int stripped = 0;
        while (tail.Length > 0)
        {
            char last = tail[^1];
            int ci = Array.IndexOf(CloseWrapper, last);
            if (ci < 0) break;
            char open = OpenWrapper[ci];
            int openIdx = tail.LastIndexOf(open);
            if (openIdx < 0 || openIdx >= tail.Length - 1) break;
            stripped++;
            tail = tail.Substring(openIdx + 1, tail.Length - openIdx - 2);
            if (tail.Length == 0) break;
        }

        char end = tail.Length > 0 ? tail[^1] : '\0';
        bool hasPunct = char.IsPunctuation(end) || end == '…';
        bool endsWithPeriod = end == '。' || end == '.';
        bool isReplaceable = Array.IndexOf(ReplaceWithPeriod, end) >= 0;
        bool noAppend = Array.IndexOf(NoAppendPunct, end) >= 0;

        string toAppend;
        if (isLast)
        {
            if (endsWithPeriod || noAppend) toAppend = "";
            else toAppend = "。";
        }
        else
        {
            toAppend = hasPunct ? "" : "；";
        }

        if (toAppend.Length == 0) return;

        string final = s;
        if (isLast && isReplaceable && !endsWithPeriod && stripped > 0)
        {
            // 最外层以包裹符结尾且内层尾标点可替换：
            // 去掉内层可替换标点，把句号追加到最外层闭合符之后
            int removeAt = s.Length - 1 - stripped * 0; // 占位，实际按包裹层数计算
            final = InsertPeriodOutside(s, stripped);
        }
        else if (isLast && isReplaceable && !endsWithPeriod)
        {
            final = s[..^1]; // 去掉末尾可替换标点，再加句号
        }

        sb.Clear().Append(final).Append(toAppend);
    }

    /// <summary>
    /// 当结尾为包裹符号（如 "你好，" 或 "你好"）时，把句号追加到最外层闭合符之后。
    /// stripped：剥掉的包裹层数。逻辑：找到最外层闭合符的位置，在其后插入 toAppend。
    /// </summary>
    private static string InsertPeriodOutside(string s, int stripped)
    {
        // 从末尾向前找 stripped 层闭合符的最外层位置
        int depth = 0;
        int pos = -1;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            int ci = Array.IndexOf(CloseWrapper, s[i]);
            if (ci >= 0)
            {
                depth++;
                if (depth == stripped) { pos = i; break; }
            }
        }
        if (pos < 0) return s;
        // 去掉最外层闭合符之前的内层末尾可替换标点（在 pos 之前最近的一个替换标点）
        int removeAt = -1;
        for (int i = pos - 1; i >= 0; i--)
        {
            if (Array.IndexOf(ReplaceWithPeriod, s[i]) >= 0) { removeAt = i; break; }
        }
        if (removeAt < 0) return s;
        return s.Remove(removeAt, 1);
    }
}
