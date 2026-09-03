use llama_harness_mac::kv_cache::KvCacheManager;
use reqwest::Client;
use std::net::TcpListener;
use std::process::Stdio;
use tokio::process::{Child, Command};
use tokio::time::{sleep, Duration};

fn free_port() -> u16 {
    TcpListener::bind(("127.0.0.1", 0))
        .unwrap()
        .local_addr()
        .unwrap()
        .port()
}

async fn start_mock(port: u16) -> Child {
    Command::new(env!("CARGO_BIN_EXE_mock-llama-server"))
        .args(["--port", &port.to_string()])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .unwrap()
}

#[tokio::test]
async fn mock_backend_supports_kv_save_restore_erase_and_clear() {
    let port = free_port();
    let mut child = start_mock(port).await;
    let client = Client::new();
    let base = format!("http://127.0.0.1:{port}");
    for _ in 0..50 {
        if client.get(format!("{base}/health")).send().await.is_ok() {
            break;
        }
        sleep(Duration::from_millis(20)).await;
    }
    let dir = tempfile::tempdir().unwrap();
    let cache = dir.path().join("cache");
    let manager = KvCacheManager::new(
        client,
        base,
        &cache,
        dir.path().join("index.json"),
        2,
        Some(4096),
    );
    let saved = manager.save(0, "conversation").await.unwrap();
    assert_eq!(saved.snapshot.unwrap().saved_tokens, 3);
    assert!(manager.restore(1, "conversation").await.is_ok());
    assert!(manager.erase(1).await.is_ok());
    assert_eq!(manager.clear_all().await.unwrap(), 1);
    assert!(manager.snapshot().is_empty());
    let _ = child.kill().await;
    let _ = child.wait().await;
}
