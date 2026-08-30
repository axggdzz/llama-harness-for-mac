# LlamaHarness 效率优化建议报告

> 范围：全部 15 个 `.cs` 源文件（~5000 行）+ 运行时配置。不改动代码，仅给出优化方向、定位与预期收益。

## 一、高影响（请求热路径，每个推理请求都走）

### E-1 单次请求触发 5~~6 次完整 JSON DOM 解析 + 3~~4 次序列化

这是本项目**最大的 CPU 效率瓶颈**。一条 chat/completions 请求在 `SmartScheduler.PrepareGatewayAsync` 管道中被反复解析-改写-序列化：

| 步骤         | 位置                                         | JSON 操作                                       |
| ------------ | -------------------------------------------- | ----------------------------------------------- |
| 思考模式注入 | `SmartScheduler.InjectThinkingMode` (~L1382) | `JsonNode.Parse` → 改 → `ToJsonString`          |
| Tool 链检测  | `SmartScheduler.DetectToolLoop` (~L989)      | `JsonNode.Parse`（只读末条 role）               |
| n_slots 注入 | `SmartScheduler.InjectNSlots` (~L1527)       | `JsonNode.Parse` → 改 → `ToJsonString`          |
| Token Guard  | `TokenGuard.GuardAsync` (~L57)               | `JsonNode.Parse` 提取 messages                  |
| 前缀哈希     | `SmartScheduler.PrefixHash` (~L1005)         | `JsonNode.Parse` → 逐条 `ToJsonString` → SHA256 |
| 强制流式改写 | `SmartScheduler.EnsureStreamTrue` (~L1380)   | `JsonNode.Parse` → 改 → `ToJsonString`          |

你的 `ctx=222822`、消息体可达数 MB。每条请求对数 MB body 做 5~6 遍完整解析 + 多遍序列化，CPU 开销显著。

**建议**：在 `PrepareGatewayAsync` 入口**一次性** `JsonNode.Parse` 成 `JsonObject root`，后续所有注入/检测/裁剪/哈希都复用同一棵 DOM，最后**只序列化一次**输出 `bodyBytes`。`InjectThinkingMode`/`InjectNSlots`/`DetectToolLoop`/`PrefixHash`/`EnsureStreamTrue` 改为接收 `JsonObject` 而非 `string`，直接在树上操作。预期：热路径 JSON CPU 降 70%+。

### E-2 Token Guard 裁剪循环中反复 HTTP tokenize，阻塞推理

`TokenGuard.GuardAsync`（TokenGuard.cs:68~111）的裁剪逻辑：

- 每删一轮对话后 → `CountTokensAsync`（一次 HTTP POST `/v1/tokenize`）
- 内容兜底截断循环（最多 10 次迭代）→ **每次截断后都重新 tokenize**

最坏情况：一次超长请求触发 **10+ 次** localhost HTTP 往返 + 10 次全量分词，全部串行阻塞在请求关键路径上，推理迟迟无法开始。

**建议**：

1. **二分搜索替代线性重试**：内容截断时用二分法定位截断比例，2~3 次 tokenize 收敛而非 10 次。
2. **增量估算**：删除一整轮对话后，新 token 数 ≈ 原值 − 该轮 token 数。预先按轮 tokenize 缓存各轮 token 数，删除时累减，最后只 verify 一次。
3. tokenize 失败已有降级（透传），但成功路径的多次往返是纯延迟。

### E-3 `bodyBytes ↔ string ↔ body` 多次往返转换

热路径中 `Encoding.UTF8.GetString(bodyBytes)` 和 `Encoding.UTF8.GetBytes(body)` 各出现 4~5 次（SmartScheduler:603/629/661/677/806…）。每次对数 MB 数据做编码转换都分配新的大数组/字符串。

**建议**：与 E-1 合并——解析后全程持有 `JsonObject`，只在管道末端做一次 `ToJsonString()` + 一次 `GetBytes()`。中间步骤不再需要 string↔bytes 往返。

