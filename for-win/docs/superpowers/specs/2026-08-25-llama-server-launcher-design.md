# llama.cpp 启动器（LlamaHarness）设计规格

日期：2026-08-25
状态：已评审通过（含用户三项优化）

## 1. 背景与目标

为 Windows 提供一个图形化启动器，一键启动 llama.cpp 的 `llama-server`（OpenAI 兼容本地 LLM API 服务），并实时显示运行日志。

**成功标准：**
- 打开程序 → 选模型 → 点"启动"→ llama-server 在后台静默运行，日志实时可见
- 参数开箱即用黄金底参，无需手填
- 配置持久化，下次自动加载

## 2. 范围（YAGNI）

**范围内：**
- GUI 启动 / 停止 llama-server
- 实时日志显示（stdout + stderr）
- 常用参数管理 + config.json 持久化
- llama-server.exe 定位（手动指定 + 自动搜索）

**范围外（不做）：**
- 自动下载 llama.cpp / 预编译包
- 多实例 / 多模型并发管理
- 交互式聊天客户端（llama-cli）
- Web 前端页面
- Linux / macOS 支持

## 3. 技术选型

- **.NET 8 + WinForms**（目标框架 `net8.0-windows`，Windows 桌面应用）
- 单项目、无第三方依赖、无 NuGet 包
- 弃选 WPF / AvalonUI：功能边界小，避免过度设计

## 4. 项目结构

单一项目 `LlamaHarness`，5 个核心文件：

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口，`[STAThread]` 启动 MainForm |
| `MainForm.cs` | UI 布局与交互（参数区、按钮、日志区、状态标签） |
| `LlamaServerProcess.cs` | 进程封装：Start/Stop/Kill 进程树、输出事件、退出码 |
| `AppConfig.cs` | 配置模型 + config.json 读写（JSON） |
| `LlamaFinder.cs` | 定位 llama-server.exe（纯逻辑，无 UI 依赖） |

## 5. 黄金默认参数（固化，不可被空值覆盖）

用户实测的稳态基础参数，作为 UI 默认值并持久化：

| 参数 | CLI 形式 | 默认值 |
|---|---|---|
| 上下文长度 | `-c` / `--context-length` | **262144**（256K） |
| GPU 层数 | `-ngl` | **999**（全部层上 GPU） |
| 并发序列 | `--parallel` | **1** |
| KV 统一 | `--no-kv-unified` | **默认开启** |
| 端口 | `--port` | 8080 |
| 线程数 | `-t` | CPU 核心数（`Environment.ProcessorCount`） |

启动命令行模板：

```
llama-server.exe -m <model.gguf> --port <port> -c 262144 -ngl 999 --parallel 1 --no-kv-unified -t <threads> [附加参数]
```

"附加参数"为可选自由文本，追加到末尾。

## 6. UI 设计

单窗口，三段布局：

**上方 — 参数区：**
- llama-server.exe 路径（文本框 + "浏览…"按钮）
- 模型文件（文本框 + "浏览…"按钮，过滤器 `*.gguf`）
- 端口、上下文长度、GPU 层数、并发数、线程数（NumericUpDown / TextBox）
- no-kv-unified 复选框（默认勾选）
- 附加参数（单行文本，可选）

**中间 — 操作区：**
- [启动] [停止] [清空日志] 按钮 + 状态标签（空闲 / 运行中 / 已退出(码X)）

**下方 — 日志区：**
- 只读多行 TextBox，逐行追加、带时间戳前缀 `[HH:mm:ss]`
- **自动滚动**：每次追加后执行 `ScrollToEnd()`，始终显示最新一行（不做"用户手动回滚时暂停跟随"的复杂处理，YAGNI）

**UI 状态机（防重复启动 / 多进程冲突）：**
- `空闲`：启动按钮可用，参数控件全部可编辑，停止按钮禁用
- `运行中`：启动按钮**禁用**，所有参数控件**禁用**（防止运行中改参），停止按钮可用
- 窗口关闭时若在运行：弹框确认 → 确认后终止进程树再退出

## 7. 进程管理（LlamaServerProcess）

```csharp
var psi = new ProcessStartInfo(exePath)
{
    UseShellExecute = false,   // 必须：允许重定向输出
    CreateNoWindow = true,     // 后台静默，无黑框弹窗
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    WorkingDirectory = <exe 所在目录>,
};
```

- stdout / stderr 各起一个 `SynchronizationContext.Post` 回调逐行推送到日志区（UTF-8 编码）
- **停止**：优先 `Process.Kill(entireProcessTree: true)`；进程已退出则忽略
- **退出检测**：`Exited` 事件 → 状态标签更新为"已退出(码X)"，恢复空闲态（启用启动按钮与参数控件）
- 重复启动防护由 UI 状态机 + 进程句柄非空双重保证

## 8. 配置持久化（AppConfig）

- 文件：exe 同目录 `config.json`
- 字段：`exePath`、`modelPath`、`port`、`ctxSize`、`ngl`、`parallel`、`noKvUnified`、`threads`、`extraArgs`
- **加载**：启动时读取；文件不存在 → 用第 5 节黄金默认值；JSON 损坏 → 回退默认值并在日志区提示
- **保存时机**：每次成功启动 llama-server 后自动保存当前参数

## 9. llama-server.exe 定位（LlamaFinder）

优先级从高到低：
1. config.json 中手动指定的路径（若文件存在则直接用）
2. PATH 环境变量搜索 `llama-server.exe` / `llama-server`
3. 常见安装位置：启动器 exe 同目录、`C:\llama.cpp\build\bin\Release\`、用户主目录

全部落空 → 状态栏/日志提示"未找到 llama-server.exe，请手动指定"，启动按钮保持禁用直至用户浏览选择。

## 10. 错误处理

| 场景 | 行为 |
|---|---|
| exe 不存在 | 阻止启动，MessageBox 提示，不改变运行态 |
| 模型文件不存在 | 阻止启动，MessageBox 提示 |
| 端口占用 / 运行时错误 | llama-server 自身 stderr 报错显示在日志区；进程退出后状态标签变红显示退出码 |
| 进程意外退出 | `Exited` 事件更新状态 + 日志显示退出码 |
| config.json 损坏 | 回退黄金默认值，日志区提示 |

## 11. 测试策略

GUI + 外部进程依赖，以手动验证为主（YAGNI，不引入单元测试框架）：

**手动验证清单：**
1. 选一个小模型 gguf → 启动 → 看到 llama-server 正常输出（如 "server is listening"）→ 停止 → 进程树无残留（任务管理器确认）
2. 修改参数 → 重启 → 确认新参数生效于命令行
3. 关闭程序重开 → 参数已持久化
4. 删掉 config.json / 写入非法 JSON → 程序用默认值启动且不崩溃
5. 占住 8080 端口后启动 → 日志显示报错且状态正确
6. 运行中双击"启动"被禁用（无法触发第二次进程）

**可直验的纯逻辑：** `LlamaFinder` 搜索顺序、命令行拼接（给定配置 → 期望字符串），在实现时以控制台方式快速自验。

## 12. 风险与假设

- **假设**：用户已自行安装 llama.cpp（含 llama-server.exe）并持有 gguf 模型文件；本工具不负责下载
- **VRAM 风险**：262144 上下文 + ngl=999 对显存要求较高，显存不足时 llama-server 会在日志中报错退出——这是预期行为而非 bug
- **进程树**：llama-server 为单进程，Kill 进程树主要防止其派生子进程残留
