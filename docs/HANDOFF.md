# 交接文档（2026-08-12 更新）

> 给新对话的完整背景。工作区 `/mnt/f/AIProjects/Unity-shikiMCP`，分支 `feat/wait-for-idle`。
> 前一份交接：交接信息（fork 底座、P0 清单、Tuanjie 调研结论）见下方"项目决策"——均已落实或推进中。

## 一、项目定位与决策（已定，不再讨论）

- **目标**：fork `CoplayDev/unity-mcp`（v10.1.2，MIT）为底座，自主增强，对标 Tuanjie/Codely 行为标准；所有 Unity 项目为 **2020.3 LTS**
- **明确不做**：Tuanjie 深入（闭源+付费）、升级 Unity、反编译 Tuanjie、深度依赖 IvanMurzak 云服务、fork Unity-Skills
- **仓库**：`RoyougiShiki/Unity-shikiMCP`（GitHub fork），`upstream/beta` 跟踪上游
- **封装边界结论**（本次调研确定）：只有"独立 CLI 形态"的 Tuanjie 组件可 spawn 封装（Unity Insight ✓）；编辑器内桥功能（wait/job/ScriptFix）无法外部调用 → 自研或后置

## 二、当前分支与提交

分支 `feat/wait-for-idle`（所有增强都在这），`baseline-v10.1.2` 为基线，`pr/compat-2020` 是 PR #1323 分支（等上游 maintainer）。

| 提交 | 内容 |
|---|---|
| `da439c7d` | **P0-3** wait_for_idle / wait_for_compile + 跨域重载会话基础设施 |
| `513acacd` | **P0-1** execute_code 协程模式 + 日志捕获 |
| `9d3ec8d9` | **P0-4** Unity Insight 适配层（封装已安装 unity-insight-cli） |
| `75b0fad3` | **P0-2** manage_input PlayMode 输入注入 |
| `7ac4d6dc` | **P1-4** 权限分级 + 编辑器内用户确认 |
| `917a4f07` | **P1-5** 快照回滚 + dryRun（manage_asset） |

PR #1323（https://github.com/CoplayDev/unity-mcp/pull/1323）：OPEN/MERGEABLE，最新 commit `639279b4`，等 maintainer；`upstream/beta` 已完全包含在 pr/compat-2020 内，暂无需 rebase。

## 三、已交付功能详述

### 1. P0-3 wait_for_idle / wait_for_compile（对标 Tuanjie StepJobRunner）
- **WaitSessionRunner**（`MCPForUnity/Editor/Helpers/WaitSessionRunner.cs`）：跨域重载等待会话——SessionState 持久化（kind/字段/deadline），域重载后 InitializeOnLoad 恢复 update tick 续跑；**token 幂等**（Python 断连重连后带同 token 重调 → 返回持久化结果或 attach 新监听）；wall-clock deadline + **线程池 watchdog**（后台窗口 pump 停时仍超时）
- **JobRegistry**（`MCPForUnity/Editor/Services/JobRegistry.cs`）：线程安全注册表；TestJobManager/PackageJobManager/ManagePackages/execute_code 协程已接入 → wait_for_idle 等真实工作
- **wait_for_idle**（默认 600s）：空闲 = 无 job + 不编译 + 不导入 + 不进入 PlayMode；超时诊断列出在途 job
- **wait_for_compile**（默认 300s）：三步状态机（detect settle 窗口仅空闲时倒计时 → await → report），`compile_observed` 防误报、`hasErrors` + LogEntries.GetCountsByType（跨域重载有效）、trigger 只触发一次（CompileTriggered 持久化）
- Python：`wait_for_idle.py` / `wait_for_compile.py`（内部 uuid token）；transport 加了 `total_timeout` 参数（默认 90s 会掐断长等待）

### 2. P0-1 execute_code 增强
- **协程模式**（`execution_mode="coroutine"`）：包装为 IEnumerator，异常安全转发泵（yield 指令原生转发、用户异常结构化、**Dispose 保证 finally 执行**——C# iterator 的 finally 只在 Dispose 时跑）；`ExecuteCode.SetCoroutineResult(obj)` 返回值；runInBackground 引用计数；双超时（Update + wall-clock watchdog）
- **日志捕获**：全局 logMessageReceived 环形缓冲（500 条），每次执行附新增日志（capture_logs/log_limit）
- **修复上游真实 bug**：CodeDom 编译器（Mono csc）读不了带 C#8 nullable 元数据的 DLL（`com.unity.nuget.newtonsoft-json@3.x` 必然触发 CS0009）——按字节特征（NullableContextAttribute 字符串）过滤（`IsCodeDomReadable` + 缓存）
- 坑：`StartCoroutine` 曾漏调（协程永不完成）；HideAndDontSave 宿主在 EditMode 测试不被驱动 → 改 `HideInHierarchy | DontSave`

