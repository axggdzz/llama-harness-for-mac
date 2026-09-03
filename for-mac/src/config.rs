use crate::thinking::ThinkingMode;
use anyhow::{anyhow, Result};
use directories::ProjectDirs;
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AppConfig {
    #[serde(default = "default_gateway_port")]
    pub gateway_port: u16,
    #[serde(default = "default_backend_host")]
    pub backend_host: String,
    #[serde(default = "default_backend_port")]
    pub backend_port: u16,
    #[serde(default)]
    pub backend_executable: Option<PathBuf>,
    #[serde(default)]
    pub backend_args: Vec<String>,
    #[serde(default = "default_ready_timeout_ms")]
    pub ready_timeout_ms: u64,
    #[serde(default = "default_ready_poll_ms")]
    pub ready_poll_ms: u64,
    #[serde(default)]
    pub warming_delay_ms: u64,
    #[serde(default = "default_idle_timeout_ms")]
    pub idle_timeout_ms: u64,
    #[serde(default = "default_sleep_observe_ms")]
    pub sleep_observe_ms: u64,
    #[serde(default = "default_slot_count")]
    pub slot_count: usize,
    #[serde(default)]
    pub token_guard_enabled: bool,
    #[serde(default)]
    pub context_size: Option<usize>,
    #[serde(default = "default_reserved_output_tokens")]
    pub reserved_output_tokens: usize,
    #[serde(default = "default_reserved_prompt_overhead")]
    pub reserved_prompt_overhead: usize,
    #[serde(default = "default_context_overflow_recovery")]
    pub context_overflow_recovery: bool,
    #[serde(default)]
    pub thinking_mode: ThinkingMode,
    #[serde(default = "default_continuation_enabled")]
    pub continuation_enabled: bool,
    #[serde(default = "default_max_continuations")]
    pub max_continuations: usize,
    #[serde(default = "default_continuation_timeout_ms")]
    pub continuation_timeout_ms: u64,
    #[serde(default = "default_crash_recovery_enabled")]
    pub crash_recovery_enabled: bool,
    #[serde(default = "default_max_crash_count")]
    pub max_crash_count: usize,
    #[serde(default)]
    pub slot_bindings_path: Option<PathBuf>,
    #[serde(default)]
    pub auto_preemptive_prefixes: Vec<String>,
    #[serde(default = "default_data_dir")]
    pub data_dir: PathBuf,
    #[serde(default)]
    pub log_dir: Option<PathBuf>,
    #[serde(default = "default_log_max_bytes")]
    pub log_max_bytes: u64,
    #[serde(default)]
    pub request_dump_enabled: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BackendConfig {
    pub executable: PathBuf,
    pub args: Vec<String>,
    pub host: String,
    pub port: u16,
    pub ready_timeout_ms: u64,
    pub ready_poll_ms: u64,
}

pub fn parse_backend_args(raw: &str) -> Vec<String> {
    serde_json::from_str::<Vec<String>>(raw)
        .unwrap_or_else(|_| raw.split_whitespace().map(str::to_owned).collect())
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            gateway_port: default_gateway_port(),
            backend_host: default_backend_host(),
            backend_port: default_backend_port(),
            backend_executable: None,
            backend_args: Vec::new(),
            ready_timeout_ms: default_ready_timeout_ms(),
            ready_poll_ms: default_ready_poll_ms(),
            warming_delay_ms: 0,
            idle_timeout_ms: default_idle_timeout_ms(),
            sleep_observe_ms: default_sleep_observe_ms(),
            slot_count: default_slot_count(),
            token_guard_enabled: false,
            context_size: None,
            reserved_output_tokens: default_reserved_output_tokens(),
            reserved_prompt_overhead: default_reserved_prompt_overhead(),
            context_overflow_recovery: default_context_overflow_recovery(),
            thinking_mode: ThinkingMode::default(),
            continuation_enabled: default_continuation_enabled(),
            max_continuations: default_max_continuations(),
            continuation_timeout_ms: default_continuation_timeout_ms(),
            crash_recovery_enabled: default_crash_recovery_enabled(),
            max_crash_count: default_max_crash_count(),
            slot_bindings_path: None,
            auto_preemptive_prefixes: Vec::new(),
            data_dir: default_data_dir(),
            log_dir: None,
            log_max_bytes: default_log_max_bytes(),
            request_dump_enabled: false,
        }
    }
}

