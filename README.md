# Llama‑cpp‑harness：长 Agent 本地私有化资源治理框架

> **资源治理抬高效率下限，硬件决定性能上限。**
>
> 专为低并发、高可靠复杂 Agent 任务深度优化。

Llama‑cpp‑harness 是 Windows 平台面向工程开发者的 **llama‑cpp（llama‑server）智能代理网关**。
不简单依靠降低模型规模、缩短上下文来妥协显存压力；通过**业务语义层显存 / KV 缓存精细化治理**，在硬件物理上限之内，榨取出更长上下文、更少无效重算、更高任务稳定性。

> llama‑cpp 只负责「能不能跑」；Harness 负责「怎么跑的高效、稳定」。

## 核心定位说明

llama‑cpp 内置包含两套 LRU 机制，本项目做差异化处理：

2. ✂️ **彻底关闭 prompt‑cache 内存层 LRU 驱逐**：该层为无业务感知的静默驱逐，无通知、不可观测，会随机丢弃高价值 Agent KV，改用 RAMDisk 快照接管持久化；
3. 🛡️ **接纳并兜底 slot 槽位分配层 LRU / LCP 相似度匹配**：尊重底座原生多槽调度逻辑，不做对抗式强制关闭；依靠日志埋点感知抢占事件，RAMDisk 快照做补偿恢复；

> 设计思想：**底座优先，上层兜底，尽力而为**。最大限度复用 llama‑cpp 原生调度红利；底层发生抢占扰动由 Harness 观测、告警、快照补偿，隔离对上层 Agent 业务的冲击。

> ⚠️ 边界声明：本项目聚焦**低并发长会话 Agent 场景**，追求单任务稳定性与 KV 复用效率；整体吞吐上限受 GPU 显存、算力硬件约束，不面向大规模高并发多用户在线服务。

## ✨ 核心能力

### 🧠 KV 缓存全生命周期治理（核心亮点）

- **RAMDisk KV 冷热快照**：热会话驻留 GPU 显存获取最高性能；冷会话导出至高速内存盘；会话被 slot‑LRU 抢占、进程重启、休眠唤醒时可快速 restore 恢复 KV，避免数万 token 全量重算。
- **槽位亲和绑定**：Agent 指纹自动绑定固定 slot，减少跨槽漂移；支持**自动强占锁槽**、**仅快照持久化（不锁槽）**两种模式，职责解耦。
- **Tool 工具链会话锁定**：检测到工具调用循环时自动标记锁槽，保护正在运行的复杂 Agent 任务不被轻易驱逐，工具循环结束自动解锁。
- **可观测 KV 命中统计**：区分`HitByDelta虚假MISS`与真实全量重算 MISS；统计 restore 命中率，低于阈值输出告警；日志区分 DEBUG/INFO 级别消除业务刷屏噪音。
- **快照完整性校验**：save 后写入元数据；restore 前校验快照完整性，快照损坏触发`[EDGE‑CASE‑SNAPSHOT‑CORRUPT]`埋点并降级兜底。

### 🛡️ TokenGuard 多层上下文防护

- 区分业务消息 token 与**tools/Jinja 模板 /system prompt 隐形 token 开销**；配置`ReservedPromptOverhead`预留头部隐形载荷预算。
- 真实 tokenize 计数，轮次二分裁剪，规避 400 上下文超限；
- restore 加载 KV 后重新校验上下文预算；捕获 400 报错执行激进裁剪 + 废弃 KV 自愈重试；
- 完整计量日志输出，供外部 DuckDB / 监控面板统计 prompt 隐形开销。

### ⚡ 思考模式状态机

- 全局统一管理推理思考档位 `Off / Low / Medium / XHigh`；
- 拦截自然语言指令切换思考模式；清洗客户端传入参数，网关层统一注入 chat_template_kwargs；
- 对 Qwen 系列混合思考模型，Off 档位显式关闭 reasoning 输出，避免思考文本污染 tool‑call JSON。

### 🚨 崩溃与异常自愈体系

