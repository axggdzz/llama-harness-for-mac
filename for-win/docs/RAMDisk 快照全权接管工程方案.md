# llama‑harness 关闭 llama.cpp Prompt‑Cache，RAMDisk 快照全权接管工程方案

> 版本：V1.0｜适用硬件：64G Host 内存 + RTX3080‑20G｜适配 llama.cpp 新版（存在`--cache‑ram`/`--cache‑idle‑slots`参数） 目标：彻底消除 `server_prompt_cache::alloc()` LRU 驱逐导致的虚假 KV‑MISS、不必要全量 Prefill；废弃 llama.cpp 内置空闲会话 checkpoint 机制；会话 KV 缓存的保存、卸载、恢复全部由 Harness + RAMDisk 全权管控。 前置约束：关闭内置 prompt‑cache 后，release 释放 slot 后，slot 内 KV 会被新任务直接覆盖，**无系统内存自动备份，RAMDisk 快照成为会话唯一可信数据源**。

## 1 变更概述

### 1.1 现状问题

1. 默认`--cache‑ram 8192`，空闲 slot 的 KV Checkpoint 自动存入主机内存，单 Agent 工具链会话 KV 可达 4‑5GiB，2 个条目即可打满 8GiB 上限，触发 LRU 驱逐。
2. 驱逐不会报错，仅输出日志`making room for prompt cache entry, removing oldest entry`，上层表现为`[KV‑MISS]前缀变更`，触发代价极高的全量 prefill。
3. 旧架构存在双重兜底：llama.cpp 内存 prompt‑cache + RAMDisk 快照。Harness 侧驱逐前/休眠前/首请求存档的 save **已是同步 await**（非"release 后后台异步"），但存在两个真实缺口：① **非首次轮次无增量快照**——进程崩溃时会话回退到首请求状态；② save 成功后**无落盘校验**，损坏快照会在 restore 时才暴露。

### 1.2 改造后设计理念

- llama.cpp 仅负责**当前正在运行任务**的活跃 KV 缓存（显存 VRAM）。
- 会话冷热卸载、持久化、恢复全部交给 Harness，RAMDisk 作为唯一持久存储介质。
- 消除 llama.cpp 内置 prompt‑cache 带来的不可控 LRU 驱逐，业务行为 100% 上层可观测可控。

### 1.3 边界风险说明

> 关闭`--cache‑ram`之后，不再存在系统内存备份安全垫。 ⚠️**slot 必须完成快照落盘校验成功，才允许 release 归还 slot 池；快照失败禁止释放 slot，否则会话 KV 直接永久丢失。**

## 2 llama.cpp 启动参数（完整生产参数）

```
./server 
--ngl 99
--ctx-size 131072
--parallel 1
--cache-ram 0
--no-cache-idle-slots
--host 127.0.0.1
--port 8080
```

参数释义：

表格

|参数|说明|
|---|---|
|`--cache‑ram 0`|完全关闭主机内存 Prompt‑Cache，不存储任何空闲 slot checkpoint，关闭内部 LRU 驱逐逻辑|
|`--no‑cache‑idle‑slots`|禁止任务 release 后自动把空闲 slot 状态存入 prompt cache，与`--cache‑ram 0`配套|
|`--parallel 1`|当前业务以单长会话 Agent 为主；后续扩展多并发，该数值上调，本套快照逻辑不变|

> 注意：该配置**不影响显存上活跃推理 KV Cache**，仅关闭主机侧的空闲会话备份。

## 3 Harness 核心逻辑改造点

### 3.1 Slot 生命周期时序改造【最高优先级，必须落地】

> **实施决策（评估确认）**：llama.cpp **无 slot 锁 API**——release 由 llama-server 内部在任务完成后自动执行，Harness 无法延迟或禁止。因此"锁定 slot → save → 解锁 release"的串行时序**只能落在网关层**，且采用**条件式 save**（而非每轮无条件同步 save，后者大会话每轮固定 +5~6s 延迟）：
>
> - **保留现有事件驱动同步 save**（已是 await 语义）：驱逐前 save / 休眠前 save / 首请求存档；
> - **新增每轮条件式后台 save**：任务完成后，快照非新鲜（上一轮后 KV 有增量）→ `Task.Run` 异步后台 save，**不阻塞响应返回**（零额外延迟）；成功 → 标记新鲜；失败 → `[EDGE-CASE-SAVE-FAILED]` + DeleteCache 废弃旧快照（下轮自动重试）；
> - **save 成功后强制落盘校验**：文件大小 > 0、saved_n > 0，不通过即判定失败；
> - 并发安全：KvCacheManager 按 key 在途去重（_inflightSaves），同步/后台 save 共享同一在途任务。

