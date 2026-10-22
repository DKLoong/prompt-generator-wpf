using System.IO;
using System.Text.Json;
using PromptGenerator.Models;

namespace PromptGenerator.Services;

/// <summary>模板 JSON 读写（数据目录统一走 AppPaths：便携优先 exe 旁 data\，不可写回退 %LOCALAPPDATA%）。</summary>
public class TemplateStore
{
    private readonly string _dir;
    private readonly string _file;
    private TemplateStoreData? _cache;

    public TemplateStore()
    {
        _dir = AppPaths.DataDir;
        _file = Path.Combine(_dir, "templates.json");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public TemplateStoreData Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(_file))
            {
                var json = File.ReadAllText(_file);
                _cache = JsonSerializer.Deserialize<TemplateStoreData>(json, JsonOpts) ?? new TemplateStoreData();
            }
            else
            {
                _cache = new TemplateStoreData();
            }
        }
        catch (Exception ex)
        {
            Logging.Warn($"模板加载失败，使用默认模板：{ex.Message}");
            _cache = new TemplateStoreData();
        }
        // 保证至少有一个模板（预置模板：仅首次启动无数据时写入全部，之后以用户数据为准，可自由更改/删除/保存）
        if (_cache.Templates.Count == 0)
        {
            foreach (var t in CreatePresetTemplates())
                _cache.Templates.Add(t);
        }
        return _cache;
    }

    public void Save(TemplateStoreData data)
    {
        string tmp = _file + ".tmp";
        try
        {
            // 原子写：先写临时文件、再同卷 Move 覆盖目标（NTFS rename 原子替换）。
            // 便携场景（U 盘拔出/断电/杀软扫描冲突）下避免直接 WriteAllText 写坏 JSON。
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOpts));
            File.Move(tmp, _file, overwrite: true);
            _cache = data;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不影响主流程 */ }
            Logging.Error($"模板保存失败：{ex.Message}");
            throw new IOException($"保存模板失败: {ex.Message}", ex);
        }
    }

    /// <summary>预置模板集：通用任务 + 13 个日常场景（信息检索/文档编辑/图片生成等）。
    /// 仅首次启动（无数据文件）时写入；之后用户可自由更改、删除、保存（数据文件为准，不会补回）。</summary>
    private static List<Template> CreatePresetTemplates()
    {
        return new List<Template>
        {
            new()
            {
                Id = "tpl-general", Name = "通用任务",
                Fields =
                {
                    new Field { Name = "任务描述", Items = { "一句话说清楚要做什么" } },
                    new Field { Name = "背景", Items = { "当前项目 / 文件 / 上下文" } },
                    new Field { Name = "约束", Items = { "技术栈、不能动的文件、兼容性要求" } },
                    new Field { Name = "验收", Items = { "怎么算完成（命令、测试、预期效果）" } },
                }
            },
            new()
            {
                Id = "tpl-search", Name = "信息检索",
                Fields =
                {
                    new Field { Name = "检索目标", Items = { "要查什么（具体问题 / 主题）" } },
                    new Field { Name = "检索范围", Items = { "网站 / 语言 / 文档类型限制" } },
                    new Field { Name = "时间范围", Items = { "数据时效要求（如近一年）" } },
                    new Field { Name = "输出要求", Items = { "格式、条数、是否附来源链接" } },
                }
            },
            new()
            {
                Id = "tpl-docedit", Name = "文档编辑",
                Fields =
                {
                    new Field { Name = "原文 / 主题", Items = { "要写或改的文档内容" } },
                    new Field { Name = "目标读者", Items = { "谁看（领导 / 客户 / 同事 / 公众）" } },
                    new Field { Name = "风格要求", Items = { "正式 / 口语 / 简洁 / 文采" } },
                    new Field { Name = "字数与结构", Items = { "篇幅、段落层级、是否要表格" } },
                }
            },
            new()
            {
                Id = "tpl-image", Name = "图片生成",
                Fields =
                {
                    new Field { Name = "画面主体", Items = { "画什么（人物 / 场景 / 物体）" } },
                    new Field { Name = "风格", Items = { "写实 / 插画 / 国风 / 3D 等" } },
                    new Field { Name = "构图与比例", Items = { "横竖、视角、布局（如居中）" } },
                    new Field { Name = "细节约束", Items = { "色彩、光影、文字、禁忌元素" } },
                }
            },
            new()
            {
                Id = "tpl-codegen", Name = "代码生成",
                Fields =
                {
                    new Field { Name = "功能需求", Items = { "实现什么功能" } },
                    new Field { Name = "语言 / 技术栈", Items = { "语言、框架、版本" } },
                    new Field { Name = "输入输出", Items = { "入参、返回、接口签名" } },
                    new Field { Name = "边界与异常", Items = { "空值、错误处理、性能" } },
                    new Field { Name = "输出格式", Items = { "完整代码 / 示例 / 含解释" } },
                }
            },
            new()
            {
                Id = "tpl-debug", Name = "代码调试",
                Fields =
                {
                    new Field { Name = "现象", Items = { "报错信息 / 错误行为" } },
                    new Field { Name = "期望行为", Items = { "应该是什么样" } },
                    new Field { Name = "环境与复现", Items = { "版本、触发步骤（最小路径）" } },
                    new Field { Name = "已尝试", Items = { "试过什么，结果如何" } },
                    new Field { Name = "调试要求", Items = { "根因定位、最小改动、不引入新问题" } },
                }
            },
            new()
            {
                Id = "tpl-translate", Name = "翻译",
                Fields =
                {
                    new Field { Name = "原文", Items = { "待翻译的内容" } },
                    new Field { Name = "目标语言", Items = { "译成什么语言" } },
                    new Field { Name = "语气与术语", Items = { "正式 / 口语、专业术语处理（保留原文标注）" } },
                    new Field { Name = "格式要求", Items = { "分段、保留排版、字数" } },
                }
            },
            new()
            {
                Id = "tpl-summarize", Name = "总结归纳",
                Fields =
                {
                    new Field { Name = "素材", Items = { "文章 / 会议 / 报告内容" } },
                    new Field { Name = "总结重点", Items = { "要点、数据、结论哪些必须保留" } },
                    new Field { Name = "输出格式", Items = { "段落 / 要点 / 表格" } },
                    new Field { Name = "长度", Items = { "一句话 / 百字 / 全文摘要" } },
                }
            },
            new()
            {
                Id = "tpl-brainstorm", Name = "头脑风暴",
                Fields =
                {
                    new Field { Name = "主题", Items = { "要发散什么" } },
                    new Field { Name = "背景与限制", Items = { "预算、时间、资源约束" } },
                    new Field { Name = "数量与方向", Items = { "要几个点子、往哪些方向想" } },
                    new Field { Name = "评估标准", Items = { "怎么筛选（可行性 / 成本 / 收益）" } },
                }
            },
            new()
            {
                Id = "tpl-data", Name = "数据分析",
                Fields =
                {
                    new Field { Name = "数据说明", Items = { "数据来源、字段、规模" } },
                    new Field { Name = "分析目标", Items = { "回答什么问题" } },
                    new Field { Name = "分析方法", Items = { "趋势 / 对比 / 占比 / 归因" } },
                    new Field { Name = "输出要求", Items = { "图表、结论、建议、数据口径" } },
                }
            },
            new()
            {
                Id = "tpl-email", Name = "邮件撰写",
                Fields =
                {
                    new Field { Name = "收件人与关系", Items = { "上级 / 客户 / 同事" } },
                    new Field { Name = "目的", Items = { "通知 / 请求 / 汇报 / 感谢" } },
                    new Field { Name = "要点", Items = { "必须包含的信息" } },
                    new Field { Name = "语气与格式", Items = { "正式 / 礼貌 / 简洁、主题与署名" } },
                }
            },
            new()
            {
                Id = "tpl-learn", Name = "学习辅导",
                Fields =
                {
                    new Field { Name = "知识点 / 问题", Items = { "学什么 / 哪里不懂" } },
                    new Field { Name = "学习者背景", Items = { "年级 / 基础 / 已有知识" } },
                    new Field { Name = "教学方式", Items = { "例子 / 类比 / 分步骤 / 图示" } },
                    new Field { Name = "输出要求", Items = { "讲解 / 练习 / 答疑" } },
                }
            },
            new()
            {
                Id = "tpl-meeting", Name = "会议纪要",
                Fields =
                {
                    new Field { Name = "会议信息", Items = { "主题、时间、参会人" } },
                    new Field { Name = "议程与发言", Items = { "讨论要点、各方意见" } },
                    new Field { Name = "结论与决议", Items = { "达成的一致、决定事项" } },
                    new Field { Name = "待办", Items = { "负责人、截止时间" } },
                }
            },
            new()
            {
                Id = "tpl-marketing", Name = "营销文案",
                Fields =
                {
                    new Field { Name = "产品 / 活动", Items = { "推广什么" } },
                    new Field { Name = "目标受众", Items = { "谁看、痛点是什么" } },
                    new Field { Name = "平台与格式", Items = { "公众号 / 小红书 / 朋友圈、图文 / 短句" } },
                    new Field { Name = "卖点与调性", Items = { "核心卖点、语气风格、字数" } },
                }
            },
        };
    }
}
