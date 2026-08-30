# LlamaHarness 效率优化实现计划（E-1~E-10）

> 依据：`docs/superpowers/specs/2026-08-30-llama-harness-efficiency-optimization-design.md`
> 原则：每批次独立可编译、可测试、可回退（独立 commit）。

## 阶段 0 —— 测试工程脚手架

**新建 `LlamaHarness.Tests/`**（xunit，引用主项目）：

- `LlamaHarness.Tests.csproj`：net8.0-windows（与主项目同 TFM）、`Microsoft.NET.Test.Sdk` + `xunit` + `xunit.runner.visualstudio`
- 解决方案文件加入新项目（`dotnet add LlamaHarness.sln LlamaHarness.Tests/LlamaHarness.Tests.csproj`；若无 sln 则单独 build）
- 首批测试：对现有 string 版 `InjectThinkingMode` / `InjectNSlots` / `EnsureStreamTrue` 写"行为基线"测试（锁定当前输出），批次 1 重构后这些测试改指向 DOM 版并继续通过

**验证**：`dotnet build` + `dotnet test` 全绿。

## 阶段 1 —— 批次 1：E-1/E-3 单 DOM 管道

### 1.1 TokenGuard 双入口（先行，供 SmartScheduler 引用）

文件：[TokenGuard.cs](file:///c:/project/lunch/LlamaHarness/TokenGuard.cs)

1. 新增核心方法 `GuardAsync(JsonObject root, HttpClient hc, int backendPort, int budget)`：
   - 入参即已解析的 DOM；内部逻辑与现 L54~127 相同，但不再 parse/serialize 中间态
   - 返回 `(bool Ok, JsonObject? Root, string? Note)`——裁剪发生在 root["messages"] 上，调用方直接持有同一棵树
2. 现有 string 版 `GuardAsync(hc, port, body, budget)` 改为薄包装：`JsonNode.Parse(body)?.AsObject()` → 调核心 → `root.ToJsonString()` 返回；parse 失败仍返回 `(true, body, null)` 透传（行为不变）
3. 把 `CountTokensAsync` 抽为可注入委托参数（默认走 HTTP），供单测用假计数器

### 1.2 SmartScheduler 热路径 DOM 化

文件：[SmartScheduler.cs](file:///c:/project/lunch/LlamaHarness/SmartScheduler.cs)

1. **方法签名改造**（§2.2 表格）：
   - `InjectThinkingMode(JsonObject obj, ref ThinkingLevel level, out string? effortFix)` → void
   - `DetectToolLoop(JsonObject obj)` → bool
   - `InjectNSlots(JsonObject obj, int slot)` → bool
   - `PrefixHash(JsonObject obj)` → string?（本批先保持 SHA256 口径不变，仅改入参；E-4 批次再换轻量指纹）
   - `EnsureStreamTrue(JsonObject obj)` → void；另保留 `static string? EnsureStreamTrue(string json)` 字符串降级版（root=null 时用）
2. **`PrepareGatewayAsync` 重写**（L594~697）：
   ```
   string body = UTF8.GetString(bodyBytes);
   JsonObject? root = null;
   try { root = JsonNode.Parse(body)?.AsObject(); } catch { /* 非法 JSON */ }

   if (IsChatCompletions(p) && root != null) { lock(_thinkingGate){ InjectThinkingMode(root, ...); } }
   if (aff != null && completion) { (body, bodyBytes, routedSlot, routedKey) = await ApplySlotAffinityAsync(req, aff, root, bodyBytes); }
   if (IsChatCompletions(p) && _cfg.TokenGuardEnabled) { var (ok, newRoot, note) = await TokenGuard.GuardAsync(root, ...); ... }
   bool streaming = root?["stream"]?.AsValue().GetValue<bool>() ?? Regex.IsMatch(body, @"""stream""\s*:\s*true"); // root=null 时退回正则
   if (!streaming && _cfg.ForceStream) { if (root != null) EnsureStreamTrue(root); else 字符串降级版; }
   if (routedKey != null) LogPrefixHash(routedKey, root);
   // 末端：仅当 root != null 且被改写过 → body = root.ToJsonString(); bodyBytes = UTF8.GetBytes(body)（各一次）
   ```
3. **`ApplySlotAffinityAsync`**（L701~809）签名改为接收 `JsonObject? root`，内部 `DetectToolLoop(root)`、`InjectNSlots(root, slot)`；root=null 时跳过 DOM 步骤（与现状各方法 try-catch 返回 null 等价）
4. **streaming 判定**：DOM 可用时读 `root["stream"]`，替代对数 MB body 的正则扫描
5. 删除热路径中所有中间 `UTF8.GetBytes/GetString` 往返（现 L603/629/661/677/806）

### 1.3 单测

- `InjectThinkingModeTests`：四档指令识别 + 剥离、无指令时 Off 态注入 enable_thinking=false、chat_template_kwargs 清洗、数组型 content 跳过
- `InjectNSlotsTests`：已有 n_slots 不覆盖、无则注入
- `EnsureStreamTrueTests`：DOM 版 stream=false→true；字符串降级版三种形态（false 替换 / 无字段注入 / 非法 JSON 返回 null）
- `DetectToolLoopTests`：末条 role=tool → true；历史含 tool 但末条非 tool → false
- `TokenGuardDomTests`：假计数器验证"预算内不动 DOM"、"裁剪后 root 被修改"

**验证**：build + test 全绿 → commit "批次1：E-1/E-3 单 DOM 管道"。

## 阶段 2 —— 批次 2：E-2 TokenGuard 二分收敛

文件：[TokenGuard.cs](file:///c:/project/lunch/LlamaHarness/TokenGuard.cs)（核心方法内）

1. **轮次裁剪**（现 L86~96）：
   - 先按轮预计算各轮 token 数不可行（tokenize 是全文口径），改为对"删除前 K 轮"二分：`lo=1, hi=turnStarts.Count-1`，每步批量删除 mid 轮 → tokenize 一次 → 依 count 与 budget 关系收缩区间
   - 收敛后若仍超预算，进入内容兜底
2. **内容兜底**（现 L99~117）：对保留比例 r ∈ [0.1, 1.0] 二分 ≤4 次；每次按当前最大消息的 `content.Length * r` 截断（头尾双保留逻辑不变）
3. 失败降级路径全部保留（tokenize 返回 null → 用当前状态透传）

### 单测

- 假计数器（count = f(删除轮数)）验证：轮数 K=8、budget 需删 5 轮 → tokenize 调用次数 ≤ 6 且最终 count ≤ budget
- 内容兜底：单条巨型消息 → 迭代 ≤ 4 次收敛
- 中途 tokenize 失败 → 返回 (true, 当前 root, null)

**验证**：build + test 全绿 → commit "批次2：E-2 TokenGuard 二分收敛"。

## 阶段 3 —— 批次 3：E-5 排队移出锁

文件：[SlotAffinity.cs](file:///c:/project/lunch/LlamaHarness/SlotAffinity.cs)（GetSlot L89~156）

1. 拆两阶段：
   - **阶段 1（lock(_gate)）**：key 解析、已有绑定刷新、新 key 找空闲槽 / LRU 驱逐；若"全槽强占满"→ 记录需等待，**立即出锁**
   - **阶段 2（锁外循环）**：`while (sw.Elapsed < MaxWaitSeconds) { Thread.Sleep(1000); lock(_gate){ slot = FindFreeSlotLocked(); if (slot >= 0) break; /* 检查可驱逐绑定 */ } }`
   - 超时降级随机槽逻辑不变
2. `FindFreeSlotLocked` 保持 private + 仅在持锁时调用（命名已体现）

### 单测

- 构造全槽强占场景 → GetSlot 进入排队；并发线程调 `IsPreemptive` / `SetPreemptive` / `Snapshot`，断言在 <1s 内完成（旧实现会被 Sleep-in-lock 卡住）
- 排队期间释放一个槽 → GetSlot 在 ≤2s 内拿到该槽
- 30s 超时路径用缩短 MaxWaitSeconds 的测试构造验证降级随机槽

**验证**：build + test 全绿 → commit "批次3：E-5 排队移出锁"。

## 阶段 4 —— 批次 4：E-4 轻量指纹 + E-6 日志批量写

### 4.1 E-4 前缀指纹

文件：[SmartScheduler.cs](file:///c:/project/lunch/LlamaHarness/SmartScheduler.cs)（PrefixHash L1001~1020）

1. `PrefixHash(JsonObject obj)` 改为：遍历 messages，拼接 `{count}:{role}|{content长度}` 序列（如 `12:user|1834,assistant|92,...`），直接返回该字符串作指纹
2. 无 SHA256、无 ToJsonString；注释说明口径变更（仅用于 HIT/MISS 日志判定，碰撞可接受）
3. `LogPrefixHash` 逻辑不变（_prefixHashes 字典比对）

### 4.2 E-6 LogFile 常驻 StreamWriter

文件：[LogFile.cs](file:///c:/project/lunch/LlamaHarness/LogFile.cs)

1. 新增 `static class LogStreamWriter`（内部）：持有 `StreamWriter`（`new FileStream(path, FileMode.Append)` + `new StreamWriter(fs, UTF8, 4096)`）、当前文件大小计数
2. `AppendMain` / `SlotAppend` / `AppendWarnError` 改为写流（不再 File.AppendAllText）
3. 轮切判定移到写前：`if (size + lineLen > maxBytes) { Close(); Rotate(); Reopen(); }`
4. 150ms `System.Threading.Timer` 周期 Flush（与 UI 防抖同节奏）；进程退出时 `Dispose`（在 MainForm 关闭路径或 static finalizer 兜底——优先显式：SmartScheduler.Dispose 链上调用 `LogFile.Shutdown()`）
5. 异常仍尽力而为不抛出

### 单测

- 轻量指纹：相同 messages → 相同指纹；改一条 content → 指纹变化；无 messages → null
- LogStreamWriter：写入后 Flush，文件内容正确；超限时轮切为 .1 且新文件可写

**验证**：build + test 全绿 → commit "批次4：E-4 轻量指纹 + E-6 日志批量写"。

## 阶段 5 —— 批次 5：E-7~E-10 打磨

### 5.1 E-7 HttpClient keep-alive

文件：[SmartScheduler.cs](file:///c:/project/lunch/LlamaHarness/SmartScheduler.cs)（L25~29）

```csharp
private readonly HttpClient _hc = new(new HttpClientHandler
{
    PooledConnectionLifetime = TimeSpan.FromSeconds(30),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
})
{
    Timeout = System.Threading.Timeout.InfiniteTimeSpan,
};
```
删除 `DefaultRequestHeaders = { { "Connection", "close" } }`；更新注释（死连接由 500ms 重试兜底，L823~828）。

### 5.2 E-8 SSE 缓冲

文件：[OutputContinuer.cs](file:///c:/project/lunch/LlamaHarness/OutputContinuer.cs)（L170~202）

1. `List<byte> pending` → `byte[] buf`（初始 64KB，扩容翻倍）+ `int lineStart`（已处理游标）+ `int len`
2. 扫描逻辑不变（从 scanFrom 找 \n），但"移除已处理"改为只前移 lineStart；当 `lineStart > 65536` 时 `Array.Copy(buf, lineStart, buf, 0, len-lineStart)` 压实、len/scanFrom 相应调整
3. `DecodeLine` 改为读 `buf[lineStart..i]`（不再 new byte[] 逐行——用 Encoding.UTF8.GetString(buf, lineStart, i-lineStart)）

### 5.3 E-9 OnLogFlush 单次 AppendText

文件：[MainForm.cs](file:///c:/project/lunch/MainForm.cs)（L2154~2204）

1. batch 先 `string.Concat(batch.Select(b => b.entry))` 一次 `_txtLog.AppendText(all)`
2. 着色保持逐行 Selection（RichTextBox 无法整段多色），但起点计算简化为顺序累加；字符上限截断逻辑不变

### 5.4 E-10 stats 表索引 + 增量汇总

文件：[MainForm.cs](file:///c:/project/lunch/MainForm.cs)

1. 新增 `Dictionary<long, DataGridViewRow> _statsRowIdx`（对齐 L178 `_slotMgmtRowIdx` 模式）：
   - `OnRoundUpdated` 建行时登记；`OnRoundRemoved` / `OnSessionReset` 时移除/清空
   - `FindStatRow` 改为字典查找，删除线性扫描
2. `UpdateSummary` 改增量计数器：字段 `_sumPromptTok/_sumEvalTok/_sumPromptMs/_sumEvalMs/_sumDraftAcc/_sumDraftGen/_reqCount`
   - `OnRoundUpdated` 新行时累加；`OnRoundRemoved` 时减去该行值；`OnSessionReset` 清零
   - `UpdateSummary` 只读计数器拼字符串，不再调 `GetRounds()` + Sum

### 单测 / 验证

- E-8：SSE 分块喂入（跨 chunk 断行、超长流 >64KB）→ 输出行与旧实现一致
- E-10：UI 逻辑难单测，靠 build + 手动冒烟观察 stats 表刷新

**验证**：build + test 全绿 → commit "批次5：E-7~E-10 打磨"。

## 阶段 6 —— 收尾

1. 全量 `dotnet build` + `dotnet test`
2. 手动冒烟清单（交用户执行）：
   - 发典型 chat/completions 请求（含思考指令、tool 消息、非流式）→ 对比改写结果与现行为一致
   - 多 agent 并发（≥3 个 key）→ 观察槽位路由日志无 30s 卡顿
   - 超长上下文触发 TokenGuard → 观察 tokenize 次数日志（应 ≤6）
   - 长流式输出 → 观察内存/GC 无异常
3. 更新 `docs/LlamaHarness 效率优化建议报告.md` 状态标记（可选，由用户决定）

## 回退策略

每批次独立 commit；若某批次冒烟失败，`git revert <batch-commit>` 单独回退，不影响其他批次。
