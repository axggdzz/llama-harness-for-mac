use llama_harness_mac::{config::AppConfig, gateway::Gateway};
use reqwest::Client;
use std::{path::PathBuf, sync::Arc};
use tokio::sync::oneshot;

fn config(backend_port: u16, context_size: usize) -> AppConfig {
    let mut config = AppConfig::default();
    config.backend_executable = Some(PathBuf::from(env!("CARGO_BIN_EXE_mock-llama-server")));
    config.backend_args = vec!["--port".into(), backend_port.to_string()];
    config.backend_port = backend_port;
    config.ready_timeout_ms = 5_000;
    config.ready_poll_ms = 10;
    config.token_guard_enabled = true;
    config.context_size = Some(context_size);
    config.reserved_output_tokens = 0;
    config.reserved_prompt_overhead = 0;
    config
}

async fn start(
    config: AppConfig,
) -> (
    Arc<Gateway>,
    String,
    oneshot::Sender<()>,
    tokio::task::JoinHandle<()>,
) {
    let gateway = Arc::new(Gateway::new(config));
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let base = format!("http://{}", listener.local_addr().unwrap());
    let (shutdown_tx, shutdown_rx) = oneshot::channel();
    let task = {
        let gateway = gateway.clone();
        tokio::spawn(async move {
            gateway
                .serve(listener, async {
                    let _ = shutdown_rx.await;
                })
                .await
                .unwrap();
        })
    };
    (gateway, base, shutdown_tx, task)
}

async fn backend_port() -> u16 {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = listener.local_addr().unwrap().port();
    drop(listener);
    port
}

#[tokio::test]
async fn gateway_applies_token_guard_before_forwarding() {
    let (gateway, base, shutdown, task) = start(config(backend_port().await, 500)).await;
    let client = Client::new();
    let payload = serde_json::json!({
        "model": "mock",
        "messages": [
            {"role":"system", "content":"rules"},
            {"role":"user", "content":"old question ".repeat(20)},
            {"role":"assistant", "content":"old answer ".repeat(20)},
            {"role":"user", "content":"latest ".repeat(120)}
        ]
    });
    let response = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&payload)
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), 200);
    let body = response.json::<serde_json::Value>().await.unwrap();
    assert!(body["x_prompt_chars"].as_u64().unwrap() <= 500);
    assert!(body["x_prompt_chars"].as_u64().unwrap() < 1_000);
    let _ = shutdown.send(());
    task.await.unwrap();
    assert!(gateway.backend_pid().await.is_none());
}

#[tokio::test]
async fn gateway_returns_structured_400_when_token_guard_cannot_fit() {
    let (_gateway, base, shutdown, task) = start(config(backend_port().await, 1)).await;
    let client = Client::new();
    let response = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&serde_json::json!({"model":"mock", "messages":[{"role":"user", "content":"too large"}]}))
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), 400);
    let body = response.json::<serde_json::Value>().await.unwrap();
    assert_eq!(body["error"]["code"], "token_guard_over_budget");
    assert_eq!(body["error"]["token_guard"], true);
    assert!(body["error"]["budget"].as_u64().is_some());
    let _ = shutdown.send(());
    task.await.unwrap();
}

#[tokio::test]
async fn gateway_retries_once_after_context_overflow_and_erases_slot() {
    let backend_port = backend_port().await;
    let mut config = config(backend_port, 10_000);
    config.slot_count = 2;
    config.backend_args.push("--overflow-once".into());
    let (_gateway, base, shutdown, task) = start(config).await;
    let client = Client::new();
    let response = client
        .post(format!("{base}/v1/chat/completions"))
        .header("x-conversation-id", "recover-session")
        .json(&serde_json::json!({"model":"mock", "messages":[{"role":"user", "content":"hello"}]}))
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), 200);
    assert_eq!(
        response.json::<serde_json::Value>().await.unwrap()["object"],
        "chat.completion"
    );
    let _ = shutdown.send(());
    task.await.unwrap();
}

#[tokio::test]
async fn gateway_continues_one_length_terminated_sse_round() {
    let backend_port = backend_port().await;
    let mut config = config(backend_port, 10_000);
    config.token_guard_enabled = false;
    config.backend_args.push("--length-once".into());
    config.max_continuations = 1;
    let (_gateway, base, shutdown, task) = start(config).await;
    let client = Client::new();
    let response = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&serde_json::json!({"model":"mock", "stream":true, "messages":[{"role":"user", "content":"hello"}]}))
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), 200);
    let text = response.text().await.unwrap();
    assert!(text.contains("mock"));
    assert!(!text.contains("\"finish_reason\":\"length\""));
    assert!(text.contains("data: [DONE]"));
    let _ = shutdown.send(());
    task.await.unwrap();
}

#[tokio::test]
async fn gateway_applies_thinking_mode_command_and_fields() {
    let backend_port = backend_port().await;
    let mut config = config(backend_port, 10_000);
    config.token_guard_enabled = false;
    let (_gateway, base, shutdown, task) = start(config).await;
    let client = Client::new();
    let response = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&serde_json::json!({
            "model":"mock",
            "thinking":true,
            "reasoning_effort":"high",
            "chat_template_kwargs":{"enable_thinking":true,"reasoning_effort":"high"},
            "messages":[{"role":"user","content":"开启中度推理模式\n分析这个问题"}]
        }))
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), 200);
    let body = response.json::<serde_json::Value>().await.unwrap();
    assert_eq!(body["x_chat_template_kwargs"]["enable_thinking"], true);
    assert_eq!(body["x_chat_template_kwargs"]["reasoning_effort"], "medium");
    let _ = shutdown.send(());
    task.await.unwrap();
}

#[tokio::test]
async fn gateway_ejects_oom_backend_and_next_request_restarts_it() {
    let backend_port = backend_port().await;
    let mut config = config(backend_port, 10_000);
    config.token_guard_enabled = false;
    let marker_dir = tempfile::tempdir().unwrap();
    config.backend_args.extend([
        "--oom-marker".into(),
        marker_dir
            .path()
            .join("oom.marker")
            .to_string_lossy()
            .into_owned(),
    ]);
    let (gateway, base, shutdown, task) = start(config).await;
    let client = Client::new();
    let payload =
        serde_json::json!({"model":"mock", "messages":[{"role":"user", "content":"hello"}]});
    let first = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&payload)
        .send()
        .await
        .unwrap();
    assert_eq!(first.status(), 503);
    assert!(gateway.backend_pid().await.is_none());
    let second = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&payload)
        .send()
        .await
        .unwrap();
    assert_eq!(second.status(), 200);
    let _ = shutdown.send(());
    task.await.unwrap();
}