### 3. P0-4 Unity Insight 适配层（方案 A：封装已安装 CLI）
- **unity_insight.py**：spawn Tuanjie 随包 node.exe + bundle（源码闭源，**零 Tuanjie 代码入库**）；CLI 发现：`UNITY_INSIGHT_CLI`/`UNITY_INSIGHT_BUNDLE` 环境变量 > 安装目录扫描（含 WSL /mnt/c 路径）；项目路径从 PluginHub 注册表解析
- 工具：`unity_refs`（引用图 in/out + target_type）、`unity_search`（类型化 glob）、`unity_ls`、`unity_read`、`unity_index`（status/sync/build）
- 输出编码嗅探（UTF-8 + mojibake 检测回退 GBK——实为 Windows Python GBK stdout 显示假象，嗅探作防御保留）
- 参考情报（已调研）：索引 schema v6 17 表、YAML PPtr 解析逻辑、边类型（DEPENDS_ON/INSTANCE_OF/RENDERS_MESH/PREFAB_INSTANCE_GUID…）、tree-sitter 技术栈（自实现方案 B 时可复用 py-tree-sitter）；vfs_grep 依赖 FTS5（本机 node 环境缺失，未封装）
- 真实索引：`C:\Users\h\Documents\UnityProject\TCMAnatomyModule\Library\UnityInsight\index.db`（1.6GB，27841 文件，per-project）

### 4. P0-2 manage_input PlayMode 输入注入
- **ManageInput.cs**（UI 路径，对标 Tuanjie 全套）+ **InputSendInput.cs**（设备路径：user32 SendInput P/Invoke，驱动旧输入系统）
- 10 个动作：mouse_click/move/drag/scroll/down/up、key_press/down/up、type_text
- 双路径：EventSystem（UI 命中时直调处理器，可靠）+ SendInput（非 UI，需窗口焦点）；`target` 参数 auto/ui/world；多帧派发（press/release 隔帧、drag 插值）；串行化 + PlayMode 退出重置
- type_text/编辑键走 IMGUI 队列（InputField.ProcessEvent + TMP 反射）——设备注入到不了文本域
- 验证：PlayMode 测试 3/3（uGUI 点击 onClick、type_text 填充、Backspace 编辑）——**测试基建**见"验证方法"

### 5. P1-4 权限分级 + 用户确认
- `ConfirmationLevel`（None/Confirm/Always）工具属性 + `ConfirmationAction` 过滤（如 manage_asset 仅 delete 确认）
- **PermissionGate.cs**：非模态确认 EditorWindow（Allow/Reject + 倒计时）+ **wall-clock watchdog**（后台 pump 停仍超时拒绝）；EditorPrefs 配置（开关 + 超时 5-600s）；串行化；关窗=拒绝；Always 不可禁用
- **CommandRegistry 单点拦截**（ExecuteCommand）：确认后 sync/async 分发；拒绝响应必须 `{status:"success", result:<ErrorResponse>}` 包装（**Python 侧 `resp.get('result', {})` unwrap**——裸 JSON 会变 {}）
- 标注：execute_code(Always)、manage_asset delete(Confirm)、manage_packages remove(Confirm)
- `manage_permissions` 工具：get/set_enabled/set_timeout

### 6. P1-5 快照回滚 + dryRun
- **SnapshotService.cs**：`Library/McpSnapshots/<id>/`（跨重启、24h TTL、上限 20）；**.meta 与文件捆绑**（防 GUID 孤儿）；文件夹递归展开；范围=文件级（场景内交 Unity Undo）
- manage_asset delete/move：`dry_run` 预览（资产+.meta 清单，不执行）；真实执行自动快照返回 snapshot_id
- `manage_snapshots` 工具：list/rollback/delete
- 验证闭环：create → dry_run(2 files) → delete(snapshot) → rollback(2 files) → get_info 资产回归 ✓

## 四、验证方法与环境（重要，新对话必读）

### 环境
- Unity 2020.3.24f1：`C:\SoftWare\Unity\2020.3.24f1`；**用户实例 PID 24504 长期开着 TestProjects/Unity2020Compat——永远不要杀它**（taskkill 循环必须 `[ "$pid" != "24504" ]`）
- pytest：`cd Server && /mnt/c/Users/h/.local/bin/uv.exe run --extra dev pytest tests/ -q`（uv 是 Windows 程序，WSL 直接调）
- 用户图形化打开测试工程时命令行编译会崩（实例占用）→ **必须用隔离工程**：cp Assets/Packages/ProjectSettings 到 `/mnt/f/AIProjects/_pr_verify_wait` + manifest 改 `file:F:/AIProjects/Unity-shikiMCP/MCPForUnity` + 独立 cmd
- 编译验证：隔离工程 `verify_compile.cmd`（batchmode -quit）→ UNITY_EXIT=0
- 运行时验证：窗口模式 + `-executeMethod MCPForUnity.Editor.McpCiBoot.StartStdioForCi` → 桥监听 6400 → Python 脚本直连 `async_send_command_with_retry`；**用完杀实例 + 删锁文件 + 删 _pr_verify_wait**
- PlayMode 测试：隔离工程 `Assets/Tests/PlayMode/`（asmdef：TestAssemblies + overrideReferences Newtonsoft.Json.dll + UnityEngine.UI）；`-runTests -testPlatform PlayMode -testFilter Xxx`；batchmode -nographics 的 "No graphic device" 日志用 `LogAssert.ignoreFailingMessages = true` 忽略；**测试间对象不自动清理**（手动 Destroy + 位置错开）

