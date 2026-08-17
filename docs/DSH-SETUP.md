# DSH (DeepSeek Harness) 接入 MCP for Unity 指南

> 面向本仓库协作者：如何让 **DSH（DeepSeek Harness）** 作为 MCP 客户端，接入本仓库的 `mcp-for-unity` 服务，从而控制 Unity 2020.3 编辑器。
> 最后更新：2026-08-17

## 一、链路拓扑（大白话）

```
DSH (DeepSeek Harness)
   │  dsh-mcp-client 插件（streamable-http 传输）
   ▼
mcp-for-unity Server（Python FastMCP，监听 127.0.0.1:8080）
   │  WebSocket/HTTP 桥（编辑器侧插件注册会话）
   ▼
Unity 2020.3.24f1 编辑器（MCPForUnity 包，工程 Unity2020Compat 等）
```

- **DSH 侧**：`@deepseek-ai/dsh-mcp-client` 插件（一个 MCP 服务器配一个实例），模型看到工具名形如 `mcp__unity__<工具名>`（如 `mcp__unity__manage_scene`、`mcp__unity__execute_code`）。
- **Server 侧**：本仓库 `Server/`（Python FastMCP），HTTP 模式监听 8080。
- **Unity 侧**：工程通过 `Packages/manifest.json` 引用 `com.coplaydev.unity-mcp`（`file:` 本地路径），编辑器里 MCP 窗口点 Connect 后桥注册会话。

## 二、前置条件

1. **Unity 2020.3.24f1** 已安装（`C:\SoftWare\Unity\2020.3.24f1` 或你的路径）。
2. **uv** 已安装（Windows：`C:\Users\<你>\.local\bin\uv.exe`；WSL 调 `uv.exe`）。
3. **mcp-for-unity Server 已在跑**（HTTP 模式，监听 8080）：
   ```bash
   # 在仓库 Server 目录下
   uv run mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools
   # 或后台方式（Windows Git Bash）
   cd Server && nohup uv run mcp-for-unity --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools > /tmp/mcp-for-unity-http.log 2>&1 &
   ```
4. **Unity 编辑器开着目标工程**（如 `TestProjects/Unity2020Compat`），并且 **MCP 窗口已点 Connect**（`Window > MCP for Unity > Connect`，Transport 选 `HTTP Local`，URL 填 `http://127.0.0.1:8080`）。
   - 验证：`curl http://127.0.0.1:8080/api/instances` 应返回非空实例列表。

## 三、DSH 配置（cordis.patch.yml）

DSH 不读 `.claude/mcp.json`，也不读 `~/.pi/agent/mcp.json`。它走自己的 **profile patch 层**：`~/.dsh/profiles/<profile>/cordis.patch.yml`（例如 `~/.dsh/profiles/web/cordis.patch.yml`）。

在文件末尾追加：

```yaml
# Unity MCP (mcp-for-unity) — DSH 接入
# serverName=unity -> 模型看到 mcp__unity__<工具名>
- insert:
    - id: mcp-unity
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: unity
        transport: streamable-http
        url: http://127.0.0.1:8080/mcp
        toolCallTimeoutMs: 120000
```

> **关键点**：
> - 用 `- insert:` 包裹（新增条目），**不要**裸写 `- id:`（那是覆盖已有行用）。
> - transport 用 **`streamable-http`**（不是 `stdio`），与 Unity 编辑器 MCP 窗口的 `HTTP Local` 模式对齐。若编辑器是 stdio 模式则反配 stdio + `command/args`。
> - `toolCallTimeoutMs` 建议调大（默认 60s 对编译/执行类工具偏短）。

### 验证配置合入

```bash
dsh --profile web --dump-config | findstr mcp-unity
```

应能看到 `id: mcp-unity` 及其完整 config 块。

### 生效方式

- 新增条目：**必须重启 DSH 进程**（HMR 只对"编辑已有行"生效，新增 insert 不热更新）。
- 编辑已有条目：HMR 自动断开重连。

## 四、使用验证

重启 DSH 后，新会话里模型应能看到 `mcp__unity__*` 工具。快速冒烟：

| 工具 | 预期 |
|---|---|
| `mcp__unity__manage_scene` action=get_active | 返回当前场景 rootCount |
| `mcp__unity__read_console` | 返回 Unity 控制台日志 |
| `mcp__unity__manage_camera` screenshot capture_source=game_view_pixels | 截到 Game 视图实际像素 |
| `mcp__unity__unity_reflect` get_type | 反射查 Unity API |
| `mcp__unity__execute_code` | 动态编译执行 C#（需人工批准，见下） |

## 五、execute_code 的人工确认机制（P1-4 权限分级）

`execute_code` 属于 `ConfirmationLevel.Always`——**执行任意代码必须用户在 Unity 编辑器里点"允许"**：

1. 模型调用 `execute_code` → Unity 弹出 `MCP Command Approval` 确认窗。
2. 用户在 60 秒内点 **允许 (Allow)**，代码才会执行；不点则自动拒绝。
3. 弹窗倒计时归零后**仍挂着是"残留尸体"**，会串行挡住新请求——先点拒绝/× 关掉，再触发新的。

> 这是安全设计（防止 AI 未经确认执行任意代码），不可关闭。每次 `execute_code` 都需要人工批准一次。

## 六、已知问题与修复记录

### 6.1 资源注册空引用（已修复，commit `d2137d93`）

- **现象**：`execute_code` 等工具失败，Unity 控制台刷 `Unknown or unsupported command type: get_editor_state`，且伴随一串 `Failed to register resource <X>: Object reference not set to an instance of an object`。
- **根因**：`MCPForUnity/Editor/Tools/CommandRegistry.cs` 的 `RegisterCommandType` 中，resource 分支未给 `toolAttr` 赋值（保持 null），但构造 `HandlerInfo` 时直接访问 `toolAttr.ConfirmationLevel` / `toolAttr.ConfirmationAction`，抛空引用，导致所有 `[McpForUnityResource]` 类（get_editor_state / get_project_info / get_selection 等）注册失败。
- **修复**：改用空值安全访问 `toolAttr?.ConfirmationLevel ?? ConfirmationLevel.None`、`toolAttr?.ConfirmationAction`（resource 默认无确认需求）。
- **验证**：Unity 2020.3.24f1 下 `execute_code` 动态编译执行 C# 成功，`get_editor_state` 正常响应。

### 6.2 多 Server 进程抢 8080 端口

- 现象：多个 `mcp-for-unity` HTTP 实例同时存在（`.venv` 直跑 + uv 包装层），端口竞争导致编辑器 "Connection is no longer valid"。
- 处理：清理多余进程，只保留一个健康实例（`/health` 返回 200 的那个）。

### 6.3 编辑器桥未注册会话

- 现象：Server 正常但 `/api/instances` 返回 `[]`。
- 处理：在 Unity MCP 窗口点一次 **Connect**，桥才向 Server 注册会话。

## 七、协作约定（衔接 DEVELOPMENT.md）

- 所有开发在 `feat/wait-for-idle` 分支；改动经 `git pull --rebase` 后推送。
- 新增 MCP 工具必须声明 `annotations=ToolAnnotations(...)` 并登记到 `Server/tests/test_tool_annotations.py`。
- 新增 C# 文件必须手写 `.meta`。
- 本文件涉及客户端接入类配置，与 `docs/UNITY_2020_3_COMPAT.md`（Unity 端兼容）、`docs/DEVELOPMENT.md`（协作总纲）互补。
