# 第四阶段 B：context overflow 自愈

本阶段在 TokenGuard 前置保护之外增加一次受控的 context overflow 恢复：

1. 网关只对后端返回的 HTTP 400 响应进行检查，并匹配 `context`、`prompt too long`、`maximum context`、`n_ctx` 等明确关键词。
2. 命中后，若请求已分配 slot，先调用 `/slots/{slot}?action=erase` 清除后端 slot 状态，再使用完全相同的请求体重试一次。
3. 非 overflow 的 400 不重试，原始状态码、响应头和响应体透传。
4. 重试仍失败时不进入无限循环；响应按后端结果返回。
5. 后端连接错误仍将网关后端句柄移出并调用进程组停止，避免恢复路径遗留进程。

恢复默认开启，可通过 `AppConfig.context_overflow_recovery=false` 关闭。该阶段不伪造模型响应，也不吞掉非 context 相关错误。mock llama-server 提供 `--overflow-once` 用于端到端验证。
