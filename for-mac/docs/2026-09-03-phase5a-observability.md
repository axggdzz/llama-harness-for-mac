# 第五阶段 A：日志与统计

新增 `observability` 模块：`RotatingLogger` 将主日志、`slot-N.log` 和错误日志写入 macOS 数据目录，并在超过 `log_max_bytes` 时原子轮转为 `.1` 文件。`request_dump_enabled` 可选记录完整请求体，默认关闭以避免敏感内容进入日志。

`Stats` 使用原子计数器和 slot 集合记录请求数、prompt/completion token、最近速度、KV restore 命中/未命中和当前使用 slot。网关通过 `GET /__stats__` 暴露 JSON 快照；后续 UI 可直接消费该稳定结构。
