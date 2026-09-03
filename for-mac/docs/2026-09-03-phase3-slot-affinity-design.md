# 第三阶段 A：macOS SlotAffinity 设计

## 目标

在不修改根目录 Windows C# 基线的前提下，为 Rust/macOS 网关加入多槽亲和调度的纯内存与持久化核心。该阶段只负责请求到 slot 的稳定分配和绑定生命周期；KV save/restore/erase 留给第三阶段 B，通过明确的驱逐事件接口接入。

## 范围与不变量

- `slot_count` 至少为 1；slot 编号固定为 `0..slot_count`。
- 已识别请求按亲和 key 固定到同一 slot，并刷新 `last_active`。
- 未识别请求返回随机 slot，不创建持久绑定。
- 新 key 优先使用空闲 slot；无空闲 slot 时驱逐最久未活跃的非强占绑定。
- 强占绑定不可被普通新 key 驱逐；强占数量最多为 `slot_count - 1`，为普通请求保留至少一个槽。
- Tool 锁定只影响驱逐优先级，不改变绑定本身；锁定 key 被驱逐时先于普通强占 key，驱逐后自动解除锁定。
- 所有等待必须在锁外进行，不能阻塞其他请求的快照或强占设置。
- 持久化失败不阻断当前路由；损坏文件、越界 slot 和无效记录按降级策略跳过。

## 模块与接口

新增 `for-mac/src/slot_affinity.rs`，公开以下类型：

```rust
pub struct SlotAffinity {
    // 内部使用 std::sync::Mutex，所有方法保持短临界区
}

pub struct SlotBinding {
    pub key: String,
    pub app: String,
    pub slot: usize,
    pub last_active: SystemTime,
    pub preemptive: bool,
    pub kv_cache: bool,
}

pub struct SlotAllocation {
    pub slot: usize,
    pub key: Option<String>,
    pub new_binding: bool,
    pub evicted: Option<SlotBinding>,
}

impl SlotAffinity {
    pub fn new(slot_count: usize, path: impl Into<PathBuf>) -> Self;
    pub fn affinity_key(headers: &HeaderMap) -> Option<String>;
    pub fn allocate(&self, headers: &HeaderMap, auto_preemptive: &[String]) -> SlotAllocation;
    pub fn set_preemptive(&self, key: &str, value: bool);
    pub fn is_preemptive(&self, key: &str) -> bool;
    pub fn mark_tool_locked(&self, key: &str);
    pub fn unmark_tool_locked(&self, key: &str);
    pub fn set_kv_cache(&self, key: &str, value: bool);
    pub fn snapshot(&self) -> Vec<SlotBinding>;
    pub fn enforce_preemptive_cap(&self) -> Vec<String>;
}
```

`allocate` 在所有槽均为强占且新请求无法安全分配时返回确定性的降级结果：等待上限内轮询一次状态，超时返回随机 slot 且 `key=None`。等待实现使用 `Condvar` 或 Tokio `Notify` 的等价短等待，不在互斥锁内 sleep。

## 请求头识别

识别优先级与 Windows 基线一致：

1. `x-deepseek-harness-user-id` → `dsh_rule_<value>`
2. `x-conversation-id` → `webui_<value>`
3. `x-model-provider: custom_openai_compatible` → `trae_global`
4. `user-agent` 含 `deepseek-harness` 且存在任一 `x-stainless-*` → `dsh_agent_global`

匹配不区分大小写；空值和未知请求不建立绑定。应用显示名由 key 前缀派生，避免在网关中复制业务判断。

## 持久化

默认文件位于 `ProjectDirs::from("com", "axggdzz", "LlamaHarness")` 返回的数据目录下的 `slot_bindings.json`，允许测试传入临时路径。写入流程为：创建父目录 → 写入同目录临时文件 → `rename` 替换目标文件。读取失败时从空表启动，不删除原文件，便于诊断。

JSON 结构保持跨版本宽容：

```json
{
  "version": 1,
  "slot_count": 2,
  "bindings": {
    "trae_global": {
      "slot": 0,
      "last_active": "2026-09-03T00:00:00Z",
      "preemptive": true,
      "kv_cache": true
    }
  }
}
```

加载时忽略缺失 key、无效时间、重复 slot 和超出当前 `slot_count` 的记录；旧字段缺省为 `preemptive=false`、`kv_cache=true`。

## 网关接入

第三阶段 A 的网关改动保持最小：

- `AppConfig` 增加 `slot_count`、`slot_bindings_path` 和 `auto_preemptive_prefixes`，默认 `slot_count=1` 时行为等同现有单槽网关。
- `Gateway` 持有 `Option<Arc<SlotAffinity>>`，在 `/v1/*` 读取请求头并分配 slot。
- 转发 JSON body 时仅当请求体是 JSON object 且没有 `n_slots` 时注入 `n_slots=<slot>`；已有字段保持调用方语义。
- `/__status__` 增加绑定快照和最近一次分配/驱逐信息，方便后续 UI 消费。
- 本阶段不调用后端 `/slots`，也不保存 KV；`SlotAllocation.evicted` 作为第三阶段 B 的唯一接入点。

## 错误与并发

- 绑定表由单个 Mutex 保护；文件 IO 在复制快照后于锁外执行，避免磁盘阻塞请求。
- `set_preemptive` 不能突破 `slot_count - 1` 上限；若设置会导致超额，调用 `enforce_preemptive_cap` 按 Tool 锁定优先、再按最早活跃时间降级。
- 并发首次请求同一 key 必须最终共享同一 slot，不能产生两个绑定。
- 持久化异常只记录 tracing warning，内存状态继续有效。

## 测试验收

纯单元测试覆盖：

- 四类请求头 key 识别与未知请求随机降级；
- 已绑定 key 复用 slot 并刷新活跃时间；
- 空闲槽优先、非强占 LRU 驱逐；
- 强占保护、强占上限和 Tool 锁定驱逐优先级；
- 损坏/越界/重复 slot 文件加载降级；
- 原子写入后新实例恢复绑定；
- 并发分配同一 key 的单绑定不变量。

网关 E2E 测试验证：请求头触发固定 `n_slots`、未知请求不写入绑定、`/v1` 请求在无后端时仍沿用第二阶段唤醒流程。

## 后续阶段边界

第三阶段 B 仅消费 `SlotAllocation.evicted` 与 `SlotBinding.kv_cache`，实现 `/slots/{id}?action=save|restore|erase`、快照索引和元数据校验；不改变本阶段的 key 识别和分配算法。

