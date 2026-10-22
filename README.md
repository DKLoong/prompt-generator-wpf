# 提示词生成器 / Prompt Generator

> 便携单文件 WPF 桌面工具：把"填字段 → 拼提示词"固化成模板流程，一键生成标准格式提示词并复制或提交。
> A portable single-file WPF desktop tool that turns "fill fields → compose prompt" into a reusable template workflow — generate a well-formatted prompt with one click, then copy or submit it.

## 特性 / Features

- **模板管理 / Template management**：预置 14 个模板（通用任务 + 13 个日常场景），支持新增、重命名、保存、重置、删除；重置/删除均有确认，删除后可立即撤销。14 built-in templates (general purpose + 13 everyday scenarios); create / rename / save / reset / delete — reset & delete require confirmation, delete is undoable.
- **字段化编辑 / Field-based editing**：每个模板由若干字段（标题 + 编号条目）组成，支持增删行，空框失焦自动清理。Each template is a list of fields (title + numbered items); add/remove rows freely, empty boxes clean up on focus loss.
- **一键生成 / One-click generation**：按定稿格式拼接提示词（`## 字段名：` + 编号条目 + 自动标点），直接可用。Composes prompts in a fixed format (`## Field:` + numbered items + auto punctuation).
- **两种输出 / Two output modes**：「生成并复制」写入剪贴板；「生成并提交」调用 claude CLI 直接生成结果。Copy to clipboard, or submit to the `claude` CLI for a real result.
- **生成历史 / History**：自动记录，可复制、编辑、删除；删除需确认、可撤销；编辑历史基于快照，不污染原模板。Auto-recorded; copy / edit / delete with confirmation & undo; edits work on a snapshot so templates are never polluted.
- **便捷唤起 / Quick access**：全局热键、托盘图标、单实例唤起、贴边收起、置顶。Global hotkey, tray icon, single-instance wake-up, edge docking, pin-on-top.
- **粘贴自动清理 / Paste cleanup**：粘贴到条目时自动去首尾空行、统一换行符、清行首尾空白（可关闭）。Strips leading/trailing blank lines, normalizes newlines and trims line whitespace on paste (configurable).
- **三套主题 / Three themes**：冷灰·松绿（默认）/ 暖灰·靛蓝 / 石墨·低饱和紫，切换即时生效。Cool-Gray·Pine Green (default) / Warm-Gray·Indigo / Graphite·Low-Sat Purple — applies instantly.
- **便携数据 / Portable data**：`data\` 自动创建于 exe 旁；目录不可写时自动回退用户数据目录并迁移数据；保存采用原子写防损坏。`data\` lives next to the EXE; falls back to the user-data dir with auto-migration when read-only; atomic writes guard against corruption.

## 快速开始 / Quick Start

1. 下载单文件 `PromptGenerator.exe`（自包含，无需安装）→ 双击运行。Download the self-contained single-file `PromptGenerator.exe` and run it.
2. 选择模板 → 填写字段 → 按 `Ctrl+Enter` 生成并复制。Pick a template, fill the fields, press `Ctrl+Enter` to generate & copy.
3. 「生成并提交」需先安装并登录 Anthropic 的 `claude` CLI。For "Generate & Submit", install and log in to the `claude` CLI first.

## 操作指南 / Usage Guide

### 鼠标 / Mouse

| 操作 / Action | 说明 / Description |
|---|---|
| 模板选择 / Template picker | 点击模板框文本区进入检索过滤；点下拉箭头展开列表选择。Click the text area to filter; click the arrow to browse. |
| 模板管理 / Template management | 重命名 / 新增 / 保存（`Ctrl+S`）/ 重置 / 删除，对应工具栏按钮。Rename / New / Save (`Ctrl+S`) / Reset / Delete buttons on the toolbar. |
| 字段编辑 / Field editing | 悬停字段间隙或行尾显示 `+` / `×`，点击插入/删除行；空框移焦自动清理。Hover gaps or row ends to reveal `+`/`×`; empty boxes auto-clean on blur. |
| 生成复制 / Generate & copy | 点击「生成并复制」或按 `Ctrl+Enter`。Click "Generate & Copy" or press `Ctrl+Enter`. |
| 历史记录 / History | 标题栏「历史」按钮打开；条目支持复制 / 编辑 / 删除（删除有确认与撤销）。Open via the History button; items support copy / edit / delete (confirmed & undoable). |
| 托盘 / Tray | 左键单击切换显示/隐藏；右键菜单：显示 / 隐藏、设置、退出。Left-click toggles; right-click menu: Show/Hide, Settings, Exit. |
| 贴边收起 / Edge docking | 拖到屏幕边缘自动收起，悬停边缘条展开（设置可关闭）。Drag to screen edge to dock; hover the edge strip to expand (configurable). |

### 键盘 / Keyboard

| 按键 / Key | 功能 / Function |
|---|---|
| `Ctrl+Alt+Shift+P`（默认） | 全局显示 / 隐藏窗口。Global show/hide. |
| `Ctrl+Enter` | 生成并复制。Generate & copy. |
| `Ctrl+S` | 保存到模板。Save template. |
| `↓` / `↑` / `Enter` / `Esc` | 模板下拉浏览 / 确认 / 逐级退出。Browse / confirm / exit the dropdown. |
| `Enter` | 内容框内新增条目；`Shift+Enter` 换行。New item; `Shift+Enter` for a soft line break. |
| `Tab` | 按钮间键盘导航（聚焦即时显示提示）。Navigate buttons with tooltips shown on focus. |

历史记录窗：`Tab` 在关闭 / 复制 / 编辑 / 删除间循环；删除后焦点移至「撤销」按钮，提示消失后移回相邻条目的「复制」按钮。
History window: `Tab` cycles Close / Copy / Edit / Delete; after delete, focus moves to "Undo", then back to the neighboring item's "Copy" when the toast dismisses.

## 设置 / Settings

通过标题栏 ⚙ 打开。Opened from the title-bar gear.

| 设置项 / Setting | 默认 / Default | 说明 / Description |
|---|---|---|
| 全局热键 / Global hotkey | `Ctrl+Alt+Shift+P` | 点击热键框后按下组合键自定义。Click the box and press a combo to customize. |
| 贴边收起 / Edge dock | 开启 / On | 拖到屏幕边缘自动收起。Auto-dock at screen edges. |
| 最近使用置顶 / Pin recent | 开启 / On | 最近使用的 5 个模板排到下拉最前。Top 5 recent templates float to the top. |
| 粘贴自动清理 / Paste cleanup | 开启 / On | 去首尾空行、统一 `\n`、清行首尾空白。Trim blank lines, normalize newlines, trim line whitespace. |
| 界面主题 / Theme | 冷灰·松绿 / Cool-Gray·Pine Green | 三种主题即时切换，无需重启。Three themes, instant switch, no restart. |

## 数据与目录 / Data & Directories

| 路径 / Path | 说明 / Description |
|---|---|
| `data\templates.json` | 模板数据（预置 + 用户模板）。Templates (built-in + user). |
| `data\settings.json` | 设置与窗口位置记忆。Settings & window-position memory. |
| `data\logs\app-日期.log` | 按天分文件的运行日志，保留 7 天。Daily log files, 7-day retention. |
| `data\crash.log` | 崩溃记录。Crash log. |

数据目录优先选择 exe 旁 `data\`（便携）；目录不可写时回退到 `%LOCALAPPDATA%\PromptGenerator\data` 并自动迁移已有数据。
Data lives next to the EXE by default (portable); falls back to `%LOCALAPPDATA%\PromptGenerator\data` with auto-migration when not writable.

## 从源码构建 / Build from Source

前置要求：.NET SDK 10（本机验证 10.0.401）。Prerequisite: .NET SDK 10 (verified with 10.0.401).

```bash
# 构建（Debug） / Build (Debug)
dotnet build PromptGenerator.csproj

