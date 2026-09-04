use crate::config::BackendConfig;
use anyhow::{anyhow, Context, Result};
use reqwest::Client;
use serde::Serialize;
use std::collections::VecDeque;
use std::process::Stdio;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};
use std::time::Duration;
use tokio::io::AsyncReadExt;
use tokio::process::{Child, Command};
use tokio::sync::Mutex;
use tokio::time::{sleep, timeout, Instant};

pub struct BackendProcess;

#[derive(Debug, Clone, Default, Serialize)]
pub struct BackendCapabilities {
    pub version: Option<String>,
    pub props: bool,
    pub slots: bool,
    pub metrics: bool,
    pub tokenize: bool,
    pub slot_save_path: bool,
    pub degradations: Vec<String>,
}

fn extract_backend_version(props: &serde_json::Value) -> Option<String> {
    props
        .get("build_info")
        .and_then(serde_json::Value::as_str)
        .or_else(|| props.get("version").and_then(serde_json::Value::as_str))
        .map(str::to_owned)
}

fn filter_arguments_from_help(args: &[String], help: &str) -> Vec<String> {
    if help.trim().is_empty() {
        return args.to_vec();
    }
    let supported = help
        .split_whitespace()
        .filter_map(|token| token.strip_prefix("--"))
        .map(|token| {
            format!(
                "--{}",
                token
                    .trim_matches(|ch: char| !ch.is_ascii_alphanumeric() && ch != '-')
                    .split('=')
                    .next()
                    .unwrap_or(token)
            )
        })
        .collect::<std::collections::HashSet<_>>();
    let mut filtered = Vec::with_capacity(args.len());
    let mut index = 0;
    while index < args.len() {
        let argument = &args[index];
        if !argument.starts_with("--") {
            filtered.push(argument.clone());
            index += 1;
            continue;
        }
        let name = argument.split('=').next().unwrap_or(argument);
        if supported.contains(name) {
            filtered.push(argument.clone());
            if !argument.contains('=')
                && args
                    .get(index + 1)
                    .is_some_and(|next| !next.starts_with("--"))
            {
                filtered.push(args[index + 1].clone());
                index += 1;
            }
        } else if !argument.contains('=')
            && args
                .get(index + 1)
                .is_some_and(|next| !next.starts_with("--"))
        {
            index += 1;
        }
        index += 1;
    }
    filtered
}

fn is_llama_server_binary(path: &std::path::Path) -> bool {
    matches!(
        path.file_name().and_then(|name| name.to_str()),
        Some("llama-server" | "llama-server.exe")
    )
}

pub struct BackendHandle {
    child: Arc<Mutex<Option<Child>>>,
    config: BackendConfig,
    client: Client,
    oom_evidence: Arc<AtomicBool>,
    stderr_tail: Arc<Mutex<VecDeque<u8>>>,
}

impl BackendProcess {
    pub fn command_arguments(config: &BackendConfig) -> Vec<String> {
        config.args.clone()
    }

    pub async fn start(config: BackendConfig) -> Result<BackendHandle> {
        Self::start_with_logger(config, None).await
    }

    pub async fn start_with_logger(
        config: BackendConfig,
        logger: Option<Arc<crate::observability::RotatingLogger>>,
    ) -> Result<BackendHandle> {
        let args = Self::detected_arguments(&config).await;
        if args != config.args {
            let message = format!(
                "backend capability probe removed unsupported arguments for {}",
                config.executable.display()
            );
            tracing::warn!(target = "backend", "{message}");
            if let Some(logger) = &logger {
                let _ = logger.write(crate::observability::LogKind::Main, &message);
            }
        }
        let mut command = Command::new(&config.executable);
        command
            .args(args)
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());

        #[cfg(unix)]
        {
            unsafe {
                command.pre_exec(|| {
                    if libc::setpgid(0, 0) != 0 {
                        return Err(std::io::Error::last_os_error());
                    }
                    Ok(())
                });
            }
        }

        let mut child = command
            .spawn()
            .with_context(|| format!("failed to spawn {}", config.executable.display()))?;

