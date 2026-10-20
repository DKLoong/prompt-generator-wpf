using PromptGenerator.Controls;
using Xunit;

namespace PromptGenerator.Tests;

/// <summary>
/// PairCompletion.ComputePairEdit 的单元测试。
/// imeCommitted=false 模拟直接键盘输入（文本尚未落地）；
/// imeCommitted=true 模拟输入法（TSF）提交（字符已在光标前，PreviewTextInput 仅为事后通知）。
/// </summary>
public class PairCompletionTests
{
    // ---------------- 直接键盘输入（imeCommitted=false） ----------------

    [Fact]
    public void 键盘_空框输入左括号_补右括号光标居中()
    {
        var r = PairCompletion.ComputePairEdit("", 0, 0, '(', false, null, 0, 0);

        Assert.NotNull(r);
        Assert.Equal("()", r.Value.NewText);
        Assert.Equal(1, r.Value.Caret);
    }

    [Fact]
    public void 键盘_文本中插入左花括号_补右括号()
    {
        var r = PairCompletion.ComputePairEdit("ab", 2, 0, '{', false, null, 2, 0);

        Assert.NotNull(r);
        Assert.Equal("ab{}", r.Value.NewText);
        Assert.Equal(3, r.Value.Caret);
    }

    [Fact]
    public void 键盘_选中文本输入引号_包裹选中()
    {
        var r = PairCompletion.ComputePairEdit("xxabcyy", 2, 3, '"', false, null, 2, 3);

        Assert.NotNull(r);
        Assert.Equal("xx\"abc\"yy", r.Value.NewText);
        Assert.Equal(6, r.Value.Caret);   // 光标在内容后、右引号前
    }

    [Fact]
    public void 键盘_圆括号内输入右括号_跳过不重复()
    {
        var r = PairCompletion.ComputePairEdit("()", 1, 0, ')', false, null, 1, 0);

        Assert.NotNull(r);
        Assert.Null(r.Value.NewText);     // 文本不变
        Assert.Equal(2, r.Value.Caret);   // 光标越过右括号
    }

    [Fact]
    public void 键盘_引号对内输入引号_跳过不重复()
    {
        var r = PairCompletion.ComputePairEdit("\"\"", 1, 0, '"', false, null, 1, 0);

        Assert.NotNull(r);
        Assert.Null(r.Value.NewText);
        Assert.Equal(2, r.Value.Caret);
    }

    [Fact]
    public void 键盘_光标前已是左括号_仍正常补对不误判()
    {
        // 回归防护：直接键盘输入时文本尚未变化，光标前的"("是已存在文本，不能误判为输入法已提交
        var r = PairCompletion.ComputePairEdit("((", 2, 0, '(', false, null, 2, 0);

        Assert.NotNull(r);
        Assert.Equal("((()", r.Value.NewText);
        Assert.Equal(3, r.Value.Caret);
    }

    [Fact]
    public void 键盘_输入孤立右括号_不干预()
    {
        var r = PairCompletion.ComputePairEdit("abc", 3, 0, ')', false, null, 3, 0);

        Assert.Null(r);
    }

    [Fact]
    public void 键盘_非成对符号_不干预()
    {
        var r = PairCompletion.ComputePairEdit("abc", 3, 0, 'x', false, null, 3, 0);

        Assert.Null(r);
    }

    // ---------------- 输入法提交（imeCommitted=true） ----------------

    [Fact]
    public void IME_输入左括号_字符已落地_补右括号()
    {
        var r = PairCompletion.ComputePairEdit("(", 1, 0, '(', true, null, 0, 0);

        Assert.NotNull(r);
        Assert.Equal("()", r.Value.NewText);
        Assert.Equal(1, r.Value.Caret);
    }

    [Fact]
    public void IME_圆括号内输入右括号_删除重复并跳过()
    {
        // 原"()"光标在中间，输入法提交")"后文本为"())"，应删掉刚写入的")"回到"()"
        var r = PairCompletion.ComputePairEdit("())", 2, 0, ')', true, null, 1, 0);

        Assert.NotNull(r);
        Assert.Equal("()", r.Value.NewText);
        Assert.Equal(2, r.Value.Caret);
    }

    [Fact]
    public void IME_选中文本输入书名号_用保存的选区恢复包裹()
    {
        // 原选中"abc"（0,3），输入法提交"《"吞掉选区，文本变"《"，光标1
        var r = PairCompletion.ComputePairEdit("《", 1, 0, '《', true, "abc", 0, 3);

        Assert.NotNull(r);
        Assert.Equal("《abc》", r.Value.NewText);
        Assert.Equal(4, r.Value.Caret);   // 光标在内容后、右符前
    }

    [Fact]
    public void IME_选中文本输入全角括号_恢复包裹()
    {
        var r = PairCompletion.ComputePairEdit("（", 1, 0, '（', true, "abc", 0, 3);

        Assert.NotNull(r);
        Assert.Equal("（abc）", r.Value.NewText);
        Assert.Equal(4, r.Value.Caret);
    }

    [Fact]
    public void IME_选中文本但提交字符不在原选区起点_不包裹仅补右符()
    {
        // 防御：提交字符不在 selStartAtStart 处（现场不符），不应误包裹
        var r = PairCompletion.ComputePairEdit("x《", 2, 0, '《', true, "abc", 0, 3);

        Assert.NotNull(r);
        Assert.Equal("x《》", r.Value.NewText);
        Assert.Equal(2, r.Value.Caret);
    }

    [Fact]
    public void IME_自配对符号光标后同符号_删除重复并跳过()
    {
        // 原"\"\""光标在中间，输入法提交"\"后文本为"\"\"\""，应删掉刚写入的引号
        var r = PairCompletion.ComputePairEdit("\"\"\"", 2, 0, '"', true, null, 1, 0);

        Assert.NotNull(r);
        Assert.Equal("\"\"", r.Value.NewText);
        Assert.Equal(2, r.Value.Caret);
    }

    [Fact]
    public void IME_自配对符号无后续同符_补右符号()
    {
        var r = PairCompletion.ComputePairEdit("x\"", 2, 0, '"', true, null, 1, 0);

        Assert.NotNull(r);
        Assert.Equal("x\"\"", r.Value.NewText);
        Assert.Equal(2, r.Value.Caret);
    }

    [Fact]
    public void IME_输入孤立右括号_保持原样()
    {
        var r = PairCompletion.ComputePairEdit(")", 1, 0, ')', true, null, 0, 0);

        Assert.Null(r);
    }

    [Fact]
    public void IME_提交字符不在光标前_不干预()
    {
        var r = PairCompletion.ComputePairEdit("a(", 1, 0, '(', true, null, 0, 0);

        Assert.Null(r);
    }
}
