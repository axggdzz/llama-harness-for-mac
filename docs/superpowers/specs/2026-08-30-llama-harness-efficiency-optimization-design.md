# LlamaHarness 效率优化设计（E-1~E-10 全批次落地）

> 日期：2026-08-30
> 依据：`docs/LlamaHarness 效率优化建议报告.md`（E-1~E-10）
> 范围：全部 5 个批次；验证方式 = 新建 xunit 轻量单测工程 + 编译 + 手动冒烟
> 方案选型：**方案 A —— 单 DOM 管道**（`PrepareGatewayAsync` 入口一次 `JsonNode.Parse`，全程复用同一棵 `JsonObject`，末端单次序列化）

## 1. 背景与目标

一条 chat/completions 请求当前在 `SmartScheduler.PrepareGatewayAsync` 管道中触发 5~6 次完整 JSON DOM 解析 + 3~4 次序列化，外加多次 string↔bytes 往返转换。对数 MB 级请求体（ctx≈222822），这是最大的 CPU 效率瓶颈。

目标：

1. 热路径 JSON CPU ↓70%+（E-1/E-3）
2. TokenGuard 最坏 tokenize 往返 10+ 次 → ≤6 次（E-2）
3. 槽位排队不再阻塞全部路由最长 30s（E-5）
4. 前缀指纹零序列化化（E-4）；日志 I/O 系统调用降一个数量级（E-6）
5. SSE 缓冲 / UI 批量写 / stats 索引打磨（E-7~E-10）

**非目标**：不改动崩溃恢复、KV Cache、闲置休眠等既有行为语义；不引入 GatewayRequest 等新抽象层（方案 C 已否决）。

## 2. 批次 1 —— E-1/E-3：单 DOM 管道（核心重构）

### 2.1 数据流改造（`PrepareGatewayAsync`，SmartScheduler.cs L594~697）

```
入口 bodyBytes
  → 一次 Encoding.UTF8.GetString + JsonNode.Parse → JsonObject? root
  → InjectThinkingMode(root, ...)        // 树上直接改，不再 ToJsonString
  → ApplySlotAffinityAsync(root, ...)    // GetSlot + DetectToolLoop(root) + InjectNSlots(root)
  → TokenGuard.GuardAsync(root, ...)     // 裁剪 root["messages"]
  → streaming = root["stream"]?.GetValue<bool>()   // 替代正则扫描
  → EnsureStreamTrue(root)               // 直接 obj["stream"]=true
  → LogPrefixHash(root)                 // 轻量指纹（见 §5）
  → 末端一次 root.ToJsonString() + 一次 GetBytes() → bodyBytes/finalBody
```

### 2.2 方法签名变化（仅 SmartScheduler 内部调用，无外部影响）

| 方法 | 现在 | 改后 |
|---|---|---|
| `InjectThinkingMode` | `(string, ref level, out fix) → string?` | `(JsonObject, ref level, out fix) → void`（树上改） |
| `DetectToolLoop` | `(string) → bool` | `(JsonObject) → bool` |
| `InjectNSlots` | `(string, int) → string?` | `(JsonObject, int) → bool`（返回是否注入） |
| `PrefixHash` | `(string) → SHA256 hex` | `(JsonObject) → string?` 轻量指纹 |
| `EnsureStreamTrue` | `(string) → string?` | `(JsonObject) → void` + 保留字符串降级版供 root=null 时用 |

### 2.3 错误处理（行为等价）

入口解析失败 → `root = null` → 跳过全部 DOM 改写，原样透传 bodyBytes；仅当 `ForceStream` 且 root=null 时走现有字符串级降级（C-005 语义不变）。与现状"每个方法各自 try-catch 透传"的最终结果完全一致。

### 2.4 TokenGuard 双入口

新增 `GuardAsync(JsonObject root, ...)` 核心实现（热路径用）；现有 string 版本改为薄包装（parse 一次 → 调核心），保住崩溃恢复/续接等 5 个非热路径调用点不动：

- SmartScheduler.cs L1143 / L1163 / L1247（崩溃恢复、续接重放）
- OutputContinuer.cs L97 / L372（续接请求体守卫）

## 3. 批次 2 —— E-2：TokenGuard 二分收敛

`TokenGuard.GuardAsync`（TokenGuard.cs L86~117）两处线性重试改二分：

- **轮次裁剪**：删除"删一轮→全量 tokenize"循环。改为对"删除前 K 轮"做二分（K 单调递减可二分）：每次批量删除 mid 轮后 tokenize 一次，≤ log₂(轮数)+1 ≈ 5~6 次收敛，替代最坏 K+1 次。
- **内容兜底**：截断比例从固定 ratio 迭代 10 次 → 对保留比例 [0.1, 1.0] 二分，≤ 4 次收敛。
- 最坏 tokenize 往返：**10+ 次 → ≤6 次**；tokenize 失败降级语义不变（中途失败用当前状态透传）。