        if let Some(mut stdout) = child.stdout.take() {
            tokio::spawn(async move {
                let mut sink = Vec::new();
                let _ = stdout.read_to_end(&mut sink).await;
            });
        }
        let oom_evidence = Arc::new(AtomicBool::new(false));
        let stderr_tail = Arc::new(Mutex::new(VecDeque::with_capacity(64 * 1024)));
        if let Some(mut stderr) = child.stderr.take() {
            let oom_evidence = oom_evidence.clone();
            let stderr_tail = stderr_tail.clone();
            let logger = logger.clone();
            tokio::spawn(async move {
                let mut chunk = [0_u8; 4096];
                loop {
                    let read = match stderr.read(&mut chunk).await {
                        Ok(0) | Err(_) => break,
                        Ok(read) => read,
                    };
                    let bytes = &chunk[..read];
                    let text = String::from_utf8_lossy(bytes);
                    let lower = text.to_ascii_lowercase();
                    if ["bad_alloc", "bad allocation", "out of memory", "oom"]
                        .iter()
                        .any(|needle| lower.contains(needle))
                    {
                        oom_evidence.store(true, Ordering::Release);
                    }
                    if let Some(logger) = &logger {
                        let _ = logger.write(crate::observability::LogKind::Error, text.trim_end());
                    }
                    let mut tail = stderr_tail.lock().await;
                    tail.extend(bytes.iter().copied());
                    while tail.len() > 64 * 1024 {
                        tail.pop_front();
                    }
                }
            });
        }

        Ok(BackendHandle {
            child: Arc::new(Mutex::new(Some(child))),
            config,
            client: Client::new(),
            oom_evidence,
            stderr_tail,
        })
    }

    async fn detected_arguments(config: &BackendConfig) -> Vec<String> {
        if !is_llama_server_binary(&config.executable) {
            return config.args.clone();
        }
        let mut probe = Command::new(&config.executable);
        probe.arg("--help").kill_on_drop(true);
        let help = timeout(Duration::from_secs(2), probe.output())
            .await
            .ok()
            .and_then(Result::ok)
            .map(|output| {
                let mut text = String::from_utf8_lossy(&output.stdout).into_owned();
                text.push_str(&String::from_utf8_lossy(&output.stderr));
                text
            })
            .unwrap_or_default();
        filter_arguments_from_help(&config.args, &help)
    }
}

impl BackendHandle {
    pub fn pid(&self) -> Option<u32> {
        self.child
            .try_lock()
            .ok()
            .and_then(|child| child.as_ref().and_then(Child::id))
    }

    pub fn base_url(&self) -> String {
        format!("http://{}:{}", self.config.host, self.config.port)
    }

    pub fn has_oom_evidence(&self) -> bool {
        self.oom_evidence.load(Ordering::Acquire)
    }

    pub async fn stderr_tail(&self, max_bytes: usize) -> String {
        let tail = self.stderr_tail.lock().await;
        let skip = tail.len().saturating_sub(max_bytes.max(1));
        String::from_utf8_lossy(&tail.iter().skip(skip).copied().collect::<Vec<_>>()).into_owned()
    }

    pub async fn is_running(&self) -> bool {
        self.child
            .lock()
            .await
            .as_mut()
            .and_then(|child| child.try_wait().ok())
            .is_none()
    }

    pub async fn wait_ready(&self) -> Result<()> {
        let deadline = Instant::now() + Duration::from_millis(self.config.ready_timeout_ms);
        let url = format!("{}/health", self.base_url());
        loop {
            let now = Instant::now();
            if now >= deadline {
                return Err(anyhow!(
                    "backend readiness timeout after {}ms",
                    self.config.ready_timeout_ms
                ));
            }

            {
                let mut child = self.child.lock().await;
                if let Some(child) = child.as_mut() {
                    if let Some(status) = child.try_wait()? {
                        return Err(anyhow!("backend exited before readiness: {status}"));
                    }
                } else {
                    return Err(anyhow!("backend process is not running"));
                }
            }

            let remaining = deadline.saturating_duration_since(now);

            if let Ok(Ok(response)) = timeout(remaining, self.client.get(&url).send()).await {
                if response.status().is_success() {
                    if let Ok(Ok(json)) =
                        timeout(remaining, response.json::<serde_json::Value>()).await
                    {
                        if json.get("status").and_then(serde_json::Value::as_str) == Some("ok") {
                            return Ok(());
                        }
                    }
                }
            }

            sleep(Duration::from_millis(self.config.ready_poll_ms).min(remaining)).await;
        }
    }

