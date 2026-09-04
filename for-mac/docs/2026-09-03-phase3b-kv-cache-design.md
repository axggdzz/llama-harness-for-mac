# 第三阶段 B：macOS KV 快照管理设计

## 目标

提供独立于 SlotAffinity 的 KV 快照管理器，负责调用 llama-server 的 `/slots` API、维护快照文件与索引，并在 restore 前验证文件完整性和元数据。该阶段不改变现有 slot 分配算法，也不自动把 save/restore 绑定到驱逐流程。

## 接口

新增 `KvCacheManager`，通过 `reqwest::Client` 与后端通信：

```rust
pub struct KvCacheManager { /* client, backend endpoint, cache directory, index */ }
pub struct KvSnapshot {
    pub key: String,
    pub slot: usize,
    pub saved_at: SystemTime,
    pub saved_tokens: u64,
    pub size_bytes: u64,
    pub sha256: String,
}
pub struct KvOperationResult {
    pub snapshot: Option<KvSnapshot>,
    pub response: serde_json::Value,
}

impl KvCacheManager {
    pub fn new(client: Client, backend_base_url: impl Into<String>, cache_dir: impl Into<PathBuf>, index_path: impl Into<PathBuf>, slot_count: usize, context_size: Option<u64>) -> Self;
    pub async fn save(&self, slot: usize, key: &str) -> Result<KvOperationResult>;
    pub async fn restore(&self, slot: usize, key: &str) -> Result<KvOperationResult>;
    pub async fn erase(&self, slot: usize) -> Result<serde_json::Value>;
    pub async fn clear_all(&self) -> Result<usize>;
    pub fn snapshot(&self) -> Vec<KvSnapshot>;
    pub fn has_snapshot(&self, key: &str) -> bool;
    pub fn delete_snapshot(&self, key: &str) -> Result<bool>;
}
```

## 文件与校验

- 文件名为安全化 key 的 `<key>.bin`，元数据为 `<key>.meta.json`。
- save 请求 body 为 `{"filename":"<key>.bin"}`；服务端成功后必须有非空文件和 `saved_tokens > 0`，否则本次 save 失败并删除不完整文件。
- 元数据保存 slot、saved token 数、文件大小、SHA-256、保存时间和可选 context size。
- restore 要求文件存在、大小大于 0、SHA-256 与元数据一致、slot/context 元数据匹配；任一校验失败返回结构化错误并删除损坏快照，不调用后端 restore。
- 无元数据的旧快照视为不可验证，按安全策略返回 MISS；不执行盲目 restore。
- 索引文件采用 `{version, snapshots}` 结构，使用临时文件 + rename 原子替换。索引损坏时从空索引启动，不删除原文件。

## 并发与错误

- 同一 key 的并发 save 共享同一个 Tokio 任务，避免后端重复写入。
- restore 会等待该 key 尚未完成的 save；save 失败不会阻塞 restore 的错误返回。
- 文件 IO 和哈希计算在 `spawn_blocking` 中执行，避免阻塞 Tokio 请求线程。
- 后端非 2xx、超时或响应 JSON 无效均返回错误；erase 的后端失败不删除本地快照。
- `clear_all` 逐槽 erase，删除所有 `.bin` 与 `.meta.json`，最后清空并持久化索引；单槽失败记录错误但继续清理。

## Mock 与测试

mock llama-server 新增 POST `/slots/{slot}?action=save|restore|erase`，save 写入可预测二进制并返回 `n_saved`/`n_written`，restore 校验文件名并返回成功，erase 返回成功。

测试覆盖：save/index/metadata、并发 save 去重、restore 成功、哈希损坏和元数据不匹配拒绝恢复、erase、clear_all、损坏索引降级，以及后端错误响应。