- **bad_alloc/OOM 双源检测**：进程存活快照接续恢复；进程死亡自动重启重放请求；客户端断开则停止重放；
- 熔断器保护：短时间多次崩溃触发熔断告警，防止无限重启循环；
- SSE keep‑alive 保活，大部分故障做到**客户端无感知恢复**；
- finish_reason=length 输出截断自动断点快照 + 无感续接。

### 📊 全链路可观测

- WinForms 仪表盘 UI：日志、统计、槽位绑定管理、系统资源、llama‑cpp 三接口（`/slots` `/props` `/metrics`）手动采集；
- 独立分级日志流水线：主日志、槽位事件日志、错误告警日志、请求 dump 日志，日志文件自动轮切；
- EDGE‑CASE 边界事件埋点：快照损坏、快照保存失败、上下文超限，全部可被外部 DuckDB+Streamlit 采集做长期运行分析；
- 硬件资源采集：CPU、内存、NVIDIA GPU 显存采样。

### 🎛️ 调度能力

- **按需唤醒 / 闲置休眠**：无任务时杀掉 llama‑cpp 完全释放显存；有推理请求自动唤醒；休眠前自动保存全部有效 KV 快照；
- Intel 混合 CPU P‑核亲和绑定，支持运行时漂移自愈；
- OpenAI 兼容代理网关，客户端对接地址固定，后端 llama‑cpp 端口自动探测。

## 📊 和同类项目对比

表格

| 项目                        | 定位                                          | 长 Agent 复杂工具调用场景表现                                                                                      |
| --------------------------- | --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| **llama‑cpp(llama‑server)** | 高性能推理底座                                | 推理速度强；缺少业务层资源治理；prompt‑cache 静默 LRU 驱逐，无快照、无防护，黑盒行为多                             |
| **Ollama**                  | 面向普通用户，开箱即用                        | 上手门槛极低；底层调度高度封装，几乎没有 KV 生命周期管控，长 Agent 遇到随机驱逐只能被动承受性能跳水                |
| **vLLM**                    | 主打高并发吞吐                                | 高并发性能优秀；Windows 适配弱；缺少面向 Agent 的业务语义层治理、快照续接、边界自愈能力                            |
| **Llama‑cpp‑harness**       | 面向工程开发者：低并发长 Agent 私有化资源治理 | **本项目**；接管 KV 完整生命周期，多层上下文防护、快照冷热交换、完备自愈与埋点；牺牲部分开箱便捷换取可控性与稳定性 |

> 本项目不追求全场景通吃，专注低并发、高可靠长 Agent 私有化赛道，把现有硬件的实战效率下限做到最高。

## 🧩 技术栈

- UI：WinForms .NET 8（零第三方 NuGet 依赖，BCL 原生 API）
- 后端：llama‑cpp llama‑server（HTTP OpenAI 兼容接口）
- GPU：NVIDIA CUDA
- 观测配套：Python + Streamlit + DuckDB（外部数据分析，不属于主程序 exe）

## 📁 项目结构

```
lunch/
├── LlamaHarness/          # C#主程序WinForms
├── LlamaHarness.Tests/    # XUnit自动化测试套件
├── config/                # 运行时配置（config.json / slot_bindings.json / kv_cache_index.json）
├── logs/                  # 分级日志目录，自动轮切
└── docs/
    ├── 架构设计说明书.md          # 完整架构、模块、变更历史、约束、故障矩阵
    ├── RAMDisk快照全权接管工程方案.md
    └── baseline‑reference.md     # 硬件&业务基线参考文档（待补充）
```

## 🔨 构建 & 运行

### 前置条件

- Windows10+
- .NET 8 SDK
- 编译好的 llama‑cpp llama‑server.exe
- NVIDIA GPU（CUDA）

### 编译

```
msbuild LlamaHarness.sln /p:Configuration=Release
# 或者Visual Studio打开解决方案F5编译运行
```

### 使用步骤

2. 运行`LlamaHarness.exe`
3. 在【配置管理】页签填入 llama‑server 路径、模型路径，调整上下文、并行槽数、RAMDisk 快照目录等参数
4. 保存配置，点击【启动 / 唤醒】
5. 客户端连接本机 `http://127.0.0.1:8080`，使用标准 OpenAI‑compatible 接口发起 Agent 请求

