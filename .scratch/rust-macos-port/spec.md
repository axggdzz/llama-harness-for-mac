# Rust/macOS 版 LlamaHarness 功能等价与 UI 高保真移植规格

## Problem Statement

当前 LlamaHarness 是面向 Windows 的 .NET 8 WinForms 应用，围绕 llama-server 提供长 Agent 场景的资源治理。用户希望在 macOS（重点是 Apple Silicon + Metal）上使用 Rust 获得同等能力，同时最大限度保留 Windows 版的功能、配置语义、状态反馈和操作界面。

直接移植 WinForms 和 Windows 专用实现不可行：WinForms、HttpListener、Windows 命名 Mutex、进程树 Kill、ProcessorAffinity、kernel32 内存/CPU API、nvidia-smi/CUDA 路径均依赖 Windows。若只移植一个 HTTP 代理，又会丢失 KV 生命周期治理、槽位亲和、TokenGuard、自动续接、自愈和仪表盘体验。

## Solution

以 Rust 重写一个跨平台核心与 macOS 运行时，并提供保持 Windows 版信息架构和交互语义的桌面 UI。核心仍以 llama-server 为推理底座，前端固定监听本机回环地址，客户端继续使用 OpenAI 兼容 API；macOS 端改用 Metal/统一内存、Unix 进程组、Application Support 路径和 macOS 系统指标。

移植采用“功能等价、平台实现替换、UI 高保真”的原则：业务规则和配置字段保持兼容；平台差异集中在进程、文件路径、系统监控和 UI 壳层；不适用于 macOS 的 P 核亲和和 NVIDIA 显存能力在 UI 中保留对应位置，并显示 macOS 等价指标或不可用状态。

验收主接缝为“桌面 UI + OpenAI 兼容 HTTP 网关 + mock llama-server”端到端流程，覆盖启动、代理、KV、TokenGuard、续接、崩溃恢复、休眠和 UI 状态反馈。

## User Stories

