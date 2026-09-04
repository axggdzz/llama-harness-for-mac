# 第五阶段 B：资源指标与后端探针

新增 `resources::ResourceSnapshot`，基于 `sysinfo` 采集 CPU、统一内存使用量和压力比例，并明确显示 macOS Metal/统一内存语义；独立显存指标不可用时通过能力说明保留 UI 布局位置。macOS 上额外提供 `vm_stat` 解析入口，便于后续接入 memory pressure 细分。

网关提供：

- `GET /__resources__`：当前系统资源快照。
- `GET /__backend/slots`：后端 `/slots` 原始响应。
- `GET /__backend/props`：后端 `/props` 原始响应。
- `GET /__backend/metrics`：后端 `/metrics` 原始响应。

这些探针会复用现有按需启动和 readiness 流程，不使用 CUDA 或 `nvidia-smi`，并过滤逐跳响应头以保持客户端兼容。
