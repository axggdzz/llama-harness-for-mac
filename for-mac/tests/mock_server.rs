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
async fn mock_server_serves_openai_json_and_sse() {
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

    let body = serde_json::json!({"model":"mock","messages":[{"role":"user","content":"hi"}]});
    let json = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&body)
        .send()
        .await
        .unwrap()
        .json::<serde_json::Value>()
        .await
        .unwrap();
    assert_eq!(json["object"], "chat.completion");

    let stream_body = serde_json::json!({"model":"mock","stream":true,"messages":[]});
    let stream = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&stream_body)
        .send()
        .await
        .unwrap()
        .bytes()
        .await
        .unwrap();
    let stream = String::from_utf8(stream.to_vec()).unwrap();
    assert!(stream.contains("data: {"));
    assert!(stream.contains("data: [DONE]\n\n"));

    let _ = child.kill().await;
    let _ = child.wait().await;
}
