# macOS 移植规格

完整规格已发布在 [GitHub Issue #1](https://github.com/axggdzz/llama-harness-for-mac/issues/1)。

本目录是 Rust/macOS 版本的文档边界。实现、测试和平台决策都应放在 `for-mac/` 下；根目录的 C# WinForms 工程是 Windows 基线，不应与 Mac 代码混用。

核心目标：最大限度保留 Windows 版 LlamaHarness 的功能和 UI，包括 OpenAI 兼容网关、智能唤醒/休眠、slot 亲和、KV 快照、TokenGuard、思考模式、SSE 自动续接、崩溃恢复、日志、统计、系统资源页和七页签仪表盘。

macOS 适配使用 Rust、Tokio/Axum、Unix process group、Application Support 路径、Metal/统一内存指标；P 核亲和和 NVIDIA 显存监控改为能力提示或 macOS 等价指标。
