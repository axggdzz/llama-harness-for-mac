# 第四阶段 D：bad_alloc/OOM 恢复

网关现在识别后端 5xx 响应中的 `bad_alloc`、`bad allocation`、`out of memory` 和 `oom` 标记。命中后会消费错误响应，移除并停止当前后端进程组，将生命周期状态回退到 `Standby`，并返回 503；后续请求可以重新拉起后端。

`crash_recovery_enabled` 控制该策略，`max_crash_count` 限制连续恢复失败次数。达到阈值后熔断器保持打开，直接返回 503，不再反复启动后端。任意一次成功请求会清零连续失败计数；普通 4xx、非 OOM 5xx 和正常 SSE 不受影响。

mock 后端支持 `--oom-once`，以及可跨进程保留状态的 `--oom-marker <path>`，用于验证进程停止、下次请求重新启动和熔断边界。
