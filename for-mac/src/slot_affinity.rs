use axum::http::HeaderMap;
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    path::{Path, PathBuf},
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
}
