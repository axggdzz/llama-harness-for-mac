# llama.cpp 版本兼容矩阵

兼容判断以启动前 `llama-server --help` 和后端 HTTP 探针为准，不把版本号当作唯一
协议判断依据。未知版本默认走安全降级，并保留原始参数（能力探测失败时不主动删除
自定义选项）。

| 能力/协议 | llama.cpp 0.3.0（本机实测） | 网关策略 |
| --- | --- | --- |
| `--metrics` / `/metrics` | 参数默认关闭；启用参数后正常 | 未启用时标记不可用，使用网关本地统计 |
| `/props` | 正常，包含 `build_info` | 展示 build 信息并作为版本来源 |
| `/slots` | 正常，默认启用 | 用于槽位状态和 SlotAffinity |
| `/v1/tokenize` | 404，不可用 | TokenGuard 保留原请求并记录降级原因 |
| `--slot-save-path` | 需要配置；文件名必须是 basename | 网关自动提取目录并使用 basename |
| slot save 响应 | `n_saved=0`、`n_written>0` | 以 `n_written` 作为有效性回退值 |
| slot restore | 使用 basename 正常 | 校验本地快照后再调用后端 |
| slot erase | 对不存在文件可返回 404 | 网关返回诊断错误，不删除本地索引 |
| SSE | `text/event-stream`，事件边界正常 | 增量透传并支持 length 续接 |

## 未来版本接入规则

1. 新版本先运行 `llama-server --help`，确认参数是否仍存在。
2. 启动后调用 `/__capabilities__`，检查 `/props`、`/slots`、`/metrics`、`/v1/tokenize`。
3. 对 KV 先执行 save/restore/erase 小快照，再允许自动驱逐保存。
4. 对不支持能力只关闭对应功能，不阻断 JSON/SSE 主链路。
5. 将新版本实测结果追加到本表，并保留失败响应和 stderr 摘要。