    pub async fn probe_capabilities(&self) -> BackendCapabilities {
        let props_body = match self
            .client
            .get(format!("{}/props", self.base_url()))
            .send()
            .await
        {
            Ok(response) if response.status().is_success() => {
                response.json::<serde_json::Value>().await.ok()
            }
            _ => None,
        };
        let slots = self.probe_endpoint("/slots").await;
        let metrics = self.probe_endpoint("/metrics").await;
        let tokenize = self
            .client
            .post(format!("{}/v1/tokenize", self.base_url()))
            .json(&serde_json::json!({"content":""}))
            .send()
            .await
            .map(|response| response.status().is_success())
            .unwrap_or(false);
        let props = props_body.is_some();
        let mut degradations = Vec::new();
        if !tokenize {
            degradations.push("TokenGuard 将跳过真实 tokenize 并保留原请求".to_owned());
        }
        if !metrics {
            degradations.push("后端未启用 /metrics，统计页使用网关本地指标".to_owned());
        }
        if !self
            .config
            .args
            .iter()
            .any(|arg| arg == "--slot-save-path" || arg.starts_with("--slot-save-path="))
        {
            degradations
                .push("未配置 --slot-save-path，KV 快照仅在后端支持绝对路径时可用".to_owned());
        }
        BackendCapabilities {
            version: props_body.as_ref().and_then(extract_backend_version),
            props,
            slots,
            metrics,
            tokenize,
            slot_save_path: self
                .config
                .args
                .iter()
                .any(|arg| arg == "--slot-save-path" || arg.starts_with("--slot-save-path=")),
            degradations,
        }
    }

    async fn probe_endpoint(&self, path: &str) -> bool {
        self.client
            .get(format!("{}{path}", self.base_url()))
            .send()
            .await
            .map(|response| response.status().is_success())
            .unwrap_or(false)
    }

    pub async fn stop(&self) -> Result<()> {
        let mut guard = self.child.lock().await;
        let Some(mut child) = guard.take() else {
            return Ok(());
        };

        #[cfg(unix)]
        if let Some(pid) = child.id() {
            unsafe {
                libc::kill(-(pid as libc::pid_t), libc::SIGTERM);
            }
        }

        if timeout(Duration::from_secs(2), child.wait()).await.is_err() {
            #[cfg(unix)]
            if let Some(pid) = child.id() {
                unsafe {
                    libc::kill(-(pid as libc::pid_t), libc::SIGKILL);
                }
            }
            let _ = child.wait().await;
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::super::config::BackendConfig;
    use super::BackendProcess;
    use std::path::PathBuf;

    #[test]
    fn command_arguments_preserve_spaces_without_shell_reparsing() {
        let config = BackendConfig {
            executable: PathBuf::from("/bin/echo"),
            args: vec![
                "--model".into(),
                "model path with spaces.gguf".into(),
                "--flag=value".into(),
            ],
            host: "127.0.0.1".into(),
            port: 8081,
            ready_timeout_ms: 1_000,
            ready_poll_ms: 10,
        };

        assert_eq!(BackendProcess::command_arguments(&config), config.args);
    }

    #[test]
    fn unsupported_help_flags_are_dropped_with_their_values() {
        let args = vec![
            "--model".into(),
            "model path.gguf".into(),
            "--flash-attn".into(),
            "--ctx-size".into(),
            "4096".into(),
        ];
        let help = "Usage: llama-server --model FILE --ctx-size N";
        assert_eq!(
            super::filter_arguments_from_help(&args, help),
            vec!["--model", "model path.gguf", "--ctx-size", "4096"]
        );
    }

    #[test]
    fn empty_help_keeps_all_arguments_for_safe_fallback() {
        let args = vec!["--custom-flag".into(), "value".into()];
        assert_eq!(super::filter_arguments_from_help(&args, ""), args);
    }

    #[test]
    fn capability_probe_only_targets_real_llama_server_binary() {
        assert!(super::is_llama_server_binary(std::path::Path::new(
            "/opt/llama-server"
        )));
        assert!(!super::is_llama_server_binary(std::path::Path::new(
            "mock-llama-server"
        )));
    }

    #[test]
    fn backend_version_prefers_build_info_and_accepts_version_fallback() {
        assert_eq!(
            super::extract_backend_version(&serde_json::json!({"build_info":"b10621"})),
            Some("b10621".to_owned())
        );
        assert_eq!(
            super::extract_backend_version(&serde_json::json!({"version":"0.3.0"})),
            Some("0.3.0".to_owned())
        );
    }
}
