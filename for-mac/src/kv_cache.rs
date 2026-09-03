use anyhow::{anyhow, Result};
use reqwest::Client;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    path::PathBuf,
    sync::{Arc, Mutex},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct KvSnapshot {
    pub key: String,
    pub slot: usize,
    pub saved_at: SystemTime,
    pub saved_tokens: u64,
    pub size_bytes: u64,
    pub sha256: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KvOperationResult {
    pub snapshot: Option<KvSnapshot>,
    pub response: serde_json::Value,
}

#[derive(Debug, Serialize, Deserialize)]
struct PersistRoot {
    #[serde(default = "default_version")]
    version: u32,
    #[serde(default)]
    snapshots: HashMap<String, PersistSnapshot>,
}

#[derive(Debug, Serialize, Deserialize)]
struct PersistSnapshot {
    slot: usize,
    saved_at: String,
    saved_tokens: u64,
    size_bytes: u64,
    sha256: String,
}

fn default_version() -> u32 {
    1
}

struct Inner {
    snapshots: HashMap<String, KvSnapshot>,
    inflight: HashMap<String, Arc<tokio::sync::Notify>>,
}

#[derive(Clone)]
pub struct KvCacheManager {
    client: Client,
    backend_base_url: String,
    cache_dir: PathBuf,
    index_path: PathBuf,
    slot_count: usize,
    context_size: Option<u64>,
    inner: Arc<Mutex<Inner>>,
}

impl KvCacheManager {
    pub fn new(
        client: Client,
        backend_base_url: impl Into<String>,
        cache_dir: impl Into<PathBuf>,
        index_path: impl Into<PathBuf>,
        slot_count: usize,
        context_size: Option<u64>,
    ) -> Self {
        let manager = Self {
            client,
            backend_base_url: backend_base_url.into().trim_end_matches('/').to_owned(),
            cache_dir: cache_dir.into(),
            index_path: index_path.into(),
            slot_count: slot_count.max(1),
            context_size,
            inner: Arc::new(Mutex::new(Inner {
                snapshots: HashMap::new(),
                inflight: HashMap::new(),
            })),
        };
        manager.load_index();
        manager
    }

    pub fn cache_file_path(&self, key: &str) -> PathBuf {
        self.cache_dir.join(format!("{}.bin", sanitize(key)))
    }

    fn metadata_path(&self, key: &str) -> PathBuf {
        self.cache_dir.join(format!("{}.meta.json", sanitize(key)))
    }

    pub fn snapshot(&self) -> Vec<KvSnapshot> {
        let mut values = self
            .inner
            .lock()
            .expect("KV mutex poisoned")
            .snapshots
            .values()
            .cloned()
            .collect::<Vec<_>>();
        values.sort_by(|a, b| b.saved_at.cmp(&a.saved_at));
        values
    }

    pub fn has_snapshot(&self, key: &str) -> bool {
        self.inner
            .lock()
            .expect("KV mutex poisoned")
            .snapshots
            .contains_key(key)
            && self.cache_file_path(key).is_file()
    }

    pub async fn save(&self, slot: usize, key: &str) -> Result<KvOperationResult> {
        if slot >= self.slot_count {
            return Err(anyhow!("slot {slot} is out of range"));
        }
        let notify = {
            let mut guard = self.inner.lock().expect("KV mutex poisoned");
            if let Some(existing) = guard.inflight.get(key) {
                Some(existing.clone())
            } else {
                let notify = Arc::new(tokio::sync::Notify::new());
                guard.inflight.insert(key.to_owned(), notify.clone());
                None
            }
        };
        if let Some(notify) = notify {
            notify.notified().await;
            return self
                .snapshot()
                .into_iter()
                .find(|snapshot| snapshot.key == key)
                .map(|snapshot| KvOperationResult {
                    snapshot: Some(snapshot),
                    response: serde_json::json!({"deduplicated": true}),
                })
                .ok_or_else(|| anyhow!("deduplicated save failed for {key}"));
        }
        let result = self.save_once(slot, key).await;
        let notify = self
            .inner
            .lock()
            .expect("KV mutex poisoned")
            .inflight
            .remove(key);
        if let Some(notify) = notify {
            notify.notify_one();
        }
        result
    }

    async fn save_once(&self, slot: usize, key: &str) -> Result<KvOperationResult> {
        tokio::fs::create_dir_all(&self.cache_dir).await?;
        let filename = self.cache_file_path(key).to_string_lossy().to_string();
        let response = self
            .client
            .post(format!(
                "{}/slots/{slot}?action=save",
                self.backend_base_url
            ))
            .json(&serde_json::json!({"filename": filename}))
            .send()
            .await?;
        let status = response.status();
        let body: serde_json::Value = response
            .json()
            .await
            .unwrap_or_else(|_| serde_json::json!({}));
        if !status.is_success() {
            return Err(anyhow!("KV save failed with HTTP {status}"));
        }
        let saved_tokens = body.get("n_saved").and_then(|v| v.as_u64()).unwrap_or(0);
        if !self.cache_file_path(key).is_file() {
            if let Some(data) = body.get("mock_data").and_then(|v| v.as_str()) {
                tokio::fs::write(self.cache_file_path(key), data.as_bytes()).await?;
            }
        }
        let bytes = tokio::fs::read(self.cache_file_path(key)).await?;
        if bytes.is_empty() || saved_tokens == 0 {
            let _ = self.delete_snapshot(key);
            return Err(anyhow!("KV save validation failed"));
        }
        let hash = hex_hash(&bytes);
        let snapshot = KvSnapshot {
            key: key.to_owned(),
            slot,
            saved_at: SystemTime::now(),
            saved_tokens,
            size_bytes: bytes.len() as u64,
            sha256: hash,
        };
        let metadata = serde_json::json!({"slot": slot, "saved_tokens": saved_tokens, "size_bytes": snapshot.size_bytes, "sha256": snapshot.sha256, "saved_at": timestamp(&snapshot.saved_at), "context_size": self.context_size});
        write_atomic(
            &self.metadata_path(key),
            &serde_json::to_vec_pretty(&metadata)?,
        )
        .await?;
        self.inner
            .lock()
            .expect("KV mutex poisoned")
            .snapshots
            .insert(key.to_owned(), snapshot.clone());
        self.save_index()?;
        Ok(KvOperationResult {
            snapshot: Some(snapshot),
            response: body,
        })
    }

    pub async fn restore(&self, slot: usize, key: &str) -> Result<KvOperationResult> {
        if slot >= self.slot_count {
            return Err(anyhow!("slot {slot} is out of range"));
        }
        loop {
            let pending = self
                .inner
                .lock()
                .expect("KV mutex poisoned")
                .inflight
                .get(key)
                .cloned();
            let Some(notify) = pending else {
                break;
            };
            notify.notified().await;
        }
        let snapshot = self
            .inner
            .lock()
            .expect("KV mutex poisoned")
            .snapshots
            .get(key)
            .cloned()
            .ok_or_else(|| anyhow!("KV snapshot not found: {key}"))?;
        let bytes = tokio::fs::read(self.cache_file_path(key))
            .await
            .map_err(|_| anyhow!("KV snapshot missing: {key}"))?;
        if bytes.is_empty()
            || bytes.len() as u64 != snapshot.size_bytes
            || hex_hash(&bytes) != snapshot.sha256
        {
            self.delete_snapshot(key)?;
            return Err(anyhow!("KV snapshot integrity check failed: {key}"));
        }
        let metadata = match std::fs::read(self.metadata_path(key))
            .ok()
            .and_then(|bytes| serde_json::from_slice::<serde_json::Value>(&bytes).ok())
        {
            Some(metadata) => metadata,
            None => {
                self.delete_snapshot(key)?;
                return Err(anyhow!("KV snapshot metadata missing or invalid: {key}"));
            }
        };
        let metadata_slot = metadata.get("slot").and_then(|v| v.as_u64());
        let metadata_tokens = metadata.get("saved_tokens").and_then(|v| v.as_u64());
        let metadata_size = metadata.get("size_bytes").and_then(|v| v.as_u64());
        let metadata_hash = metadata.get("sha256").and_then(|v| v.as_str());
        let context_matches = self.context_size.map_or(true, |context| {
            metadata.get("context_size").and_then(|v| v.as_u64()) == Some(context)
        });
        if metadata_slot != Some(snapshot.slot as u64)
            || metadata_tokens != Some(snapshot.saved_tokens)
            || metadata_size != Some(snapshot.size_bytes)
            || metadata_hash != Some(snapshot.sha256.as_str())
            || !context_matches
        {
            self.delete_snapshot(key)?;
            return Err(anyhow!("KV snapshot metadata validation failed: {key}"));
        }
        let response = self
            .client
            .post(format!(
                "{}/slots/{slot}?action=restore",
                self.backend_base_url
            ))
            .json(&serde_json::json!({"filename": self.cache_file_path(key)}))
            .send()
            .await?;
        let status = response.status();
        let body: serde_json::Value = response
            .json()
            .await
            .unwrap_or_else(|_| serde_json::json!({}));
        if !status.is_success() {
            return Err(anyhow!("KV restore failed with HTTP {status}"));
        }
        Ok(KvOperationResult {
            snapshot: Some(snapshot),
            response: body,
        })
    }

    pub async fn erase(&self, slot: usize) -> Result<serde_json::Value> {
        if slot >= self.slot_count {
            return Err(anyhow!("slot {slot} is out of range"));
        }
        let response = self
            .client
            .post(format!(
                "{}/slots/{slot}?action=erase",
                self.backend_base_url
            ))
            .send()
            .await?;
        let status = response.status();
        let body: serde_json::Value = response
            .json()
            .await
            .unwrap_or_else(|_| serde_json::json!({}));
        if !status.is_success() {
            return Err(anyhow!("KV erase failed with HTTP {status}"));
        }
        Ok(body)
    }

    pub fn delete_snapshot(&self, key: &str) -> Result<bool> {
        let existed = self
            .inner
            .lock()
            .expect("KV mutex poisoned")
            .snapshots
            .remove(key)
            .is_some();
        let _ = std::fs::remove_file(self.cache_file_path(key));
        let _ = std::fs::remove_file(self.metadata_path(key));
        self.save_index()?;
        Ok(existed)
    }

    pub async fn clear_all(&self) -> Result<usize> {
        let mut deleted = 0;
        if let Ok(mut entries) = tokio::fs::read_dir(&self.cache_dir).await {
            while let Some(entry) = entries.next_entry().await? {
                let path = entry.path();
                let is_bin = path.extension().and_then(|ext| ext.to_str()) == Some("bin");
                let is_metadata = path
                    .file_name()
                    .and_then(|name| name.to_str())
                    .is_some_and(|name| name.ends_with(".meta.json"));
                if is_bin || is_metadata {
                    if tokio::fs::remove_file(path).await.is_ok() && is_bin {
                        deleted += 1;
                    }
                }
            }
        }
        self.inner
            .lock()
            .expect("KV mutex poisoned")
            .snapshots
            .clear();
        self.save_index()?;
        for slot in 0..self.slot_count {
            let _ = self.erase(slot).await;
        }
        Ok(deleted)
    }

    fn load_index(&self) {
        let Ok(text) = std::fs::read_to_string(&self.index_path) else {
            return;
        };
        let Ok(root) = serde_json::from_str::<PersistRoot>(&text) else {
            return;
        };
        let mut guard = self.inner.lock().expect("KV mutex poisoned");
        for (key, value) in root.snapshots {
            if value.slot >= self.slot_count || value.sha256.is_empty() || value.size_bytes == 0 {
                continue;
            }
            let Some(saved_at) = parse_timestamp(&value.saved_at) else {
                continue;
            };
            guard.snapshots.insert(
                key.clone(),
                KvSnapshot {
                    key,
                    slot: value.slot,
                    saved_at,
                    saved_tokens: value.saved_tokens,
                    size_bytes: value.size_bytes,
                    sha256: value.sha256,
                },
            );
        }
    }

    fn save_index(&self) -> Result<()> {
        let guard = self.inner.lock().expect("KV mutex poisoned");
        let root = PersistRoot {
            version: 1,
            snapshots: guard
                .snapshots
                .iter()
                .map(|(key, value)| {
                    (
                        key.clone(),
                        PersistSnapshot {
                            slot: value.slot,
                            saved_at: timestamp(&value.saved_at),
                            saved_tokens: value.saved_tokens,
                            size_bytes: value.size_bytes,
                            sha256: value.sha256.clone(),
                        },
                    )
                })
                .collect(),
        };
        drop(guard);
        if let Some(parent) = self.index_path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let tmp = self
            .index_path
            .with_extension(format!("tmp-{}", std::process::id()));
        std::fs::write(&tmp, serde_json::to_vec_pretty(&root)?)?;
        std::fs::rename(tmp, &self.index_path)?;
        Ok(())
    }
}

fn sanitize(key: &str) -> String {
    let safe = key
        .chars()
        .filter(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.'))
        .collect::<String>();
    if safe.is_empty() {
        "snapshot".into()
    } else {
        safe
    }
}

fn timestamp(value: &SystemTime) -> String {
    value
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .to_string()
}
fn parse_timestamp(value: &str) -> Option<SystemTime> {
    value
        .parse::<u64>()
        .ok()
        .and_then(|ms| UNIX_EPOCH.checked_add(Duration::from_millis(ms)))
}
fn hex_hash(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

async fn write_atomic(path: &std::path::Path, bytes: &[u8]) -> Result<()> {
    let parent = path
        .parent()
        .ok_or_else(|| anyhow!("snapshot path has no parent"))?;
    tokio::fs::create_dir_all(parent).await?;
    let temp = path.with_extension(format!("tmp-{}", std::process::id()));
    tokio::fs::write(&temp, bytes).await?;
    if let Err(error) = tokio::fs::rename(&temp, path).await {
        let _ = tokio::fs::remove_file(&temp).await;
        return Err(error.into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::KvCacheManager;
    use axum::{
        extract::{Path, Query},
        response::IntoResponse,
        routing::post,
        Json, Router,
    };
    use serde_json::{json, Value};
    use std::fs;
    use std::{
        collections::HashMap,
        sync::{
            atomic::{AtomicUsize, Ordering},
            Arc,
        },
    };
    use tokio::{
        net::TcpListener,
        sync::oneshot,
        time::{sleep, Duration},
    };

    #[test]
    fn loads_index_and_sanitizes_snapshot_paths() {
        let dir = tempfile::tempdir().unwrap();
        let index = dir.path().join("kv_cache_index.json");
        fs::write(
            &index,
            r#"{"version":1,"snapshots":{"session/one":{"slot":0,"saved_at":"1","saved_tokens":12,"size_bytes":4,"sha256":"abcd"}}}"#,
        )
        .unwrap();
        let manager = KvCacheManager::new(
            reqwest::Client::new(),
            "http://127.0.0.1:1",
            dir.path().join("cache"),
            &index,
            2,
            Some(1024),
        );
        assert_eq!(manager.snapshot().len(), 1);
        assert_eq!(manager.snapshot()[0].saved_tokens, 12);
        assert!(manager
            .cache_file_path("../session/one")
            .starts_with(dir.path().join("cache")));
    }

    #[test]
    fn malformed_index_degrades_to_empty_snapshot() {
        let dir = tempfile::tempdir().unwrap();
        let index = dir.path().join("kv_cache_index.json");
        fs::write(&index, "{not-json").unwrap();
        let manager = KvCacheManager::new(
            reqwest::Client::new(),
            "http://127.0.0.1:1",
            dir.path().join("cache"),
            &index,
            1,
            None,
        );
        assert!(manager.snapshot().is_empty());
    }

    async fn slot_server(
        cache_dir: std::path::PathBuf,
        saves: Arc<AtomicUsize>,
    ) -> (String, oneshot::Sender<()>) {
        let app = Router::new().route(
            "/slots/:slot",
            post(
                move |Path(_slot): Path<usize>,
                      Query(query): Query<HashMap<String, String>>,
                      body: Option<Json<Value>>| {
                    let cache_dir = cache_dir.clone();
                    let saves = saves.clone();
                    async move {
                        match query.get("action").map(String::as_str) {
                            Some("save") => {
                                saves.fetch_add(1, Ordering::SeqCst);
                                sleep(Duration::from_millis(20)).await;
                                let filename = body
                                    .as_ref()
                                    .and_then(|Json(value)| value["filename"].as_str())
                                    .unwrap();
                                let path = if std::path::Path::new(filename).is_absolute() {
                                    std::path::PathBuf::from(filename)
                                } else {
                                    cache_dir.join(filename)
                                };
                                if let Some(parent) = path.parent() {
                                    let _ = tokio::fs::create_dir_all(parent).await;
                                }
                                tokio::fs::write(path, b"mock-kv-data").await.unwrap();
                                Json(json!({"n_saved": 3, "n_written": 12})).into_response()
                            }
                            Some("restore") => Json(json!({"status":"restored"})).into_response(),
                            Some("erase") => Json(json!({"status":"erased"})).into_response(),
                            _ => (
                                axum::http::StatusCode::BAD_REQUEST,
                                Json(json!({"error":"action"})),
                            )
                                .into_response(),
                        }
                    }
                },
            ),
        );
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let (tx, rx) = oneshot::channel();
        tokio::spawn(async move {
            axum::serve(listener, app)
                .with_graceful_shutdown(async {
                    let _ = rx.await;
                })
                .await
                .unwrap();
        });
        (format!("http://{address}"), tx)
    }

    #[tokio::test]
    async fn saves_deduplicated_snapshot_and_restores_it() {
        let dir = tempfile::tempdir().unwrap();
        let cache_dir = dir.path().join("cache");
        let saves = Arc::new(AtomicUsize::new(0));
        let (base, shutdown) = slot_server(cache_dir.clone(), saves.clone()).await;
        let manager = KvCacheManager::new(
            reqwest::Client::new(),
            base,
            &cache_dir,
            dir.path().join("index.json"),
            2,
            Some(1024),
        );
        let (left, right) = tokio::join!(manager.save(0, "session"), manager.save(0, "session"));
        assert!(left.is_ok() && right.is_ok());
        assert_eq!(saves.load(Ordering::SeqCst), 1);
        assert_eq!(manager.snapshot()[0].saved_tokens, 3);
        assert!(manager.restore(1, "session").await.is_ok());
        let _ = shutdown.send(());
    }

    #[tokio::test]
    async fn rejects_modified_snapshot_before_backend_restore() {
        let dir = tempfile::tempdir().unwrap();
        let cache_dir = dir.path().join("cache");
        let saves = Arc::new(AtomicUsize::new(0));
        let (base, shutdown) = slot_server(cache_dir.clone(), saves).await;
        let manager = KvCacheManager::new(
            reqwest::Client::new(),
            base,
            &cache_dir,
            dir.path().join("index.json"),
            1,
            None,
        );
        manager.save(0, "session").await.unwrap();
        fs::write(manager.cache_file_path("session"), b"corrupted").unwrap();
        assert!(manager.restore(0, "session").await.is_err());
        assert!(!manager.has_snapshot("session"));
        manager.save(0, "metadata").await.unwrap();
        let metadata_path = manager.metadata_path("metadata");
        let mut metadata: Value =
            serde_json::from_slice(&fs::read(&metadata_path).unwrap()).unwrap();
        metadata["sha256"] = json!("bad-hash");
        fs::write(&metadata_path, serde_json::to_vec(&metadata).unwrap()).unwrap();
        assert!(manager.restore(0, "metadata").await.is_err());
        assert!(!manager.has_snapshot("metadata"));
        let _ = shutdown.send(());
    }

    #[tokio::test]
    async fn erase_and_clear_all_remove_local_snapshots_and_call_each_slot() {
        let dir = tempfile::tempdir().unwrap();
        let cache_dir = dir.path().join("cache");
        let saves = Arc::new(AtomicUsize::new(0));
        let (base, shutdown) = slot_server(cache_dir.clone(), saves).await;
        let manager = KvCacheManager::new(
            reqwest::Client::new(),
            base,
            &cache_dir,
            dir.path().join("index.json"),
            2,
            None,
        );
        manager.save(0, "one").await.unwrap();
        manager.save(1, "two").await.unwrap();
        assert!(manager.erase(0).await.is_ok());
        assert_eq!(manager.clear_all().await.unwrap(), 2);
        assert!(manager.snapshot().is_empty());
        assert!(!cache_dir.join("one.bin").exists());
        assert!(!cache_dir.join("two.meta.json").exists());
        let _ = shutdown.send(());
    }
}
