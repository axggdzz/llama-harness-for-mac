# 第六阶段 C：llama-server 能力探测与安全降级

本阶段为不同版本的 `llama-server` 增加启动参数兼容层。只有当可执行文件名精确为
`llama-server`（或 Windows 兼容名 `llama-server.exe`）时，启动前才会执行
`llama-server --help`，从帮助文本提取声明支持的 `--flag`，并从参数数组中移除未声明的
选项及其值。参数仍以数组传递，不经过 shell 重新解析，因此模型路径和带空格的值保持不变。

探测失败、超时或帮助文本为空时，系统保留完整原始参数数组，以兼容自定义构建和 mock 后端。
非 llama-server 可执行文件也不会被探测，避免测试替身或包装脚本被误判。网关的
`GET /__capabilities__` 继续通过 `/props`、`/slots`、`/metrics` 和 `/v1/tokenize` 返回运行时
能力，供 UI 展示和后续功能降级使用。

验证：`cargo fmt --check`、`cargo check`、`cargo test` 全部通过；能力过滤、空帮助回退、精确
二进制识别和网关端到端流程均有测试覆盖。