#### ❌旧时序（废弃，存在高危窗口）

```
任务执行结束
→ slot release 归还llama.cpp slot池
→ 非首次轮次无增量快照（崩溃回退首请求状态）+ save 无落盘校验
风险：损坏/过期快照在 restore 时才暴露；崩溃丢失全部增量 KV。
```

#### ✅新时序（改造后，唯一正确流程）

预览

查看代码

```
flowchart LR
A[Agent任务执行完毕] --> B[锁定当前slot，禁止归还池]
B --> C[调用slot‑save，Dump KV缓存至RAMDisk]
C --> D{快照调用返回成功?}
D --失败--> E[埋点EDGE‑CASE‑SAVE‑FAILED；保留slot锁定；上报异常指标；可配置重试/丢弃会话]
D --成功--> F[校验快照元数据：文件大小、saved_n_tokens]
F --校验不通过 --> E
F --校验通过 --> G[写入metrics日志：会话ID、saved_n_tokens、快照路径、时间戳]
G --> H[slot‑unlock，执行slot release，归还slot共享池]
```

豆包

你的 AI 助手，助力每日工作学习

> 介质为 RAMDisk（内存虚拟盘），IO 延迟极低，快照等待对业务几乎无感知。 快照失败严禁 release，防止 KV 丢失。

### 3.2 会话恢复 slot‑load 链路强化

会话切换，需要恢复历史会话快照：

1. Harness 查询 RAMDisk，读取目标会话快照文件与元数据；
2. 文件完整性校验：快照文件存在、大小合法；损坏 / 缺失直接降级，走全量 prefill 兜底，输出埋点`[EDGE‑CASE‑SNAPSHOT‑CORRUPT]`；
3. 调用`slot‑load`加载快照到 slot；
4. **加载完成后强制执行完整 TokenGuard.GuardAsync 校验**（强制路径，不可跳过）；
5. TokenGuard 校验不通过：执行激进裁剪，丢弃部分历史消息；极端情况废弃当前快照，走全新全量 prefill；
6. 校验通过，才允许提交新 prompt 给 llama.cpp 推理。

> 关键：关闭 cache‑ram 后，没有内置缓存兜底，快照加载后的边界检查不可省略，规避快照本身逼近上下文上限直接触发 400 报错。

### 3.3 TokenGuard 配套改造（同步落地）

1. 新增可配置参数 `ReservedPromptOverhead = 10240`（UI 可调整，默认 10240 tokens），专门承接`system + tools schema + jinja模板`隐形 token 开销。
2. 预算计算公式更新：

```
//旧
var budget = CtxSize - ReservedOutputTokens;
//新
var budget = CtxSize - ReservedOutputTokens - ReservedPromptOverhead;
```

3. **无论是否发生裁剪，每次执行 TokenGuard 强制输出计量日志到 metrics** 输出字段：`budget、msg_est_token、reserved_out、reserved_overhead`。
4. ~~JINJA 渲染完成后二次兜底 tokenize 校验~~ **（评估移除）**：llama.cpp 无端点可获取 Jinja 渲染后的完整 prompt，短期无法实现；由第 5 条 400 自愈分支兜底（效果等价）。
5. 捕获 HTTP 400 上下文超限，增加自愈分支：激进裁剪、废弃 slot 内存 KV、可回退磁盘快照重跑；输出独立边界事件埋点 `[EDGE-CASE-CONTEXT-OVERFLOW-400]`。

### 3.4 快照元数据体系新增

每个 slot‑save 成功，在 RAMDisk 快照同目录写入简单元数据 json，示例：

```
{
  "session_id": "trae_global",
  "saved_n_tokens": 130609,
  "save_timestamp": 1756432123,
  "ctx_size": 131072
}
```

用途：slot‑load 阶段做校验；metrics 统计会话占用 token；后续 Streamlit+DuckDB 做观测分析。

