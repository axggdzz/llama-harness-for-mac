use llama_harness_mac::config::BackendConfig;
use llama_harness_mac::process::BackendProcess;
use std::path::PathBuf;
use tokio::time::{sleep, Duration};

#[tokio::test]
async fn process_manager_waits_for_readiness_and_stops_group() {
    let config = BackendConfig {
        executable: PathBuf::from(env!("CARGO_BIN_EXE_mock-llama-server")),
        args: vec![
            "--port".into(),
            "18081".into(),
            "--startup-delay-ms".into(),
            "100".into(),
        ],
        host: "127.0.0.1".into(),
        port: 18081,
        ready_timeout_ms: 5_000,
        ready_poll_ms: 20,
    };
    let handle = BackendProcess::start(config).await.unwrap();
    let pid = handle.pid().expect("child pid");
    handle.wait_ready().await.unwrap();
    assert!(handle.base_url().ends_with(":18081"));
    handle.stop().await.unwrap();
    sleep(Duration::from_millis(50)).await;
    assert!(unsafe { libc::kill(pid as libc::pid_t, 0) } != 0);
}

#[tokio::test]
async fn readiness_reports_an_early_backend_exit() {
    let config = BackendConfig {
        executable: PathBuf::from("/usr/bin/true"),
        args: Vec::new(),
        host: "127.0.0.1".into(),
        port: 18999,
        ready_timeout_ms: 5_000,
        ready_poll_ms: 20,
    };
    let handle = BackendProcess::start(config).await.unwrap();
    let error = handle.wait_ready().await.unwrap_err().to_string();
    assert!(error.contains("exited before readiness"));
    handle.stop().await.unwrap();
}