1. 作为 macOS 用户，我希望使用原生 Apple Silicon 构建的 Rust 应用，以便无需安装 .NET 或运行兼容层。
2. 作为现有 Windows 版用户，我希望沿用熟悉的左侧控制面板、页签和底部统计卡片，以便不重新学习操作流程。
3. 作为用户，我希望在配置页设置模型路径、llama-server 路径、上下文长度、并行槽数和 Metal 推理参数，以便按硬件调整性能。
4. 作为用户，我希望配置文件字段和 Windows 版保持兼容，以便迁移既有配置并减少重新配置工作。
5. 作为用户，我希望应用自动定位 llama-server，并在版本不支持某些参数时安全降级，以便底层版本升级不会导致静默启动失败。
6. 作为用户，我希望点击启动后看到待机、唤醒、预热、运行等状态变化，以便判断模型是否真正可用。
7. 作为用户，我希望客户端始终连接固定的本机端口，以便唤醒、休眠和后端端口变化对客户端透明。
8. 作为用户，我希望第一个请求能够自动唤醒 llama-server，后续请求复用同一后端，以便按需使用模型资源。
9. 作为用户，我希望闲置达到配置时长后自动保存状态并释放模型进程，以便降低统一内存占用。
10. 作为用户，我希望自动休眠期间的新请求能够取消休眠并继续服务，以便避免请求丢失。
11. 作为用户，我希望停止操作能够终止整个 llama-server 进程组，以便不留下孤儿进程。
12. 作为 Agent 用户，我希望系统根据请求头识别会话或应用并绑定固定 slot，以便减少跨槽漂移和 KV 重算。
13. 作为多会话用户，我希望 slot 满时按照 LRU 选择可驱逐绑定，以便有限资源下仍能服务新会话。
14. 作为重要 Agent 用户，我希望启用强占保护，使其 slot 尽量不被 LRU 驱逐，以便长任务稳定执行。
15. 作为 Tool 调用用户，我希望工具循环期间临时锁定 slot，并在循环结束后自动解除，以便保护正在执行的工具链。
16. 作为用户，我希望强占绑定数量受到 slot 数量上限约束，以便单槽和多槽模式都不会造成永久排队死锁。
17. 作为用户，我希望被驱逐的 slot 能在驱逐前保存 KV 快照，以便新请求或进程重启后恢复上下文。
18. 作为用户，我希望休眠前所有有效 slot 都能保存 KV，以便下次唤醒跳过长上下文全量预填充。
19. 作为用户，我希望 restore 前自动校验快照文件和元数据，以便损坏快照不会污染新请求。
20. 作为用户，我希望损坏或不兼容快照会被标记并降级为全量 prefill，以便系统能够自愈而不是持续失败。
21. 作为用户，我希望快照索引、token 数、时间和文件大小可在 UI 中查看，以便判断缓存是否有效。
22. 作为长上下文用户，我希望系统调用 llama-server 的真实 tokenize 接口估算 token，以便防止上下文超限。
23. 作为 Agent 用户，我希望 TokenGuard 优先删除完整旧轮次，而不是破坏 tool_call/tool_result 配对，以便保留对话语义。
24. 作为用户，我希望单条超大消息能够头尾保留后截断，以便巨型工具结果不会直接导致 400 错误。
25. 作为用户，我希望 TokenGuard 计量包含消息估算、输出预留和工具/system/Jinja 隐形开销，以便预算决策可解释。
26. 作为用户，我希望 restore 后再次执行上下文预算校验，以便恢复的 KV 不会与新 prompt 叠加后超限。
27. 作为用户，我希望遇到 context overflow 400 时自动收紧预算、废弃旧 KV 并重试一次，以便常见边界错误能够自愈。
28. 作为用户，我希望通过自然语言指令切换 Off、Low、Medium、XHigh 思考档位，以便 Agent 任务动态调整推理深度。
29. 作为用户，我希望网关清洗客户端自带的 reasoning 参数并统一注入 chat_template_kwargs，以便不同客户端行为一致。
30. 作为用户，我希望 Off 档显式关闭 thinking，以便混合思考模型不会把 reasoning 文本混入工具调用 JSON。
31. 作为流式客户端用户，我希望 SSE 事件始终以合法空行结束，以便严格 OpenAI 客户端不会拼接多个 JSON。
32. 作为用户，我希望输出因 max_tokens 截断时自动续接，并把多轮内容合并成一次响应，以便客户端无需改代码。
33. 作为工具调用用户，我希望包含 tool_calls 的响应不会被错误地自动续接，以便工具协议保持正确。
34. 作为用户，我希望续接期间收到合法 SSE keep-alive，以便长时间等待不会被客户端误判为断线。
35. 作为用户，我希望检测到 bad_alloc、OOM 或后端进程退出时自动尝试恢复，以便短暂故障不必手工重启。
36. 作为用户，我希望客户端已断开时停止重放请求，以便避免无意义的模型计算。
37. 作为用户，我希望短时间连续崩溃触发熔断，以便防止无限重启循环消耗资源。
38. 作为用户，我希望日志按主日志、slot 事件、警告错误和请求 dump 分流，并支持轮切，以便长期运行仍可诊断。
39. 作为用户，我希望日志包含快照损坏、保存失败、上下文溢出和恢复判定等结构化标记，以便外部工具可以检索和统计。
40. 作为用户，我希望在统计页看到 prompt/completion token、速度、恢复命中率和续接次数，以便评估系统效果。
41. 作为用户，我希望在槽位绑定页查看应用、slot、强占和 KV 状态，并能手动修改，以便控制资源分配。
42. 作为 macOS 用户，我希望系统资源页显示 CPU、系统内存、llama-server RSS 和 Metal/统一内存信息，以便替代 Windows/NVIDIA 指标。
43. 作为用户，我希望系统资源页手动刷新 `/slots`、`/props`、`/metrics`，并保留原始响应，以便排查底层状态。
44. 作为用户，我希望信息展示页继续内嵌使用说明、FAQ 和更新内容，以便无需打开外部窗口。
45. 作为用户，我希望窗口关闭时优雅停止日志管道、HTTP 监听器和 llama-server，以便不丢日志或留下后台进程。
46. 作为开发者，我希望核心逻辑可以在 macOS CI 中通过 mock llama-server 自动测试，以便不依赖真实模型即可回归关键行为。
47. 作为维护者，我希望将平台相关实现隔离在适配层，以便未来支持 Linux 或恢复 Windows UI 时无需重写业务规则。

