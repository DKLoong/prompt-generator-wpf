using PromptGenerator.Models;
using PromptGenerator.Services;
using Xunit;

namespace PromptGenerator.Tests;

/// <summary>
/// PromptBuilder.Build 定稿格式与标点规则 v2 的单元测试。
/// 规则（与实现注释一致）：
///   - 格式：## 字段名：\n1. 内容；\n2. 内容。\n（字段间不空行）
///   - 空字段名 / 空内容项整体跳过；全部为空返回 null
///   - 非末条子项：结尾无标点加"；"（已有任意标点不加）
///   - 末条子项：无标点加"。"；已"。"/"!?…"结尾不加；"，、；：",;: 替换为"。"；
///     引号/括号包裹结尾先剥包裹再判断，句号加在最外层
/// </summary>
public class PromptBuilderTests
{
    private static Field Field(string name, params string[] items) =>
        new() { Name = name, Items = items.ToList() };

    /// <summary>断言 Build 结果。ignoreLineEndingDifferences：实现用 AppendLine（Windows 为 \r\n），期望串用 \n 书写更可读。</summary>
    private static void AssertBuild(string expected, string? actual)
        => Assert.Equal(expected, actual, ignoreLineEndingDifferences: true);

    // ---------------- 基本拼接 ----------------

    [Fact]
    public void Build_多字段多条目_按定稿格式拼接()
    {
        var fields = new List<Field>
        {
            Field("要求", "写一段", "要有结论"),
            Field("风格", "正式"),
        };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 要求：\n1. 写一段；\n2. 要有结论。\n## 风格：\n1. 正式。", result);
    }

    [Fact]
    public void Build_字段名去首尾空白()
    {
        var fields = new List<Field> { Field("  字段  ", "内容") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. 内容。", result);
    }

    [Fact]
    public void Build_内容去首尾空白()
    {
        var fields = new List<Field> { Field("字段", "  内容  ") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. 内容。", result);
    }

    // ---------------- 空值跳过 ----------------

    [Fact]
    public void Build_空字段名_跳过整个字段()
    {
        var fields = new List<Field>
        {
            Field("  ", "内容"),
            Field("有效", "内容"),
        };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 有效：\n1. 内容。", result);
    }

    [Fact]
    public void Build_空内容项_跳过且编号不空缺()
    {
        var fields = new List<Field> { Field("字段", "有效", "   ", "") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. 有效。", result);
    }

    [Fact]
    public void Build_全部为空_返回null()
    {
        var fields = new List<Field>
        {
            Field("", "内容"),
            Field("字段", "   "),
        };

        Assert.Null(PromptBuilder.Build(fields));
    }

    // ---------------- 末条标点规则 ----------------

    [Theory]
    [InlineData("写一段", "## 字段：\n1. 写一段。")]      // 无标点 → 加句号
    [InlineData("写一段。", "## 字段：\n1. 写一段。")]    // 已有句号 → 不加
    [InlineData("太棒了！", "## 字段：\n1. 太棒了！")]    // 感叹号 → 不加
    [InlineData("继续…", "## 字段：\n1. 继续…")]         // 省略号 → 不加
    [InlineData("写一段，", "## 字段：\n1. 写一段。")]    // 中文逗号 → 替换句号
    [InlineData("写一段、", "## 字段：\n1. 写一段。")]    // 顿号 → 替换句号
    [InlineData("写一段；", "## 字段：\n1. 写一段。")]    // 分号 → 替换句号
    [InlineData("写一段：", "## 字段：\n1. 写一段。")]    // 冒号 → 替换句号
    [InlineData("hello,", "## 字段：\n1. hello。")]       // 英文逗号 → 替换句号
    [InlineData("hello;", "## 字段：\n1. hello。")]       // 英文分号 → 替换句号
    [InlineData("hello:", "## 字段：\n1. hello。")]       // 英文冒号 → 替换句号
    [InlineData("hello.", "## 字段：\n1. hello.")]        // 英文句号 → 不加
    [InlineData("hello!", "## 字段：\n1. hello!")]        // 英文感叹号 → 不加
    [InlineData("hello?", "## 字段：\n1. hello?")]        // 英文问号 → 不加
    public void Build_末条标点规则(string item, string expected)
    {
        var fields = new List<Field> { Field("字段", item) };

        var result = PromptBuilder.Build(fields);

        AssertBuild(expected, result);
    }

    // ---------------- 非末条标点规则 ----------------

    [Fact]
    public void Build_非末条无标点_加中文分号()
    {
        var fields = new List<Field> { Field("字段", "第一", "第二") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. 第一；\n2. 第二。", result);
    }

    [Fact]
    public void Build_非末条已有标点_不加分号()
    {
        var fields = new List<Field> { Field("字段", "第一。", "第二") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. 第一。\n2. 第二。", result);
    }

    // ---------------- 包裹符边界 ----------------

    [Fact]
    public void Build_引号包裹且内层可替换标点_句号加在引号外()
    {
        var fields = new List<Field> { Field("字段", "“你好，”") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. “你好”。", result);
    }

    [Fact]
    public void Build_引号包裹无内层标点_句号加在引号外()
    {
        var fields = new List<Field> { Field("字段", "“你好”") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. “你好”。", result);
    }

    [Fact]
    public void Build_括号包裹_句号加在括号外()
    {
        var fields = new List<Field> { Field("字段", "（重要）") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. （重要）。", result);
    }

    [Fact]
    public void Build_多层包裹_句号加在最外层()
    {
        var fields = new List<Field> { Field("字段", "“（重要）”") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. “（重要）”。", result);
    }

    // ---------------- 多条目编号 ----------------

    [Fact]
    public void Build_多条子项_编号连续()
    {
        var fields = new List<Field> { Field("字段", "一", "二", "三") };

        var result = PromptBuilder.Build(fields);

        AssertBuild("## 字段：\n1. 一；\n2. 二；\n3. 三。", result);
    }
}