### 3.5 Metrics 埋点补充（用于问题回溯，后续接入 DuckDB）

新增独立事件标记，输出到`[METRICS‑XXX]`日志：

1. `[EDGE‑CASE‑SAVE‑FAILED]`：slot‑save 快照保存失败
2. `[EDGE‑CASE‑SNAPSHOT‑CORRUPT]`：快照文件损坏、元数据异常
3. `[EDGE‑CASE‑CONTEXT‑OVERFLOW‑400]`：捕获到 llama.cpp 400 上下文超限
4. `[TOKEN‑GUARD‑STAT]`：每次 guard 执行输出估值、预算、真实 prompt token 差值

> 区分两类 KV‑MISS 日志：
> 
> - - 真实业务消息前缀变更；

- 快照缺失 / 快照损坏导致的 MISS； 不再会出现 “prompt‑cache LRU 驱逐” 带来的虚假 MISS。

## 4 异常降级策略

1. **slot‑save 快照保存失败**

- 不 release slot，保留 slot 占用；记录指标告警；支持配置：重试 N 次，或者直接丢弃该会话。
- 禁止直接归还 slot，避免 KV 永久丢失。

2. **slot‑load 加载快照失败（文件损坏、元数据不匹配）**

- 不强行继续推理；直接废弃快照，走全新全量 prefill；输出埋点统计事件频次。

3. **TokenGuard 校验失败，逼近上下文窗口**

- 优先执行会话裁剪；裁剪后依然超限，废弃快照，重新构建会话。

## 5 测试回归验证清单（改造完成后必测）

### 5.1 功能测试

- 多轮 Agent 工具链任务跑完，slot‑save 成功之后才执行 release；查看时间戳日志。
- 会话切换，完全从 RAMDisk slot‑load 恢复，不再依赖 llama.cpp 内置 cache。
- 日志不再出现 `making room for prompt cache entry, removing oldest entry`。
- 快照文件损坏，触发降级，不会直接抛出异常崩溃。

### 5.2 边界压力测试

- 长会话 + 全套多工具，复现接近上下文上限场景，验证 TokenGuard 的`ReservedPromptOverhead`预留生效，不会触发 400 报错。
- 模拟 slot‑save 返回失败，确认 slot 不会 release，会话 KV 不会丢失。
- 加载高 token 快照，确认 load 之后强制走 TokenGuard 校验。

### 5.3 性能观测

- 观测 metrics：统计`prompt_overhead = real_full_prompt_token - msg_est_token`，观测 tools/system 隐形载荷的 token 波动。
- 统计全量 prefill 事件频次，对比改造前，消除因 prompt‑cache 驱逐带来的无效全量重算。

## 6 文档与约束备注（写入项目开发文档）

1. 开启`--cache‑ram 0 --no‑cache‑idle‑slots`之后，**release 之后 slot 内部 KV 没有任何自动备份**，RAMDisk 快照是会话唯一可信数据源。
2. 禁止再使用旧时序：release 之后后台异步 save 快照。
3. `ReservedPromptOverhead`不是精确计算值，是安全缓冲；新增工具会增大 tools schema 开销，需要根据 metrics 观测数值调整预留大小。
4. 后续扩展`--parallel >1`多并发场景，本套时序逻辑依然生效，需要评估 slot 池容量，快照期间 slot 短暂锁定。

## 7 备选回滚方案（改造出问题可快速切回旧模式）

> 如果新版本出现异常，可直接恢复旧启动参数，回退原有双兜底模式：

```
--cache-ram 8192
```

Harness 上层逻辑不需要改动，用于紧急回滚。

## 8 收益总结

1. 彻底根除 llama.cpp prompt‑cache LRU 驱逐带来的虚假 KV‑MISS、突发 prefill 性能跳水。
2. 会话 KV 全生命周期全部由 Harness 管控，行为可观测、可埋点、可自愈。
3. 释放大量主机内存，不再存储巨大的空闲会话 checkpoint 副本。
4. 配合 TokenGuard 修复，同时解决「隐形 tools 载荷导致 400 上下文超限」与「prompt‑cache 驱逐性能损失」两大高压边界问题。
5. 充分利用 RAMDisk 高速 IO 的优势，构建上层冷热 KV 缓存体系，最大化有限显存利用率。