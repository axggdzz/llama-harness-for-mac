use llama_harness_mac::{config::AppConfig, gateway::Gateway, lifecycle::LifecyclePhase};
use reqwest::Client;
use std::{path::PathBuf, sync::Arc};
use tokio::{
    sync::oneshot,
    time::{sleep, Duration},
};

#[tokio::test]
async fn gateway_starts_backend_for_json_and_streaming_requests() {
    let mut config = AppConfig::default();
    config.backend_executable = Some(PathBuf::from(env!("CARGO_BIN_EXE_mock-llama-server")));
    config.backend_args = vec![
        "--port".into(),
        "18082".into(),
        "--startup-delay-ms".into(),
        "100".into(),
        "--stderr-line".into(),
        "gateway backend diagnostic".into(),
    ];
    config.backend_port = 18082;
    config.ready_timeout_ms = 5_000;
    config.ready_poll_ms = 20;
    config.slot_count = 2;
    config.slot_bindings_path = Some(tempfile::tempdir().unwrap().path().join("bindings.json"));

    let gateway = Arc::new(Gateway::new(config));
    let listener = tokio::net::TcpListener::bind("127.0.0.1:8080")
        .await
        .unwrap();
    let (shutdown_tx, shutdown_rx) = oneshot::channel();
    let serving = {
        let gateway = gateway.clone();
        tokio::spawn(async move {
            gateway
                .serve(listener, async {
                    let _ = shutdown_rx.await;
                })
                .await
                .unwrap()
        })
    };
    let client = Client::new();
    let base = "http://127.0.0.1:8080";
    for _ in 0..50 {
        if client
            .get(format!("{base}/__status__"))
            .send()
            .await
            .is_ok()
        {
            break;
        }
        sleep(Duration::from_millis(10)).await;
    }

    let payload =
        serde_json::json!({"model":"mock","messages":[{"role":"user","content":"hello"}]});
    let response = client
        .post(format!("{base}/v1/chat/completions"))
        .header("x-conversation-id", "same-session")
        .json(&payload)
        .send()
        .await
        .unwrap();
    assert_eq!(response.status(), 200);
    let response_json = response.json::<serde_json::Value>().await.unwrap();
    assert_eq!(response_json["object"], "chat.completion");
    assert_eq!(response_json["x_n_slots"], 0);

    let explicit = client
        .post(format!("{base}/v1/chat/completions"))
        .header("x-conversation-id", "same-session")
        .json(&serde_json::json!({"model":"mock","n_slots":1,"messages":[]}))
        .send()
        .await
        .unwrap()
        .json::<serde_json::Value>()
        .await
        .unwrap();
    assert_eq!(explicit["x_n_slots"], 1);

    let stream_payload = serde_json::json!({"model":"mock","stream":true,"messages":[]});
    let stream = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&stream_payload)
        .send()
        .await
        .unwrap()
        .bytes()
        .await
        .unwrap();
    let stream = String::from_utf8(stream.to_vec()).unwrap();
    assert!(stream.ends_with("data: [DONE]\n\n"));

    let status = client
        .get(format!("{base}/__status__"))
        .send()
        .await
        .unwrap()
        .json::<serde_json::Value>()
        .await
        .unwrap();
    assert_eq!(
        status["phase"],
        serde_json::to_value(LifecyclePhase::Running).unwrap()
    );
    assert!(status["runtime_seconds"].is_number());
    assert_eq!(status["thinking_mode"], "off");
    assert_eq!(status["crash_count"], 0);
    assert_eq!(status["bindings"][0]["key"], "webui_same-session");
    let stats = client
        .get(format!("{base}/__stats__"))
        .send()
        .await
        .unwrap()
        .json::<serde_json::Value>()
        .await
        .unwrap();
    assert!(stats["requests"].as_u64().unwrap() >= 3);
    assert!(stats["prompt_tokens"].as_u64().unwrap() >= 1);
    assert!(stats["completion_tokens"].as_u64().unwrap() >= 2);
    let logs = client
        .get(format!("{base}/__logs__?kind=main&max_bytes=4096"))
        .send()
        .await
        .unwrap()
        .text()
        .await
        .unwrap();
    assert!(logs.contains("request POST /v1/chat/completions"));
    let backend_logs = client
        .get(format!("{base}/__logs__?kind=backend&max_bytes=4096"))
        .send()
        .await
        .unwrap()
        .text()
        .await
        .unwrap();
    assert!(backend_logs.contains("gateway backend diagnostic"));
    let snapshots = client
        .get(format!("{base}/__kv__"))
        .send()
        .await
        .unwrap()
        .json::<serde_json::Value>()
        .await
        .unwrap();
    assert!(snapshots.is_array());
    let resources = client
        .get(format!("{base}/__resources__"))
        .send()
        .await
        .unwrap()
        .json::<serde_json::Value>()
        .await
        .unwrap();
    let capabilities = client
        .get(format!("{base}/__capabilities__"))
        .send()
        .await
        .unwrap()
        .json::<serde_json::Value>()
        .await
        .unwrap();
    assert_eq!(capabilities["props"], true);
    assert_eq!(capabilities["slots"], true);
    assert_eq!(capabilities["metrics"], true);
    assert_eq!(capabilities["tokenize"], true);
    assert!(capabilities["degradations"].as_array().unwrap().len() >= 1);
    assert!(resources.get("gpu_backend").is_some());
    let metrics = client
        .get(format!("{base}/__backend/metrics"))
        .send()
        .await
        .unwrap();
    assert_eq!(metrics.status(), 200);
    assert!(metrics
        .text()
        .await
        .unwrap()
        .contains("mock_requests_total"));
    for path in ["slots", "props"] {
        let response = client
            .get(format!("{base}/__backend/{path}"))
            .send()
            .await
            .unwrap();
        assert_eq!(response.status(), 200);
    }
    let backend_pid = gateway.backend_pid().await.expect("running backend pid");
    let _ = shutdown_tx.send(());
    serving.await.unwrap();
    sleep(Duration::from_millis(50)).await;
    assert!(unsafe { libc::kill(backend_pid as libc::pid_t, 0) } != 0);
}