### 已知环境坑
- 窗口后台（无焦点）：Edit mode 协程泵/update 停 → 所有等待必须有 wall-clock watchdog（已内建于 WaitSessionRunner/PermissionGate/ExecuteCode）
- Windows Python stdout 管道默认 GBK → WSL 终端显示中文乱码是假象（用 repr/encode 验证）
- heredoc 喂 Windows Python 时源码中文可能按 GBK 解析 → 验证脚本用 UTF-8 文件
- 桥 async 响应 = `{status, result}` 包装（Python unwrap result）；确认拒绝必须按此包装

## 五、待办（按优先级）

| 项 | 说明 |
|---|---|
| **P1-6 结构化审计 JSONL** | 记录所有工具调用/结果的结构化审计日志（McpLogRecord 已有雏形——TransportCommandDispatcher 里 `McpLogRecord.Log(...)`，看是否输出 JSONL 文件；未完成部分：文件输出/查询工具） |
| **P2 截图体系扩展** | 上游已有 EditorWindowScreenshotUtility；扩展方向未定 |
| **P2后议 ScriptFix** | 保留：8 个 FixProvider 修 CS1002/1026/1513/0246 等；Tuanjie 桥内功能无法封装，自研后置 |
| **P2后议 ensure_* 幂等** | 保留 |
| **P0-2 遗留** | manage_input 的 sequence 多帧序列动作（后置）；SendInput 运行时验证需真实焦点窗口 |
| **Insight 方案 B** | 自包含实现（tree-sitter + yaml，四表核心版）按需后置（适配层已交付） |
| **PR #1323** | 等上游 maintainer；upstream beta 前进时 rebase |

## 六、关键文件索引

**C#（MCPForUnity/Editor/）**：
- `Helpers/WaitSessionRunner.cs`（跨域重载会话）、`Helpers/PermissionGate.cs`（确认弹窗）、`Helpers/SnapshotService.cs`（快照）、`Helpers/CompilationHelper.cs`（编译状态/错误计数）
- `Services/JobRegistry.cs`（job 注册表）、`Services/WaitSessions.cs`（idle/compile 会话状态机）
- `Tools/WaitForIdle.cs`、`Tools/WaitForCompile.cs`、`Tools/ExecuteCode.cs`、`Tools/ManageInput.cs`、`Tools/InputSendInput.cs`、`Tools/ManagePermissions.cs`、`Tools/ManageSnapshots.cs`
- `Tools/CommandRegistry.cs`（确认拦截 + ConfirmationLevel）、`Tools/McpForUnityToolAttribute.cs`（属性扩展）

**Python（Server/src/services/tools/）**：`wait_for_idle.py`、`wait_for_compile.py`、`unity_insight.py`、`manage_input.py`、`manage_permissions.py`、`manage_snapshots.py`
**transport**：`unity_connection.py`（加了 total_timeout）
**测试**：`Server/tests/test_wait_for_idle.py`、`test_wait_for_compile.py`、`test_unity_insight.py`、`test_manage_input.py`、`test_manage_permissions.py`、`test_manage_snapshots.py`、`test_manage_asset_dryrun.py`、`test_tool_annotations.py`（READ_ONLY/NON_DESTRUCTIVE 集合——新工具必须登记）

## 七、工作流约定

- 每个新 MCP 工具必须：`annotations=ToolAnnotations(title=..., destructiveHint=...)` 显式声明 + 按只读/非破坏登记到 test_tool_annotations.py 集合（否则全量测试失败）
- 新增 C# 文件必须手写 .meta（`fileFormatVersion: 2\nguid: <32hex>\nMonoImporter...`，guid 用 `cat /proc/sys/kernel/random/uuid | tr -d '-'`）
- 2020.3 = C#8：不用 target-typed new、switch 表达式慎用；提交前 `verify_compile.cmd` + pytest 全量
- git 提交用 `git config user.name "RoyougiShiki"`（本仓库已配）
- `TestProjects/Unity2020Compat/Packages/manifest.json` 的改动（用户实例加的 MCP 包/toolchain）**不要提交**（用户自己的）
- 修改后运行相关测试；回复简洁；复杂方案先给选项再执行