## Implementation Decisions

- 采用 Rust workspace，划分为平台无关核心、macOS/进程运行时、HTTP 网关/调度器和桌面 UI 四个边界；核心业务模块不依赖 UI 或 macOS API。
- 桌面 UI 采用 Tauri 2（Rust 后端 + Web 前端）或等价的 Rust 桌面壳；必须保留 Windows 版的左侧控制面板、七个页签、底部统计卡片、暗色主题、字段名称、状态文本和主要交互顺序。UI 技术实现可不同，但信息架构和行为保持等价。
- 网关使用 Tokio 异步运行时、Axum/Hyper HTTP 服务和 Reqwest 后端客户端；只监听 `127.0.0.1`，继续提供 OpenAI 兼容路径。
- 调度状态保持 `Standby → Waking → Warming → Running → Sleeping`。唤醒任务必须合并并发请求；休眠必须经过静默观察期，确认无新请求且无在途任务后执行 KV 保存和进程组停止。
- llama-server 通过参数数组启动，不再拼接一整段命令行字符串；路径、空格和附加参数必须安全传递。
- 启动参数由能力探测层根据 `llama-server --help` 或版本信息决定是否发送；不支持的 Metal/CUDA 专用参数必须记录降级原因并继续启动。
- macOS 进程管理使用 Tokio Command、独立 Unix process group、SIGTERM→超时 SIGKILL；stdout/stderr 异步读取并保留退出码和最近输出。
- 单实例使用 Unix 文件锁或 Unix Domain Socket；配置、日志和 KV 文件放在 macOS Application Support 目录，不写入 `.app` 包目录。
- KV 快照协议继续调用 `/slots/{id}` 的 save、restore、erase 操作；快照文件使用可读前缀加稳定哈希命名，避免清理非法字符造成碰撞；索引中的文件大小统一使用无符号 64 位整数。
- SlotAffinity 保留请求头优先级、固定 slot、LRU 驱逐、Tool 锁定、自动强占和 `preemptive ≤ slot_count - 1` 不变量；锁外等待不得阻塞其他槽位操作。
- TokenGuard 保留真实 tokenize、输入预算公式、轮次二分删除、超大消息头尾裁剪、restore 后复核以及 context overflow 自愈；预算至少包含输出预留和 prompt 头部隐形开销。
- 思考模式保留 Off/Low/Medium/XHigh 状态机、自然语言切换、客户端字段清洗和统一参数注入；Off 必须显式写入 `enable_thinking=false`。
- SSE 管道必须按事件边界转发并补齐空行；finish_reason=length 时暂扣末块、抑制中间 `[DONE]`、执行续接，最终合并 usage 并归一化为 stop；tool_calls 响应不得自动续接。
- 崩溃恢复保留存活进程快照接续、死亡进程重启重放、客户端断开取消重放、keep-alive 和熔断器策略。
- 日志使用单有界队列和后台写线程，保留四类日志、双阈值 flush、轮切、IO 退避、shutdown drain、最近日志环形缓冲和结构化 EDGE-CASE 标记。
- 系统指标使用跨平台库采集 CPU/系统内存/进程 RSS；Metal/统一内存作为 macOS 资源展示，NVIDIA 显存和 P 核掩码不作为 macOS 必需能力，但 UI 保留兼容位置并标记状态。
- 配置迁移必须支持 Windows 版 snake_case 与旧 PascalCase 字段读取；新配置使用 macOS 默认路径和 Metal 合理默认值，不照搬 `g:/temp`、`mlock` 或 13900F P 核掩码。
- 对外 API、配置语义、状态名称、日志关键字和 UI 文案尽可能稳定；平台特有差异通过能力字段和状态提示表达。
- 首个可发布版本先实现无真实模型依赖的 mock 后端端到端验证，再接入真实 Metal llama-server；UI 和 daemon 必须共享同一核心状态模型。

## Testing Decisions