#[tokio::test]
async fn control_endpoints_wake_and_stop_backend() {
    let mut config = AppConfig::default();
    config.backend_executable = Some(PathBuf::from(env!("CARGO_BIN_EXE_mock-llama-server")));
    config.backend_args = vec!["--port".into(), "18083".into()];
    config.backend_port = 18083;
    config.ready_timeout_ms = 5_000;
    config.ready_poll_ms = 20;
    config.data_dir = tempfile::tempdir().unwrap().path().to_path_buf();
    let gateway = Arc::new(Gateway::new(config));
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let (shutdown_tx, shutdown_rx) = oneshot::channel();
    let serving = {
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
    let client = Client::new();
    let base = format!("http://{address}");
    let wake = client
        .post(format!("{base}/__control/wake"))
        .send()
        .await
        .unwrap();
    assert_eq!(wake.status(), 200);
    assert_eq!(
        wake.json::<serde_json::Value>().await.unwrap()["backend_ready"],
        true
    );

    let stop = client
        .post(format!("{base}/__control/stop"))
        .send()
        .await
        .unwrap();
    assert_eq!(stop.status(), 200);
    assert_eq!(
        stop.json::<serde_json::Value>().await.unwrap()["backend_ready"],
        false
    );
    let _ = shutdown_tx.send(());
    serving.await.unwrap();
}

#[tokio::test]
async fn slot_eviction_saves_previous_binding_kv_snapshot() {
    let data_dir = tempfile::tempdir().unwrap();
    let mut config = AppConfig::default();
    config.backend_executable = Some(PathBuf::from(env!("CARGO_BIN_EXE_mock-llama-server")));
    config.backend_args = vec!["--port".into(), "18084".into()];
    config.backend_port = 18084;
    config.slot_count = 2;
    config.data_dir = data_dir.path().to_path_buf();
    config.ready_timeout_ms = 5_000;
    let gateway = Arc::new(Gateway::new(config));
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let (shutdown_tx, shutdown_rx) = oneshot::channel();
    let serving = {
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
    let client = Client::new();
    let base = format!("http://{address}");
    for key in ["one", "two", "three"] {
        let response = client
            .post(format!("{base}/v1/chat/completions"))
            .header("x-conversation-id", key)
            .json(&serde_json::json!({"model":"mock","messages":[]}))
            .send()
            .await
            .unwrap();
        assert_eq!(response.status(), 200);
    }
    let snapshots = client
        .get(format!("{base}/__kv__"))
        .send()
        .await
        .unwrap()
        .json::<serde_json::Value>()
        .await
        .unwrap();
    assert!(
        snapshots
            .as_array()
            .unwrap()
            .iter()
            .any(|item| item["key"] == "webui_one" || item["key"] == "webui_two"),
        "snapshots={snapshots}"
    );
    let _ = shutdown_tx.send(());
    serving.await.unwrap();
}
