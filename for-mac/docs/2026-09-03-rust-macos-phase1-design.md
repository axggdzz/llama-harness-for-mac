# Rust/macOS LlamaHarness 第一阶段设计

## 目标

在 `for-mac/` 内建立可测试的 Rust 核心，完成固定前端端口的 OpenAI 兼容网关、按需启动和停止 llama-server、readiness 检测、普通/流式请求转发，以及可控的 mock llama-server。`for-win/` 下的 Windows C# 基线不参与编译，也不修改。

## 范围与边界

- 第一阶段只覆盖网关主链路与进程生命周期，不实现 UI、SlotAffinity、KV、TokenGuard、续接和完整调度策略。
- 所有路径、配置和测试夹具位于 `for-mac/`；运行时默认目录使用 macOS Application Support，测试通过临时目录隔离。
- 网关只绑定 `127.0.0.1:8080`；后端使用独立可配置端口，避免客户端感知后端变化。
- llama-server 命令使用参数数组传递；Unix 进程组通过 `setpgid`/信号实现优雅停止，超时后升级为 `SIGKILL`。

## 模块设计

```text
src/
├── lib.rs                 # 公共模块导出与测试入口
├── config.rs              # macOS 默认路径和最小配置模型
├── process.rs             # 参数数组启动、readiness、进程组停止
├── gateway.rs             # Axum 路由、启动合并、反向代理、SSE 透传
├── lifecycle.rs           # Standby/Waking/Warming/Running/Sleeping 状态模型
└── main.rs                # CLI 入口，组装运行时
src/bin/mock-llama-server.rs # 测试用 HTTP mock 后端
tests/
└── phase1_e2e.rs          # 黑盒端到端测试
```

核心接口保持简单且可替换：

- `BackendProcess::start(config) -> Result<BackendHandle>`：启动参数数组，等待子进程可观测退出。
- `BackendHandle::wait_ready() -> Result<()>`：轮询后端 `/health`，要求 HTTP 200 和 JSON `{"status":"ok"}`。
- `BackendHandle::stop() -> Result<()>`：向进程组发送 SIGTERM，超时后 SIGKILL，并等待句柄回收。
- `Gateway::serve(listener, backend_factory)`：固定监听器上的请求服务；首个推理请求通过共享 future 合并并发唤醒。
- `LifecyclePhase`：公开 `Standby/Waking/Warming/Running/Sleeping`，用于后续 UI 和调度事件。

## 请求行为

1. `GET /__status__` 返回当前阶段和后端端口，不触发唤醒。
2. `GET /health` 在后端未就绪时返回 503；就绪后返回网关自身状态。
3. `/v1/*` 推理请求在 `Standby` 下触发一次启动/就绪流程；并发请求等待同一个启动 future。
4. 启动成功后请求转发到后端同路径、方法、请求头和 body；响应状态、Content-Type 和 body 原样转发。
5. 流式响应使用字节流转发，不重分割 SSE；后端的 `data: ...\n\n` 边界和 `[DONE]` 保持不变。
6. 网关关闭时先停止接受新请求，再停止后端并等待进程组退出。

## 错误处理

- 后端启动失败、readiness 超时或异常退出，网关阶段回到 `Standby`，推理请求得到结构化 502/503。
- 反向代理连接失败只返回可诊断错误，不留下后台任务；后续请求仍可再次触发启动。
- 任何清理路径都必须执行 stop/join，端到端测试通过 PID 检查确认无残留 mock 进程。

## 第一阶段测试

- 单元测试：状态迁移、后端参数数组不拆分空格、readiness 成功/超时、SSE 字节边界。
- 黑盒端到端：启动网关，发送普通和流式 OpenAI 请求，验证固定端口、后端自动启动、并发唤醒合并、关闭后的进程组清理。
- mock 后端支持环境变量控制延迟、SSE 内容和退出行为，避免依赖真实模型或 Metal。

## 后续扩展约束

后续阶段在不改变第一阶段公开行为的前提下接入 SlotAffinity、KV、TokenGuard、SSE 续接、崩溃恢复、日志/统计和 Tauri UI。UI 只消费版本化状态事件，不直接依赖进程或网关内部实现。
