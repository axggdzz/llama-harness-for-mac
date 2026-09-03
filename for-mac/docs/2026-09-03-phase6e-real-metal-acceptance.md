# 第六阶段 E：真实 Metal/GGUF 验收记录

## 环境

- 机器：Apple Silicon，`arm64`
- 后端：Homebrew `llama.cpp 0.3.0`
- 模型：`ggml-org/tiny-llamas` 的 `stories15M-q4_0.gguf`，约 18 MB
- llama-server 参数包含 `--metrics` 和 `--slot-save-path`

## 已通过

- llama-server 成功加载 GGUF，并监听 `127.0.0.1:18090`。
- 直接后端：`/health`、`/props`、`/slots`、`/metrics` 正常。
- Rust 网关能力探测返回 `props=true`、`slots=true`、`metrics=true`、`tokenize=false`。
- 通过 Rust 网关完成真实 JSON chat completion。
- 通过 Rust 网关完成真实 SSE；`finish_reason=length` 自动续接并发送 `[DONE]`。
- 真实 slot KV save/restore/erase 成功；`n_saved=0,n_written>0` 被正确记录为有效快照。
- 网关停止后，8080 和 18090 均不再监听，未发现残留后端。

## 已知限制 / 待补

- 该 llama.cpp 版本没有 `/v1/tokenize`，因此 TokenGuard 在真实后端走安全降级；TokenGuard
  的真实 tokenize 路径仍需支持该接口的 llama.cpp 版本验证。
- 本次使用的 tiny 模型训练上下文为 128，适合 smoke，不代表生产模型质量或性能。
- 闲置休眠验证需要启动网关时设置较短的 `idle_timeout_ms` 和 `sleep_observe_ms`；当前
  CLI 尚未提供这两个环境变量覆盖，因此已保留 mock 生命周期测试，真实休眠/唤醒列为下一项。
- DMG、签名、公证和安装升级仍需发布环境验收。