> 前端网关端口固定 8080；llama‑cpp 后端端口由程序自动探测分配，客户端无需关心。

## ⚙️ 关键配置说明（config.json）

表格

| 配置项                   | 说明                                                                                  |
| ------------------------ | ------------------------------------------------------------------------------------- |
| `CacheRamMiB`            | `0`= 关闭 llama‑cpp 内置 prompt‑cache（推荐，RAMDisk 全权接管）；填`8192`为回滚旧模式 |
| `NoCacheIdleSlots`       | 禁止 release 空闲 slot 写入 prompt‑cache，配合 CacheRamMiB=0 使用                     |
| `ReservedPromptOverhead` | 预留 tools、jinja 模板等隐形 token 开销，TokenGuard 防护使用                          |
| `AutoPreemptiveApps`     | 自动强占锁槽的 Agent 前缀，锁槽会抑制 slot‑LRU 驱逐                                   |
| `AutoSnapshotKeys`       | 仅做快照持久化，**不会锁槽**；崩溃重启可恢复，但允许被 slot‑LRU 正常抢占              |
| `KvCachePath`            | RAMDisk/SSD 快照输出目录，开启 save/restore 的必要条件                                |

> 更多完整字段、约束、回滚策略，参考：[docs / 架构设计说明书.md](docs/%E6%9E%B6%E6%9E%84%E8%AE%BE%E8%AE%A1%E8%AF%B4%E6%98%8E%E4%B9%A6.md)

## 📐 设计原则（摘录）

2. **底座优先，上层兜底，尽力而为**：复用 llama‑cpp 原生能力，不激进对抗底层调度；风险在网关层做观测与补偿。
3. **配置职责单一**：一个配置开关只负责一类能力；快照持久化与槽位强占拆分为两套独立配置。
4. **可观测优先**：所有边界异常输出带统一标记的日志埋点，便于 grep 检索与外部 metrics 统计。
5. **尽力自愈，不掩盖硬件故障**：IO 损坏、硬件异常输出告警，不会盲目自动调参掩盖底层问题。

## 📝 后续规划

2. 输出`baseline‑reference.md`：不同硬件性能基线，异常事件健康阈值；
3. 开发 Agent Skill：读取本地 metrics 监控数据，对照基线输出参数调优建议（仅输出建议，不自动覆写配置）；
4. 持续丰富手动验证清单与自动化测试用例。

## License

MIT License



# QuickStart 快速上手

> 目标：5‑10 分钟跑通整套网关，体验 KV 快照、资源治理能力。

## 前置准备

2. 操作系统： Windows 10 / Windows 11
3. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/zh%E2%80%91cn/download/dotnet/8.0)
4. 编译好 **llama‑cpp**，拿到 `llama‑server.exe`
5. GGUF 模型文件（推荐 Qwen 系列等支持工具调用的模型）
6. 建议配置 RAMDisk（例如 ImDisk）作为 KV 快照目录，`g:/temp`，也可以先用普通 SSD 目录临时测试。

> ⚠️ 重要提示：
> 本项目**只是网关代理程序，不内置 llama‑cpp 和模型权重**，需要你自行准备。

## 编译项目

```
# Release编译
msbuild LlamaHarness.sln /p:Configuration=Release
```