### E-4 前缀哈希对大上下文的每请求全量 SHA256（纯可观测开销）

`SmartScheduler.PrefixHash`（~L1005）每次请求：把除末条外的全部 messages 逐条 `ToJsonString()` 拼成一个超大字符串 → `SHA256.ComputeHash`。对于 200K+ token 的上下文，这是**数 MB 的序列化 + 哈希**，只为输出一行 `[KV-HIT]`/`[KV-MISS]` 日志。

**建议**：将前缀哈希改为**采样/可选**——例如仅在 debug 日志级别开启，或只哈希最后 N 条之前的部分（而非全量），或用更轻量的指纹（消息条数 + 末条内容长度 + role 序列）。当前它是每个请求的纯额外开销。

---

## 二、中影响（并发 / I/O / 稳态）

### E-5 `SlotAffinity.GetSlot` 在锁内 `Thread.Sleep` 最长 30 秒——并发阻塞

`SlotAffinity.cs:92` `lock(_gate)` 包裹整个 `GetSlot`，其中 L127~147 的全槽强占排队分支用 `Thread.Sleep(1000)` 循环等待。这意味着：**一旦某请求进入排队，所有其他请求的槽位操作（GetSlot/SetPreemptive/Snapshot）全部被阻塞最长 30 秒**。多 agent 并发场景下，一个强占满槽就会卡住全部路由。

`AutoPreemptiveApps` 默认含 `trae_global`，Trae 多开时完全可能触发。

**建议**：锁内只做"判定需要等待 + 记录等待者"，把 `Thread.Sleep` 循环移出锁，用 `SemaphoreSlim(0,1)` 或 `Monitor.Wait/Pulse` 实现条件等待。槽位释放时 `Pulse` 唤醒等待者。这是前轮审计 O-3 提出但**尚未落地**的项（代码仍是 Sleep-in-lock）。

### E-6 `LogFile.Append` 每行一次 `File.AppendAllText`（open/write/close）

`LogFile.cs:113/128/142`：每条日志都 `File.AppendAllText`（打开文件→写→关闭）。llama-server 推理时 stdout 极其密集（每秒数十~上百行 token 进度行），每行触发一次文件打开/关闭系统调用。

**建议**：持有一个常驻 `StreamWriter`（或 `FileStream` + 缓冲区），用 150ms 定时器（复用 UI 的防抖节奏）批量 flush。或将 `harness.log` 的写入也并入 UI 的 `_logQueue` 批处理周期。预期：推理期间文件 I/O 系统调用降一个数量级。

### E-7 `Connection: close` 导致代理不复用 TCP 连接

`SmartScheduler.cs:31`：代理 HttpClient 设 `DefaultRequestHeaders = { { "Connection", "close" } }`，每条推理请求都新建到 `localhost:backendPort` 的 TCP 连接。注释解释了原因（休眠/唤醒后旧连接残留），但 localhost 握手虽便宜，高频请求下仍累积。

**建议**：改为默认 keep-alive + 设置 `PooledConnectionLifetime`（如 30s）+ `PooledConnectionIdleTimeout`。休眠时 `_hc` 仍存活但连接自然过期；唤醒后首次请求若遇到死连接，已有 500ms 重试兜底（L790）。收益小但零风险。

---

## 三、较低影响（UI / 数据结构打磨）

### E-8 SSE 字节缓冲用 `List<byte>` + `RemoveRange` + 逐行 `new byte[]`

`OutputContinuer.cs:185/196/477`：SSE 解析用 `List<byte> pending`，每处理完一批行做 `pending.RemoveRange(0, lineStart)`（O(n) 数组搬移），`DecodeLine` 每行 `new byte[end-start]` 分配。长流式输出（数千 SSE 事件）下产生大量小对象分配 + 反复数组搬移。

