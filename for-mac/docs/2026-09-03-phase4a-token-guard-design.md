# 第四阶段 A：macOS TokenGuard 设计

## 目标

在网关转发前使用 llama-server 的真实 `/v1/tokenize` 结果执行上下文预算保护，避免渲染后的 prompt 触发 context overflow。该阶段只实现 TokenGuard 与网关前置裁剪；400 自愈重试、思考模式、SSE 续接和 OOM 恢复分别在后续阶段接入。

## 预算

启用条件为 `token_guard_enabled=true` 且 `context_size` 已配置。输入预算为：

`context_size / slot_count - reserved_output_tokens - reserved_prompt_overhead`

预算小于 1 时按 1 处理。默认关闭 TokenGuard，保持当前网关兼容性。

## 裁剪算法

1. 从 `messages` 构造 `role: content` 文本并调用 `/v1/tokenize`。
2. 预算内直接透传；tokenize 失败时记录降级结果并原样透传。
3. 超预算时保留首个 user 之前的 system/前缀消息和最后一轮 user 及其后续 assistant/tool 消息；可删除的旧轮次使用二分搜索确定最少删除数量。
4. 最小轮次集合仍超限时，选择最长字符串 content，最多 5 次按比例缩短，头尾各保留一半并追加 `[已截断 - Token Guard]`。
5. 裁剪后仍超限返回 HTTP 400，错误体含 `token_guard`、`budget` 和 `tokens` 字段。

## Rust 接口

`for-mac/src/token_guard.rs` 提供：

```rust
pub struct TokenGuardConfig { pub context_size: usize, pub slot_count: usize, pub reserved_output_tokens: usize, pub reserved_prompt_overhead: usize, pub enabled: bool }
pub struct GuardReport { pub modified: bool, pub skipped: bool, pub estimated_tokens: Option<usize>, pub final_tokens: Option<usize>, pub budget: usize, pub deleted_turns: usize }
pub struct TokenGuard;
impl TokenGuard {
    pub fn budget(config: &TokenGuardConfig) -> usize;
    pub async fn guard<F, Fut>(config: &TokenGuardConfig, body: &mut serde_json::Value, counter: F) -> anyhow::Result<GuardReport>
    where F: Fn(String) -> Fut, Fut: Future<Output = anyhow::Result<usize>>;
    pub async fn count_tokens(client: &reqwest::Client, backend_base_url: &str, text: &str) -> anyhow::Result<usize>;
}
```

## 网关接入

`AppConfig` 增加 `token_guard_enabled`、`context_size`、`reserved_output_tokens`、`reserved_prompt_overhead`。网关在后端就绪、slot 注入之后执行 TokenGuard；裁剪后的 JSON 才发送给后端。配置关闭或请求不是带 `messages` 的 JSON object 时保持原样转发。

## 错误与可观测性

- tokenize 网络错误、非 2xx 或未知响应格式均视为降级，不阻断请求。
- 裁剪后仍超预算返回 400，不调用后端 completion。
- 通过 tracing 输出 `[TOKEN-GUARD]`，包含 budget、估算 token、最终 token 和删除轮数；错误输出 `[TOKEN-GUARD-REJECTED]`。
- 不在本阶段删除 KV 快照；context overflow 自愈阶段通过显式失效回调接入。

## 测试

单测覆盖预算计算、无 messages/预算内/ tokenize 失败、轮次二分裁剪、tool 配对保留、超大消息头尾裁剪和最终拒绝。网关 E2E 使用 mock `/v1/tokenize` 验证真实 HTTP 计数、请求体裁剪和 400 不转发。
