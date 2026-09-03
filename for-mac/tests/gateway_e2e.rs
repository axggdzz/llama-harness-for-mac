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
    assert_eq!(status["bindings"][0]["key"], "webui_same-session");
    let backend_pid = gateway.backend_pid().await.expect("running backend pid");
    let _ = shutdown_tx.send(());
    serving.await.unwrap();
    sleep(Duration::from_millis(50)).await;
    assert!(unsafe { libc::kill(backend_pid as libc::pid_t, 0) } != 0);
}