编译产物输出在 `LlamaHarness\bin\Release\net8.0‑windows\`，运行 `LlamaHarness.exe`。

## 基础配置步骤

2. 打开程序，切换到【配置管理】页签

3. 填写关键路径：

   - `exe`：你的 `llama‑server.exe` 完整路径
   - `model`：GGUF 模型完整路径
   - `KvCachePath`：快照目录，优先 RAMDisk，如 `g:/temp`

4. 核心参数参考（可按你的显卡调整）

   ```
   CtxSize: 65536
   Parallel: 1
   Ngl: 999
   CacheRamMiB: 0
   NoCacheIdleSlots: true
   ReservedPromptOverhead: 10240
   ```

5. 点击页面下方【保存】，配置写入 `config/config.json`。

> `CacheRamMiB=0 / NoCacheIdleSlots=true` 为推荐生产配置，关闭 llama‑cpp 内置 prompt‑cache，由 Harness+RAMDisk 接管 KV 持久化。
> 如果你需要回退旧模式：设置 `CacheRamMiB=8192`，`NoCacheIdleSlots=false`。

## 启动服务

2. 点击左侧面板【启动 / 唤醒】按钮；
3. UI 状态会从 `待机中` → `唤醒中` → `预热中` → `运行中`；
4. 启动成功后，**网关固定监听 `http://127.0.0.1:8080`**；llama‑server 使用自动探测的后端端口，客户端无需关心。

> Warming「预热中」阶段会执行 eager restore + dummy 预热，属于正常现象，耐心等待完成。

## 发起测试请求

使用任意 OpenAI 兼容客户端 /curl/ Trae / DSH Agent，向 `http://127.0.0.1:8080/v1/chat/completions` 发送请求。

示例 curl（PowerShell）：

```
curl http://127.0.0.1:8080/v1/chat/completions `
‑H "Content‑Type: application/json" `
‑d '{
  "model": "test",
  "stream": true,
  "messages": [{"role":"user","content":"简单介绍一下llama‑cpp‑harness"}]
}'
```

## 观测运行状态

- 【日志】页签：查看 `[KV‑SAVE]` `[KV‑RESTORE]` `[KV‑RESTORE‑JUDGE]`、TokenGuard、EDGE‑CASE 事件；
- 【统计】页签：查看每轮 token 速度、f_sim_best、restore 命中率；
- 【槽位绑定】页签：查看 Agent‑slot 绑定、强占标记；
- 【系统资源】页签：点击【手动刷新】查看 CPU / 显存、llama‑cpp `/slots` `/props` `/metrics` 原始信息。

## 停止

- 点击左侧【停止】：直接终止 llama‑cpp，回到待机；
- 开启 `AutoMode=true` 自动模式：闲置 IdleMinutes（默认 15 分钟）无人请求，会自动休眠，休眠前自动保存 KV 快照、完全释放 GPU 显存。

> 💡 使用建议：
> 复杂 Agent 工具调用场景，把你的 Agent key 加入 `AutoSnapshotKeys`，会话会自动快照，休眠重启后可恢复上下文；
> 如果需要锁槽不被驱逐，把 key 加到 `AutoPreemptiveApps`。

---

# FAQ 常见问题

### Q1：这个项目和 Ollama 的区别？我可以直接替换 Ollama 吗？

A：定位不同，不适合小白开箱即用替换。

- Ollama：面向普通用户，极致开箱即用，底层高度黑盒封装；长 Agent 复杂任务遇到 LRU 随机驱逐无干预手段。
- llama‑cpp‑harness：面向工程开发者，**牺牲一部分开箱便捷换取深度可控性**；重点解决长会话 Agent 的 KV 生命周期、上下文防护、崩溃自愈；适合私有化低并发场景。

> 不适合大规模多用户高并发线上服务。

### Q2：`CacheRamMiB=0` 是什么含义，必须设置为 0 吗？

A：`CacheRamMiB=0` 关闭 llama‑cpp 内置 prompt‑cache，消除静默无通知的 prompt‑cache LRU 驱逐，KV 持久化交给 RAMDisk 快照接管，**推荐生产使用**。
可以回退旧模式 `CacheRamMiB=8192`，但是会重新面临原生 prompt‑cache 随机驱逐导致虚假 KV‑MISS。

### Q3：日志频繁出现 `[KV‑MISS‑DEBUG] HitByDelta`，是异常吗？

A：**不是故障，属于正常现象。**
Agent 每一轮会追加 assistant、tool 返回，消息指纹发生变化；但快照内 KV 前缀仍然有效，只做增量 prefill。
该日志降级为 DEBUG，仅用于排查；真实全量重算会打印 `[KV‑MISS]`。metrics 统计不受日志级别影响。