# 运行测试 / Run tests
dotnet test tests/PromptGenerator.Tests/PromptGenerator.Tests.csproj

# 发布单文件 exe（Release） / Publish single-file EXE (Release)
dotnet publish PromptGenerator.csproj -c Release
```

发布参数已固化在 `PromptGenerator.csproj`（仅 Release 生效）：自包含、`win-x64`、单文件、压缩、native 库自解压。发布产物：`PromptGenerator.exe`（约 75 MB）+ `README.txt`（随行使用说明）。发布后首次启动若被杀毒软件拦截，请在安全中心放行。
Publish settings are baked into `PromptGenerator.csproj` (Release only): self-contained, `win-x64`, single-file, compressed, native libs self-extract. Output: `PromptGenerator.exe` (~75 MB) + `README.txt` (user guide, shipped alongside). If the first run is blocked by antivirus, allow it in Security Center.

## 常见问题 / FAQ

| 问题 / Question | 回答 / Answer |
|---|---|
| 能开多个实例吗？/ Can I run multiple instances? | 不能也不需要：单实例设计，再次启动自动唤起已运行实例。No — single-instance by design; relaunching wakes the running instance. |
| 数据存在哪里？/ Where is data stored? | 默认 exe 旁 `data\`；不可写时回退 `%LOCALAPPDATA%\PromptGenerator\data`。Next to the EXE; falls back to `%LOCALAPPDATA%\PromptGenerator\data`. |
| 「生成并提交」没反应？/ Submit does nothing? | 确认已安装并登录 `claude` CLI，查看 `data\logs` 当日日志。Check `claude` CLI installation/login and today's log. |
| 关闭窗口后程序还在？/ The app still runs after closing? | 关闭只隐藏到托盘；托盘右键「退出」才结束进程。Closing hides to tray; "Exit" in the tray menu truly quits. |
| 粘贴内容被自动修改？/ Paste got modified? | 「粘贴自动清理」默认开启，可在设置中关闭。Paste cleanup is on by default and can be disabled in Settings. |
| 删除后还能找回吗？/ Can I restore deleted items? | 删除均有确认；确认后弹出「撤销」提示，3 秒内可一键恢复。Deletes require confirmation; an "Undo" toast allows restoring within 3 seconds. |

## 许可 / License

[MIT](LICENSE) © 2026 DKLoong
