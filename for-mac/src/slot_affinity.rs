use axum::http::HeaderMap;
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    path::PathBuf,
    sync::{
        atomic::{AtomicUsize, Ordering},
        Mutex,
    },
    time::SystemTime,
};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SlotBinding {
    pub key: String,
    pub app: String,
    pub slot: usize,
    pub last_active: SystemTime,
    pub preemptive: bool,
    pub kv_cache: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SlotAllocation {
    pub slot: usize,
    pub key: Option<String>,
    pub new_binding: bool,
    pub evicted: Option<SlotBinding>,
}

#[derive(Debug, Clone)]
struct BindingRecord {
    slot: usize,
    last_active: SystemTime,
    preemptive: bool,
    kv_cache: bool,
}

struct Inner {
    bindings: HashMap<String, BindingRecord>,
    tool_locked: HashSet<String>,
}

pub struct SlotAffinity {
    slot_count: usize,
    path: PathBuf,
    inner: Mutex<Inner>,
}

static NEXT_RANDOM_SLOT: AtomicUsize = AtomicUsize::new(0);

impl SlotAffinity {
    pub fn new(slot_count: usize, path: impl Into<PathBuf>) -> Self {
        Self {
            slot_count: slot_count.max(1),
            path: path.into(),
            inner: Mutex::new(Inner {
                bindings: HashMap::new(),
                tool_locked: HashSet::new(),
            }),
        }
    }

    pub fn slot_count(&self) -> usize {
        self.slot_count
    }

    pub fn affinity_key(headers: &HeaderMap) -> Option<String> {
        let value = |name: &str| {
            headers
                .get(name)
                .and_then(|v| v.to_str().ok())
                .filter(|v| !v.is_empty())
        };
        if let Some(uid) = value("x-deepseek-harness-user-id") {
            return Some(format!("dsh_rule_{uid}"));
        }
        if let Some(cid) = value("x-conversation-id") {
            return Some(format!("webui_{cid}"));
        }
        if value("x-model-provider")
            .is_some_and(|v| v.eq_ignore_ascii_case("custom_openai_compatible"))
        {
            return Some("trae_global".into());
        }
        let user_agent = value("user-agent").unwrap_or_default();
        let has_stainless = headers
            .keys()
            .any(|name| name.as_str().starts_with("x-stainless-"));
        if user_agent.to_ascii_lowercase().contains("deepseek-harness") && has_stainless {
            return Some("dsh_agent_global".into());
        }
        None
    }

    pub fn app_name(key: &str) -> String {
        if key.starts_with("trae_") {
            "Trae Work"
        } else if key.starts_with("webui_") {
            "WebUI"
        } else if key.starts_with("dsh_rule_") {
            "DSH 规则引擎"
        } else if key.starts_with("dsh_agent_") {
            "DSH 主 Agent"
        } else {
            "未知应用"
        }
        .to_string()
    }

    pub fn snapshot(&self) -> Vec<SlotBinding> {
        let guard = self.inner.lock().expect("slot affinity mutex poisoned");
        let mut values = guard
            .bindings
            .iter()
            .map(|(key, record)| SlotBinding {
                key: key.clone(),
                app: Self::app_name(key),
                slot: record.slot,
                last_active: record.last_active,
                preemptive: record.preemptive,
                kv_cache: record.kv_cache,
            })
            .collect::<Vec<_>>();
        values.sort_by(|a, b| b.last_active.cmp(&a.last_active));
        values
    }

    pub fn allocate(&self, headers: &HeaderMap, auto_preemptive: &[String]) -> SlotAllocation {
        let Some(key) = Self::affinity_key(headers) else {
            return SlotAllocation {
                slot: NEXT_RANDOM_SLOT.fetch_add(1, Ordering::Relaxed) % self.slot_count,
                key: None,
                new_binding: false,
                evicted: None,
            };
        };
        let auto_pre = auto_preemptive.iter().any(|prefix| {
            !prefix.is_empty()
                && key
                    .to_ascii_lowercase()
                    .starts_with(&prefix.to_ascii_lowercase())
        });
        let mut guard = self.inner.lock().expect("slot affinity mutex poisoned");
        if guard.bindings.contains_key(&key) {
            let should_promote = auto_pre
                && !guard
                    .bindings
                    .get(&key)
                    .is_some_and(|record| record.preemptive)
                && guard.bindings.values().filter(|b| b.preemptive).count()
                    < self.slot_count.saturating_sub(1);
            let record = guard.bindings.get_mut(&key).expect("binding exists");
            if should_promote {
                record.preemptive = true;
            }
            record.last_active = SystemTime::now();
            return SlotAllocation {
                slot: record.slot,
                key: Some(key),
                new_binding: false,
                evicted: None,
            };
        }

        let used: HashSet<usize> = guard.bindings.values().map(|record| record.slot).collect();
        let slot = (0..self.slot_count).find(|slot| !used.contains(slot));
        let mut evicted = None;
        let slot = slot.or_else(|| {
            let victim_key = guard
                .bindings
                .iter()
                .filter(|(_, record)| !record.preemptive)
                .min_by_key(|(_, record)| record.last_active)
                .map(|(key, _)| key.clone())
                .or_else(|| {
                    if auto_pre {
                        guard
                            .bindings
                            .iter()
                            .filter(|(key, record)| {
                                record.preemptive && guard.tool_locked.contains(*key)
                            })
                            .min_by_key(|(_, record)| record.last_active)
                            .map(|(key, _)| key.clone())
                            .or_else(|| {
                                guard
                                    .bindings
                                    .iter()
                                    .filter(|(_, record)| record.preemptive)
                                    .min_by_key(|(_, record)| record.last_active)
                                    .map(|(key, _)| key.clone())
                            })
                    } else {
                        None
                    }
                });
            let victim_key = victim_key?;
            let victim = guard.bindings.remove(&victim_key)?;
            guard.tool_locked.remove(&victim_key);
            evicted = Some(SlotBinding {
                key: victim_key.clone(),
                app: Self::app_name(&victim_key),
                slot: victim.slot,
                last_active: victim.last_active,
                preemptive: victim.preemptive,
                kv_cache: victim.kv_cache,
            });
            Some(victim.slot)
        });
        let slot = slot
            .unwrap_or_else(|| NEXT_RANDOM_SLOT.fetch_add(1, Ordering::Relaxed) % self.slot_count);
        let cap = self.slot_count.saturating_sub(1);
        let final_pre = auto_pre
            && guard
                .bindings
                .values()
                .filter(|record| record.preemptive)
                .count()
                < cap;
        guard.bindings.insert(
            key.clone(),
            BindingRecord {
                slot,
                last_active: SystemTime::now(),
                preemptive: final_pre,
                kv_cache: true,
            },
        );
        SlotAllocation {
            slot,
            key: Some(key),
            new_binding: true,
            evicted,
        }
    }

    pub fn set_preemptive(&self, key: &str, value: bool) {
        let mut guard = self.inner.lock().expect("slot affinity mutex poisoned");
        if guard.bindings.contains_key(key) {
            let allowed = !value
                || guard
                    .bindings
                    .get(key)
                    .is_some_and(|record| record.preemptive)
                || guard.bindings.values().filter(|b| b.preemptive).count()
                    < self.slot_count.saturating_sub(1);
            if allowed {
                guard
                    .bindings
                    .get_mut(key)
                    .expect("binding exists")
                    .preemptive = value;
            }
        }
    }

    pub fn is_preemptive(&self, key: &str) -> bool {
        self.inner
            .lock()
            .expect("slot affinity mutex poisoned")
            .bindings
            .get(key)
            .is_some_and(|record| record.preemptive)
    }

    pub fn mark_tool_locked(&self, key: &str) {
        self.inner
            .lock()
            .expect("slot affinity mutex poisoned")
            .tool_locked
            .insert(key.to_string());
    }
    pub fn unmark_tool_locked(&self, key: &str) {
        self.inner
            .lock()
            .expect("slot affinity mutex poisoned")
            .tool_locked
            .remove(key);
    }

    pub fn set_kv_cache(&self, key: &str, value: bool) {
        if let Some(record) = self
            .inner
            .lock()
            .expect("slot affinity mutex poisoned")
            .bindings
            .get_mut(key)
        {
            record.kv_cache = value;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::SlotAffinity;
    use axum::http::HeaderMap;

    #[test]
    fn recognizes_affinity_headers_in_priority_order() {
        let mut headers = HeaderMap::new();
        headers.insert("x-conversation-id", "conversation-1".parse().unwrap());
        headers.insert("x-deepseek-harness-user-id", "user-1".parse().unwrap());
        assert_eq!(
            SlotAffinity::affinity_key(&headers).as_deref(),
            Some("dsh_rule_user-1")
        );

        headers.remove("x-deepseek-harness-user-id");
        assert_eq!(
            SlotAffinity::affinity_key(&headers).as_deref(),
            Some("webui_conversation-1")
        );
    }

    #[test]
    fn recognizes_trae_and_deepseek_agent_headers() {
        let mut headers = HeaderMap::new();
        headers.insert(
            "x-model-provider",
            "CUSTOM_OPENAI_COMPATIBLE".parse().unwrap(),
        );
        assert_eq!(
            SlotAffinity::affinity_key(&headers).as_deref(),
            Some("trae_global")
        );

        headers.clear();
        headers.insert("user-agent", "deepseek-harness/1.0".parse().unwrap());
        headers.insert("x-stainless-os", "macos".parse().unwrap());
        assert_eq!(
            SlotAffinity::affinity_key(&headers).as_deref(),
            Some("dsh_agent_global")
        );
    }

    #[test]
    fn unknown_requests_have_no_binding() {
        let manager =
            SlotAffinity::new(2, tempfile::tempdir().unwrap().path().join("bindings.json"));
        assert_eq!(manager.slot_count(), 2);
        assert!(manager.snapshot().is_empty());
        assert!(SlotAffinity::affinity_key(&HeaderMap::new()).is_none());
    }

    fn key_headers(name: &str) -> HeaderMap {
        let mut headers = HeaderMap::new();
        headers.insert("x-conversation-id", name.parse().unwrap());
        headers
    }

    #[test]
    fn reuses_binding_and_evicts_least_recent_non_preemptive() {
        let dir = tempfile::tempdir().unwrap();
        let manager = SlotAffinity::new(2, dir.path().join("bindings.json"));
        let first = manager.allocate(&key_headers("one"), &[]);
        let second = manager.allocate(&key_headers("two"), &[]);
        assert_ne!(first.slot, second.slot);
        assert_eq!(manager.allocate(&key_headers("one"), &[]).slot, first.slot);
        let third = manager.allocate(&key_headers("three"), &[]);
        assert_eq!(
            third.evicted.as_ref().map(|b| b.key.as_str()),
            Some("webui_two")
        );
        assert_eq!(third.slot, second.slot);
    }

    #[test]
    fn protects_preemptive_binding_and_caps_preemptive_count() {
        let dir = tempfile::tempdir().unwrap();
        let manager = SlotAffinity::new(3, dir.path().join("bindings.json"));
        let first = manager.allocate(&key_headers("one"), &[]);
        manager.set_preemptive("webui_one", true);
        let second = manager.allocate(&key_headers("two"), &["webui_".into()]);
        let third = manager.allocate(&key_headers("three"), &["webui_".into()]);
        assert!(manager.is_preemptive("webui_one"));
        assert_eq!(
            manager.snapshot().iter().filter(|b| b.preemptive).count(),
            2
        );
        let ordinary = manager.allocate(&key_headers("four"), &[]);
        assert_ne!(
            ordinary.evicted.as_ref().map(|b| b.key.as_str()),
            Some("webui_one")
        );
        assert_eq!(
            first.slot,
            manager
                .snapshot()
                .iter()
                .find(|b| b.key == "webui_one")
                .unwrap()
                .slot
        );
        assert!(second.new_binding && third.new_binding);
    }

    #[test]
    fn tool_locked_preemptive_binding_is_eviction_candidate_first() {
        let dir = tempfile::tempdir().unwrap();
        let manager = SlotAffinity::new(3, dir.path().join("bindings.json"));
        manager.allocate(&key_headers("one"), &["webui_".into()]);
        manager.allocate(&key_headers("two"), &["webui_".into()]);
        manager.mark_tool_locked("webui_one");
        manager.set_preemptive("webui_one", true);
        manager.set_preemptive("webui_two", true);
        manager.allocate(&key_headers("three"), &[]);
        assert!(manager.is_preemptive("webui_one"));
        manager.unmark_tool_locked("webui_one");
        assert!(manager.is_preemptive("webui_one"));
    }
}