## 4. 批次 3 —— E-5：排队移出锁

`SlotAffinity.GetSlot`（SlotAffinity.cs L89~156）拆两阶段：

- **阶段 1（锁内）**：绑定查找 / LRU 驱逐判定 / 发现"全槽强占满"→ 立即出锁。
- **阶段 2（锁外）**：`while (elapsed < MaxWaitSeconds) { Thread.Sleep(1000); lock(_gate){ slot = FindFreeSlotLocked(); ... } }`——Sleep 不再持锁，其他请求的 GetSlot/SetPreemptive/Snapshot 全程不阻塞。
- 语义不变：仍是最多等 30s、超时降级随机槽；只是等待期间不再卡死整个槽位系统。

## 5. 批次 4 —— E-4 轻量指纹 + E-6 日志批量写

### 5.1 E-4 前缀指纹

`PrefixHash(JsonObject)` 改为零序列化指纹：`消息条数 + 各条 role|content长度` 拼接（如 `12:user|1834,assistant|92,...`）。无 SHA256、无 ToJsonString，开销从数 MB 降到微秒级。HIT/MISS 可观测性保留；碰撞概率对"仅用于日志判定"场景可接受（注释中说明口径变更）。

### 5.2 E-6 LogFile 常驻 StreamWriter

LogFile.cs 三个日志文件（harness/slot/warn_error）各持一个常驻 `StreamWriter`（`FileOptions.Append` + 缓冲），写入只 Append 到流；150ms 定时器 Flush（与 UI 防抖同节奏）；轮切时 close→rename→reopen。现有 `_gate` 锁保护，异常仍尽力而为不阻断主流程。

## 6. 批次 5 —— E-7~E-10 打磨

| 项 | 改动 |
|---|---|
| E-7 | `_hc` 去掉 `Connection: close` 默认头，改用 `HttpClientHandler { PooledConnectionLifetime = 30s, PooledConnectionIdleTimeout = 60s }`；保留现有 500ms 重试兜底死连接 |
| E-8 | OutputContinuer.cs L170~202 SSE 缓冲：`List<byte>` + RemoveRange → `byte[]` + 已处理偏移指针（只读游标前移，不搬移字节）；当已处理量超阈值（64KB）时执行一次 `Array.Copy` 把未处理尾部压实到数组头部、偏移归零，消除 O(n) 逐批搬移 |
| E-9 | MainForm.cs L2163~2166 `OnLogFlush`：batch 先 `string.Concat` 一次 AppendText，再单次遍历着色（替代 N 次 AppendText + N 次 Selection） |
| E-10 | stats 表加 `Dictionary<long, DataGridViewRow>` 索引（对齐 slot mgmt 已有模式）；`UpdateSummary` 改增量计数器（OnRoundUpdated 时累加），不再每次全量 Sum |

## 7. 测试与验证

新建 **`LlamaHarness.Tests`** xunit 工程（引用主项目），覆盖：

- **纯函数单测**（无网络）：InjectThinkingMode（指令识别/剥离/清洗/四档注入）、InjectNSlots、EnsureStreamTrue（含字符串降级）、DetectToolLoop、轻量指纹稳定性
- **TokenGuard 裁剪逻辑**：把 tokenize 抽为可注入的 `Func<string, Task<int?>>`，用假计数器单测二分收敛（轮次删除数、截断比例、失败降级）
- **SlotAffinity 并发**：一个 GetSlot 进入排队时，断言 IsPreemptive/SetPreemptive 在毫秒级完成（不再被 Sleep 卡住）
- **每批次**：`dotnet build` 通过 + 跑测试；全部完成后手动冒烟（发典型 chat/completions 请求验证改写结果与现行为一致）

## 8. 实施顺序

按报告路线图分 5 批提交，每批独立可编译、可测试、可回退：

1. **批次 1**：E-1/E-3 单 DOM 管道（SmartScheduler + TokenGuard 双入口）
2. **批次 2**：E-2 TokenGuard 二分收敛
3. **批次 3**：E-5 排队移出锁
4. **批次 4**：E-4 轻量指纹 + E-6 日志批量写
5. **批次 5**：E-7~E-10 数据结构/UI 打磨

## 9. 风险与回退

| 批次 | 风险 | 缓解 |
|---|---|---|
| 1 | 中：5 个方法签名变化 + TokenGuard DOM 化 | xunit 覆盖每个改写函数输入→输出；行为语义不变 |
| 2 | 低：二分边界条件 | 假计数器单测收敛性；失败降级路径保留 |
| 3 | 中：并发语义 | 单测断言排队期间其他操作不被阻塞；超时降级路径保留 |
| 4 | 低：指纹碰撞 / StreamWriter 生命周期 | 仅用于日志判定，碰撞可接受；轮切 close→reopen 防句柄泄漏 |
| 5 | 低：UI 线程模型 | 保持现有 UI 线程调用约定不变 |

每批独立 commit，可单独 revert。