- 测试只验证用户可观察行为和公开边界，不绑定具体 Rust 内部函数、线程数或组件实现。
- 最高层测试使用可控 mock llama-server，提供 `/health`、`/v1/chat/completions`、`/v1/tokenize`、`/slots`、`/props`、`/metrics`，能够模拟延迟、SSE、截断、400 context overflow、bad_alloc、进程退出和损坏快照。
- 端到端测试覆盖 UI 操作或等价命令触发启动/停止/休眠、固定前端端口、并发唤醒合并、请求转发、客户端断开和进程组清理。
- SlotAffinity 测试覆盖指纹优先级、已有绑定复用、LRU 驱逐、全强占排队、超时降级、Tool 锁定优先级以及强占 cap。
- KV 测试覆盖 save/restore/erase、并发 save 去重、save 后文件和元数据校验、restore 前损坏检测、休眠前逐槽保存和索引恢复。
- TokenGuard 测试覆盖预算公式、无 user 消息透传、按完整轮次裁剪、二分收敛、超大消息头尾保留、tokenize 失败降级和最终仍超限时的标准错误。
- SSE/续接测试覆盖 `data` 事件空行、usage 合并、finish_reason=length、多轮续接、tool_calls 不续接、keep-alive、`[DONE]` 抑制和客户端断开。
- 思考模式测试覆盖自然语言切换、字段清洗、Off 显式关闭、Low/Medium/XHigh 注入和跨请求状态保持。
- 崩溃恢复测试覆盖 bad_alloc 双源检测、存活 restore、进程重启重放、客户端断开取消、最大重启次数和熔断。
- 日志测试覆盖队列满策略、FIFO、轮切、IO 失败退避、drain、结构化标记和最近日志读取。
- 配置测试覆盖默认值、snake_case/PascalCase 兼容、原子保存、非法值兜底、macOS Application Support 路径和 Windows 配置迁移。
- UI 测试覆盖七个页签存在、关键控件可操作、状态文本、日志/统计/槽位/资源数据刷新、配置保存和关闭清理；视觉回归使用固定窗口尺寸截图比较，允许字体渲染的受控差异。
- 在 macOS arm64 CI 上运行全部 mock 集成测试；真实 Metal llama-server 测试作为硬件标记测试，不阻塞无 GPU/无模型的默认 CI。

## Out of Scope

- 不重写 llama.cpp 推理内核，不打包模型权重，不替代 llama-server。
- 不保证与 Windows WinForms 的像素级逐像素一致；保证布局、颜色层级、字段、状态、交互和信息密度高保真。
- 不在第一版实现 macOS 上强制 P 核/能效核亲和；该能力显示为不可用或使用系统调度。
- 不实现 NVIDIA CUDA/nvidia-smi 监控；macOS 使用 Metal/统一内存指标。
- 不默认创建或依赖 RAMDisk；KV 默认使用 Application Support 下的 APFS 路径。
- 不支持大规模高并发服务治理，不改变原项目低并发长 Agent 定位。
- 不自动修改用户配置进行性能调优，不引入自动调参策略。
- 不在本规格内完成 Windows 版重构或 Linux 版发布。
- 不把 GitHub Issue、远程发布流程或外部 DuckDB/Streamlit 面板作为运行时必需依赖。

## Further Notes

- 当前上游 v2.13 的关键设计是关闭内置 prompt-cache、用 KV 快照接管持久化、区分 HitByDelta 与真实 MISS，并对快照损坏和 context overflow 进行可观测自愈；这些语义应作为移植基线。
- macOS Apple Silicon 采用统一内存，ctx、parallel、batch 和 Metal offload 的默认值必须通过实际机型基线确定，不能直接复制 13900F/NVIDIA 的黄金参数。
- 建议交付顺序为：Rust 核心与 mock 网关 → macOS 进程/路径适配 → KV/slot/TokenGuard/续接/恢复 → UI 高保真复刻 → Metal 硬件验证 → `.app` 签名与 notarization。
- UI 与核心之间应使用版本化事件模型，至少包括 phase、backend_ready、inflight、slot_bindings、restore_stats、system_metrics、logs 和 config_changed，避免再次形成 WinForms 式强耦合。
- 发布前需明确支持的 macOS 最低版本、Apple Silicon/Intel 架构范围、llama.cpp commit/版本范围和签名策略；这些属于实现阶段的发布决策。
