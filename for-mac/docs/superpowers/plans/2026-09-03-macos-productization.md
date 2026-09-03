# macOS Productization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task with verification checkpoints.

**Goal:** 将已完成的 Rust 核心能力接入可用的 Tauri macOS 应用，并补齐配置、控制、统计、KV、SSE 和发布验证链路。

**Architecture:** Rust gateway 继续作为唯一状态和进程管理所有者；Tauri 壳层通过本地 HTTP 控制端点调用 gateway，不在前端重复实现调度逻辑。UI 静态资源由构建脚本复制到隔离的 `ui/dist`，真实模型和 Metal 只在 macOS 验收阶段启用。

**Tech Stack:** Rust 2021, Tokio, Axum, reqwest, serde, Tauri 2, vanilla JavaScript, macOS Application Support, Unix process groups.

**Spec:** `for-mac/docs/spec.md` and GitHub Issue #1.

## Global Constraints

- 所有 Rust/macOS 代码、测试、文档和构建配置只放在 `for-mac/`。
- 根目录 `LlamaHarness/` Windows C# 工程保持不变。
- 进程启动使用参数数组；路径使用 macOS Application Support；不假设 `.exe`、CUDA 或 `nvidia-smi`。
- 每个任务先写行为测试，再实现最小改动；每个任务结束运行 `cargo fmt --check`, `cargo check`, `cargo test`（前端任务另运行 Node/Tauri 构建）。

### Task 1: Gateway control and config persistence (completed)

**Files:** `for-mac/src/config.rs`, `for-mac/src/gateway.rs`, `for-mac/src/main.rs`, `for-mac/tests/`

- [x] 为 `GET /__config__`, `PUT /__config__`, `POST /__control/wake`, `POST /__control/stop` 写集成测试。
- [x] 为 Application Support 配置文件实现原子加载/保存和字段校验。
- [x] 将控制端点绑定到已有 `Gateway::ensure_backend` / `stop_now`，不复制生命周期逻辑。
- [x] 将主入口改为加载配置并保持环境变量覆盖；增加 Unix 单实例锁测试和实现。

### Task 2: UI control and observability wiring (completed)

**Files:** `for-mac/ui/index.html`, `for-mac/ui/app.js`, `for-mac/ui/scripts/`, `for-mac/src/gateway.rs`

- [ ] 先写浏览器脚本可执行的 DOM/API 行为测试夹具。
- [x] 接通唤醒、停止、配置保存、日志读取、`/metrics` 原始响应和 KV 操作。
- [ ] 将错误、不可用能力和请求进行中的状态显示在现有七页签布局中。
- [ ] 保持默认暗色主题、中文字段和现有信息密度。

### Task 3: KV and statistics integration (completed)

**Files:** `for-mac/src/gateway.rs`, `for-mac/src/kv_cache.rs`, `for-mac/src/observability.rs`, `for-mac/tests/`

- [x] 写测试验证 SlotAffinity 分配后 KV restore/save/erase 的外部行为。
- [x] 在 gateway 中创建并持有 `KvCacheManager`，提供快照索引和操作端点。
- [x] 从 JSON usage 和 restore 结果填充 token、速度、命中率统计。
- [x] 通过日志端点提供轮转日志的安全尾部读取，限制单次读取大小。

### Task 4: Incremental SSE continuation (completed)

**Files:** `for-mac/src/gateway.rs`, `for-mac/src/continuation.rs`, `for-mac/tests/`

- [ ] 写测试验证事件边界、注释 keep-alive、`[DONE]` 和 length 续接顺序。
- [x] 将当前整段缓存改为异步流式转发；正常事件即时下发，只有检测到 length 才启动下一轮。
- [ ] 保留超时、工具调用、不完整 JSON 和客户端断开时的安全降级行为。

### Task 5: Backend capability probing and runtime hardening

**Files:** `for-mac/src/process.rs`, `for-mac/src/config.rs`, `for-mac/tests/`

- [x] 写 mock backend 能力探测测试。
- [x] 启动前按真实 `llama-server --help` 能力过滤不支持的参数；探测失败时安全回退并保留原始参数。
- [x] 将能力过滤警告接入主日志。
- [x] 补充 llama.cpp 0.3.0 真实 Metal 版本兼容性矩阵。
- [x] 补齐子进程提前退出、stderr 尾部日志、OOM 熔断和重启后的状态清理。

Tauri 壳层已直接托管 gateway，并在应用退出时触发 gateway shutdown；DMG、真实 Metal 能力探测和参数降级仍属于后续真机/发布验收。

### Task 6: Metal and release validation

**Files:** `for-mac/docs/`, `for-mac/README.md`, CI/release metadata under `for-mac/`

- [ ] 在 Apple Silicon 上用真实 Metal llama-server 和 GGUF 完成 smoke、SSE、KV、休眠/唤醒验证（smoke、SSE、KV 已通过；真实闲置休眠待补）。
- [x] 提供可重复的 `scripts/verify.sh`，验证 Rust、UI 语法和 Tauri `.app` 构建。
- [x] 在本机确认 Homebrew `llama-server` 为 Darwin arm64/AppleClang 构建，并验证缺失模型时提前退出。
- [x] 通过 Rust 网关验证真实后端提前退出时返回结构化 502，且退出后无测试后端残留。
- [ ] 验证 `.app`、DMG、签名、公证和安装升级；记录无法在受限环境执行的项目。
- [ ] 更新 README 阶段状态和最终验收清单，确认 Windows 路径无改动。