impl AppConfig {
    pub fn config_path(&self) -> PathBuf {
        self.data_dir.join("config.json")
    }

    pub fn load_from(path: impl AsRef<Path>) -> Result<Self> {
        let path = path.as_ref();
        let bytes = fs::read(path)?;
        let config: Self = serde_json::from_slice(&bytes)?;
        config.validate()?;
        Ok(config)
    }

    pub fn save_to(&self, path: impl AsRef<Path>) -> Result<()> {
        self.validate()?;
        let path = path.as_ref();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let temporary = path.with_extension("json.tmp");
        fs::write(&temporary, serde_json::to_vec_pretty(self)?)?;
        fs::rename(temporary, path)?;
        Ok(())
    }

    pub fn validate(&self) -> Result<()> {
        if self.gateway_port == 0 {
            return Err(anyhow!("gateway_port must be non-zero"));
        }
        if self.backend_port == 0 {
            return Err(anyhow!("backend_port must be non-zero"));
        }
        if self.backend_host.trim().is_empty() {
            return Err(anyhow!("backend_host must not be empty"));
        }
        if self.slot_count == 0 {
            return Err(anyhow!("slot_count must be greater than zero"));
        }
        Ok(())
    }

    pub fn backend_config(&self) -> Option<BackendConfig> {
        self.backend_executable
            .clone()
            .map(|executable| BackendConfig {
                executable,
                args: self.backend_args.clone(),
                host: self.backend_host.clone(),
                port: self.backend_port,
                ready_timeout_ms: self.ready_timeout_ms,
                ready_poll_ms: self.ready_poll_ms,
            })
    }
}

fn default_gateway_port() -> u16 {
    8080
}
fn default_backend_host() -> String {
    "127.0.0.1".to_owned()
}
fn default_backend_port() -> u16 {
    8081
}
fn default_ready_timeout_ms() -> u64 {
    30_000
}
fn default_ready_poll_ms() -> u64 {
    100
}
fn default_idle_timeout_ms() -> u64 {
    15 * 60 * 1000
}
fn default_sleep_observe_ms() -> u64 {
    10_000
}
fn default_slot_count() -> usize {
    1
}
fn default_reserved_output_tokens() -> usize {
    1024
}
fn default_reserved_prompt_overhead() -> usize {
    10240
}
fn default_context_overflow_recovery() -> bool {
    true
}
fn default_continuation_enabled() -> bool {
    true
}
fn default_max_continuations() -> usize {
    1
}
fn default_continuation_timeout_ms() -> u64 {
    120_000
}
fn default_crash_recovery_enabled() -> bool {
    true
}
fn default_max_crash_count() -> usize {
    3
}

fn default_data_dir() -> PathBuf {
    ProjectDirs::from("com", "axggdzz", "LlamaHarness")
        .map(|dirs| dirs.data_dir().to_path_buf())
        .unwrap_or_else(|| PathBuf::from(".llama-harness"))
}
fn default_log_max_bytes() -> u64 {
    10 * 1024 * 1024
}

#[cfg(test)]
mod tests {
    use tempfile::tempdir;

    #[test]
    fn defaults_use_fixed_gateway_port_and_application_support() {
        let config = super::AppConfig::default();
        assert_eq!(config.gateway_port, 8080);
        assert!(config
            .data_dir
            .to_string_lossy()
            .contains("Application Support"));
    }

    #[test]
    fn config_round_trips_atomically_and_rejects_invalid_ports() {
        let dir = tempdir().unwrap();
        let path = dir.path().join("nested/config.json");
        let mut config = super::AppConfig::default();
        config.backend_port = 9123;
        config.save_to(&path).unwrap();
        let loaded = super::AppConfig::load_from(&path).unwrap();
        assert_eq!(loaded.backend_port, 9123);
        config.gateway_port = 0;
        assert!(config.save_to(&path).is_err());
    }

    #[test]
    fn backend_args_json_preserves_spaces() {
        assert_eq!(
            super::parse_backend_args(r#"["--model","model path.gguf","--ctx-size","4096"]"#),
            vec!["--model", "model path.gguf", "--ctx-size", "4096"]
        );
    }
}