**建议**：改用 `MemoryStream` + 读写指针（或 `ArrayPool<byte>` + 环形缓冲），避免 `RemoveRange` 整体搬移和逐行 `new byte[]`。已有单遍扫描优化（注释提到从 O(n²) 改善），但数据结构本身仍可优化。

### E-9 `OnLogFlush` 批量写入实际是 N 次独立 `AppendText`

`MainForm.cs:2163~2166`：注释写"一次批量 AppendText"，实际是 `foreach (var (_, entry) in batch) _txtLog.AppendText(entry)`——N 次独立追加，每次可能触发布局。随后又 N 次 `SelectionStart/Length/Color` 着色。

**建议**：先 `string.Concat` 全部 entry 一次 `AppendText`，再统一着色。着色也可考虑合并连续同色行区间，减少 Selection 操作次数。

### E-10 `UpdateSummary` 每次请求全量重算 + `FindStatRow` 线性扫描

`MainForm.cs:2035`：每次 `OnRoundUpdated` 调 `UpdateSummary` → `GetRounds()`（锁内复制全表）→ `Sum` 遍历全部 50 轮。`FindStatRow`（L2026）对网格行做 O(n) 线性扫描。stats 表已修了 `_slotMgmtRowIdx` 字典模式但 stats 表未跟进。

**建议**：维护 `Dictionary<long, DataGridViewRow>` 索引（同 slot mgmt 已有模式）；`UpdateSummary` 改为维护增量计数器（累加 prompt/eval tokens），更新时累加而非全量 Sum。50 行规模下收益有限，但推理密集时每秒多次触发。

---

## 四、已做得好的地方（确认无需改）

为避免你重复优化，以下已是高效设计：

- **日志 UI 防抖**：150ms 批量消费队列，减少 RichTextBox 重绘 ✓
- **`force_stream: true`** 已启用，避免非流式超时→断开→全量重填循环 ✓
- **KV 缓存量化** `--cache-type-k/v q4_0` + `--flash-attn on` + `--spec-type draft-mtp` 投机解码，吞吐参数已调优 ✓
- **nvidia-smi 路径 `Lazy` 缓存** + 3 秒超时 + `WaitForExit` 回收，防句柄泄漏 ✓
- **闲置休眠**释放显存 + 唤醒 restore，零显存待机 ✓
- **`ParseAutoPreemptivePrefixes`** 每请求调用 `Split`——可缓存，但 1 行字符串 split 开销极小 ✓（低优先）
- **崩溃恢复 keep-alive + writeGate 互斥**，防 SSE 行交错 ✓

---

## 优化优先级路线图（建议执行顺序）

| 批次        | 项                                        | 预期收益                                     | 风险                      |
| ----------- | ----------------------------------------- | -------------------------------------------- | ------------------------- |
| **第 1 批** | E-1 合并 JSON 解析 + E-3 消除 string 往返 | 热路径 CPU ↓70%+，最大单项收益               | 中（需重构 5 个方法签名） |
| **第 2 批** | E-2 TokenGuard 二分/增量 tokenize         | 超长请求首 token 延迟 ↓（最坏 10+ 往返→2~3） | 低                        |
| **第 3 批** | E-5 排队移出锁                            | 多 agent 并发不再被单请求卡 30s              | 中（并发语义需仔细验证）  |
| **第 4 批** | E-4 前缀哈希采样化 + E-6 日志批量写       | 大上下文每请求省数 MB 序列化；推理期 I/O ↓   | 低                        |
| **第 5 批** | E-7~E-10 数据结构/UI 打磨                 | 长流式 + 密集 stats 场景边际收益             | 低                        |

---

**总结**：这个项目工程化程度已经相当高——防御性编程、熔断、KV 复用闭环、审计修复都到位。如果只动一处，优先做 **E-1（合并 JSON 解析）**：它覆盖每个推理请求、对大上下文收益最显著，且 E-3（string 往返）天然随之消除。E-2（TokenGuard 往返）和 E-5（锁内 Sleep）是另外两个有明显体感收益的点。
