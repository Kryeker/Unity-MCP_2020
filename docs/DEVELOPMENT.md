# 开发进度与协作指南（Development Guide）

> 面向本仓库协作者的背景、进度、约定文档。主分支约定见下文"分支与协作"。
> 最后更新：2026-08-13

## 一、项目背景与定位

- **底座**：fork `CoplayDev/unity-mcp`（v10.1.2，MIT）——通过 MCP 协议让 AI 助手（Claude/Cursor/pi 等）控制 Unity 编辑器。
- **目标**：在 v10.1.2 基线上自主增强，对标 Tuanjie（团结引擎）/ Codely 的行为标准；**所有 Unity 项目目标版本为 2020.3 LTS**（C#8、Unity 2020.3.24f1）。
- **明确不做**：Tuanjie 深入（闭源+付费）、升级 Unity 版本、反编译 Tuanjie、深度依赖 IvanMurzak 云服务、fork Unity-Skills。
- **架构**：Python Server（FastMCP）↔ WebSocket/stdio 桥 ↔ Unity C# 编辑器插件（MCPForUnity）。Python 工具层 / C# 工具层 / CLI 三层并存，领域对称。
- **上游跟踪**：`upstream/beta` 跟踪 CoplayDev/unity-mcp 上游 beta；上游 PR #1323（2020.3 兼容）已 open，等 maintainer。

## 二、分支与协作约定

| 分支 | 用途 | 说明 |
|---|---|---|
| `feat/wait-for-idle` | **主开发分支（默认分支）** | 所有增强功能都在这；clone 后默认 checkout 此分支 |
| `beta` / `main` | 上游基线镜像 | **不要**往上推工作代码 |
| `baseline-v10.1.2` | 本地基线标记 | v10.1.2 基线 |
| `pr/compat-2020` | 上游 PR #1323 跟踪 | 等上游 maintainer 合并 |

**协作规则**：
- 都在 `feat/wait-for-idle` 上开发（或建 `feat/<xxx>` 子分支，验证后合入）
- push 前先 `git pull --rebase`；冲突按 rebase 处理
- 提交信息用中文/英文均可，但需说明改动动机（参考历史提交风格）
- 每新增一个 MCP 工具必须：显式声明 `annotations=ToolAnnotations(title=..., destructiveHint=...)`，并按只读/非破坏登记到 `Server/tests/test_tool_annotations.py`
- 新增 C# 文件必须手写 .meta（`fileFormatVersion: 2\nguid: <32hex>`，guid 用 `cat /proc/sys/kernel/random/uuid | tr -d '-'`）

## 三、功能清单与状态

### P0 系列（全部完成 ✅）
| 项 | 功能 | 说明 |
|---|---|---|
| P0 基础 | Unity 2020.3 LTS 兼容 | 基线兼容 + packages-lock 解析修复 |
| P0-3 | `wait_for_idle` / `wait_for_compile` | 跨域重载会话（SessionState+token 幂等+watchdog）、JobRegistry |
| P0-1 | `execute_code` | 协程模式 + 日志捕获 + CodeDom nullable bug 修复 |
| P0-4 | Unity Insight 适配层 | 封装已安装 unity-insight-cli（unity_refs/search/ls/read/index） |
| P0-2 | `manage_input` | PlayMode 鼠标/键盘注入（EventSystem + SendInput + type_text） |

### P1 系列（全部完成 ✅）
| 项 | 功能 | 说明 |
|---|---|---|
| P1-4 | 权限分级 | ConfirmationLevel + 编辑器内用户确认窗 + manage_permissions |
| P1-5 | 快照回滚 | manage_asset delete/move 快照 + dry_run + manage_snapshots |
| P1-6 | 结构化审计 | query_audit_log（JSONL 查询）+ manage_audit（开关/状态） |

### P2（核心完成 ✅）
| 项 | 功能 | 说明 |
|---|---|---|
| P2 | `game_view_pixels` 截图 | 捕获 GameView 实际像素（含 UGUI overlay），Edit/Play 双模式；修复脚本建 Canvas 默认 WorldSpace 的 bug |

### 演示
- `TestProjects/Unity2020Compat/Assets/Scenes/UiMenuFinal.unity`：现代审美 UGUI 主菜单示例（星云背景 + 毛玻璃卡片 + 胶囊按钮 + 中文字体），素材在 `Assets/UI/`

## 四、待办（按优先级）

| 项 | 说明 | 状态 |
|---|---|---|
| 离屏 UI 截图技术验证 | 让截图不依赖窗口可见（读 RT/临时渲染），对标 Tuanjie 能力 | 待验证 |
| P0-2 遗留 | manage_input 的 sequence 多帧序列动作（后置）；SendInput 需真实焦点窗口验证 | 待做 |
| ScriptFix | 8 个 FixProvider 修 CS1002/1026 等编译错误 | 待做 |
| ensure_* 幂等 | 待做 |
| Insight 方案 B | 自包含 tree-sitter 实现 | 按需 |
| PR #1323 | 等上游 maintainer；upstream beta 前进时 rebase | 等待 |

## 五、开发环境与验证

- **Unity 2020.3.24f1**：`C:\SoftWare\Unity\2020.3.24f1`（Windows）
- **Python 测试**：`cd Server && uv run --extra dev pytest tests/ -q`（uv 用 Windows 版，WSL 直接调 `uv.exe`）
- **编译验证**：用户实例占用测试工程时，用隔离工程（拷贝 ProjectSettings + manifest 改 `file:` 绝对路径到 MCPForUnity + `verify_compile.cmd` batchmode，`UNITY_EXIT=0` 即通过；用完删除）
- **运行时验证**：窗口模式 + `-executeMethod MCPForUnity.Editor.McpCiBoot.StartStdioForCi`（桥监听 6400）+ Python 脚本直连 `async_send_command_with_retry`
- **pi 接入**：`~/.pi/agent/mcp.json` 配置 unity-mcp server（stdio 经 cmd.exe 调 Windows uv，`UNITY_MCP_DEFAULT_INSTANCE` 指定实例）；新会话生效
- **已知环境坑**：
  - 编辑器窗口后台/被遮挡时渲染与截图不可靠（EditMode UI 不合成；PlayMode 需 `runInBackground=1`）
  - Windows Python stdout 默认 GBK（中文乱码是假象）
  - 脚本/工具创建对象时先观察场景状态，避免重复创建（`manage_gameobject create` 不唯一化是上游语义）

## 六、MCP 配置（协作者本机）

如需用 pi/Claude 等客户端控制 Unity，参照 `.pi/settings.json`（skills 挂载）与 `~/.pi/agent/mcp.json`（unity-mcp server）。Server 入口命令：`mcp-for-unity`（stdio 默认）。Unity 端：`TestProjects/Unity2020Compat/Packages/manifest.json` 的 `com.coplaydev.unity-mcp: file:../../../MCPForUnity` 需在各自仓库位置可解析。