### Q4：slot 被 LRU 抢占，会直接任务失败吗？

A：不会。
本项目接纳 llama‑cpp slot 层原生 LRU/LCP 调度，不强制关闭；当 slot 被抢占，Harness 感知事件，调用 restore 从 RAMDisk 重新加载 KV，业务层无感知，仅会带来短暂 restore IO + 显存拷贝开销。

> 设计思想：底座优先，上层兜底，尽力而为。

### Q5：restore 恢复快照很慢怎么办？

A：

2. 强烈建议把 `KvCachePath` 指向 **RAMDisk（内存虚拟盘）**，SSD 也可用，但速度会下降；机械硬盘不建议存放 KV 快照；
3. 快照越大（saved_n_tokens 越大），restore 耗时越高，属于物理 IO + 显存拷贝固有开销；
4. 尽量减少不必要的 slot 抢占：主力 Agent 可配置 `AutoPreemptiveApps` 开启强占锁槽，减少抢占触发次数。

### Q6：TokenGuard 依然报 400 上下文超限？

A：

2. 检查 `ReservedPromptOverhead`，工具调用多的 Agent，可以适当调大预留值（如 16384、20480），预留 tools schema、jinja 渲染隐形 token；
3. 确认 `CtxSize`、`Parallel` 设置合理；
4. 程序内置 400 自愈分支：捕获超限后自动收紧预算、废弃 KV 重试；如果仍然频繁触发，说明单轮业务真实 token 已经逼近模型上下文硬上限，需要业务侧裁剪对话历史。

### Q7：Warming 预热时间很长，或者 restore 失败？

A：

2. Warming 有 60s 超时兜底，超时不会阻塞进入 Running；
3. 检查 `KvCachePath` 目录读写权限，磁盘空间是否充足；
4. 查看日志 `[EDGE‑CASE‑SNAPSHOT‑CORRUPT]`，快照损坏会自动废弃快照降级全量 prefill。

### Q8：支持 Linux / MacOS 吗？

A：当前版本 UI 层为 WinForms，**仅支持 Windows**；核心调度网关逻辑理论可移植，但目前没有做跨平台适配。

### Q9：并发可以开很大吗，Parallel 设置几十？

A：硬件显存是硬上限。本项目优化重点是**低并发长会话 Agent**。Parallel 盲目拉高，会大量消耗显存，prefill 开销暴涨，边际收益快速收缩。

> 优化只能减少无效浪费，不能突破硬件物理上限。

### Q10：程序崩溃 / 进程 OOM 之后，会话还能恢复吗？

A：

- 如果崩溃前已经成功执行过 `[KV‑SAVE]` 快照落盘，重启唤醒后该 key 首次请求会执行 restore，恢复会话；
- 如果崩溃发生在 save 之前，内存中 KV 没有落盘，则无法恢复，只能全量重跑。

> 开启 AutoSnapshotKeys 会在 prefill 完成后自动快照，提升崩溃恢复概率。

### Q11：什么是强占 Preemptive 和自动快照 AutoSnapshotKeys，两者区别？

- `AutoPreemptiveApps`：**快照持久化 + 锁槽抑制 slot‑LRU 驱逐**，该 Agent 尽量不被抢占；
- `AutoSnapshotKeys`：**仅快照持久化，不锁槽**。崩溃重启可以恢复，但允许被 slot‑LRU 正常抢占。

> 单槽调试场景，推荐使用 AutoSnapshotKeys；希望锁槽保护任务不被驱逐，使用 AutoPreemptiveApps。

### Q12：我想要调参，有没有一键自动调参功能？

A：目前没有自动覆写配置的自动调参。后续计划输出基线参考文档 + Agent Skill，读取本机 metrics，给出调优建议片段，**由人工确认后修改配置，不会自动改写 config.json，避免 AI 错误调参破坏系统**。

---

完整底层原理、模块细节、变更历史，请查阅 [docs / 架构设计说明书.md](docs/%E6%9E%B6%E6%9E%84%E8%AE%BE%E8%AE%A1%E8%AF%B4%E6%98%8E%E4%B9%A6.md)。

## 
