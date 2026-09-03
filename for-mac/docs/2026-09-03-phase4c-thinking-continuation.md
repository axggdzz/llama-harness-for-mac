# 第四阶段 C：思考模式与 SSE 自动续接

## 思考模式

网关只对 `POST /v1/chat/completions` 的 JSON 请求应用思考模式。当前状态包含 `Off`、`Low`、`Medium`、`XHigh` 四档，默认 `Off`。最后一条连续的 user 消息可以携带“开启思考模式”“关闭思考模式”或三种深度指令；命中后指令文本会从 prompt 中移除，并更新网关状态。

转发前网关清理客户端提供的顶层 `thinking`/`reasoning_effort` 以及 `chat_template_kwargs` 中的同名字段，再注入统一字段：`Off` 使用 `enable_thinking=false`，其余档位使用 `enable_thinking=true` 与对应的 `reasoning_effort`。

## SSE 续接

启用 `continuation_enabled` 后，网关收集流式响应的一轮 SSE。若最后 chunk 的 `finish_reason` 为 `length`，且没有 `tool_calls`，网关会把已生成文本作为 assistant 消息并追加续接指令，最多按 `max_continuations` 再请求一次。续接期间不向客户端发送 `[DONE]`，末轮的 SSE 原样结束；中间轮的 length 被归一化为 `null`。

续接请求受 `continuation_timeout_ms` 限制。达到上限、检测到工具调用或后端续接失败时，网关不循环重试，保留已收到的 SSE 内容。`Off` 思考模式、非 chat 路径和非流式响应不受 SSE 续接改写影响。
