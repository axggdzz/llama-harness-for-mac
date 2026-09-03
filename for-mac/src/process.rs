use crate::config::BackendConfig;
use anyhow::{anyhow, Context, Result};
use reqwest::Client;
use std::process::Stdio;
use std::sync::Arc;
use std::time::Duration;
use tokio::io::AsyncReadExt;
use tokio::process::{Child, Command};
use tokio::sync::Mutex;
use tokio::time::{sleep, timeout, Instant};

pub struct BackendProcess;

pub struct BackendHandle {
    child: Arc<Mutex<Option<Child>>>,
    config: BackendConfig,
    client: Client,
}

impl BackendProcess {
    pub fn command_arguments(config: &BackendConfig) -> Vec<String> {
        config.args.clone()
    }

    pub async fn start(config: BackendConfig) -> Result<BackendHandle> {
        let mut command = Command::new(&config.executable);
        command
            .args(Self::command_arguments(&config))
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
        if let Some(mut stderr) = child.stderr.take() {
            tokio::spawn(async move {
                let mut sink = Vec::new();
                let _ = stderr.read_to_end(&mut sink).await;
            });
        }

        Ok(BackendHandle {
            child: Arc::new(Mutex::new(Some(child))),
            config,
            client: Client::new(),
        })
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

    pub async fn wait_ready(&self) -> Result<()> {
        let deadline = Instant::now() + Duration::from_millis(self.config.ready_timeout_ms);
        let url = format!("{}/health", self.base_url());
        loop {
            if Instant::now() >= deadline {
                return Err(anyhow!(
                    "backend readiness timeout after {}ms",
                    self.config.ready_timeout_ms
                ));
            }

            if let Some(status) = self
                .client
                .get(&url)
                .send()
                .await
                .ok()
                .filter(|response| response.status().is_success())
            {
                if status
                    .json::<serde_json::Value>()
                    .await
                    .ok()
                    .and_then(|json| {
                        json.get("status")
                            .and_then(serde_json::Value::as_str)
                            .map(str::to_owned)
                    })
                    .as_deref()
                    == Some("ok")
                {
                    return Ok(());
                }
            }

            sleep(Duration::from_millis(self.config.ready_poll_ms)).await;
        }
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
}
