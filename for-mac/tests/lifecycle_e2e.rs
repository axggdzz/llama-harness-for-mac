use llama_harness_mac::{config::AppConfig, gateway::Gateway, lifecycle::LifecyclePhase};
use reqwest::Client;
use std::{path::PathBuf, sync::Arc};
use tokio::{
    sync::oneshot,
    time::{sleep, Duration},
};

fn config(port: u16) -> AppConfig {
    let mut config = AppConfig::default();
    config.backend_executable = Some(PathBuf::from(env!("CARGO_BIN_EXE_mock-llama-server")));
    config.backend_args = vec!["--port".into(), port.to_string()];
    config.backend_port = port;
    config.ready_timeout_ms = 5_000;
    config.ready_poll_ms = 10;
    config
}

async fn start_gateway(
    config: AppConfig,
) -> (
    Arc<Gateway>,
    String,
    oneshot::Sender<()>,
    tokio::task::JoinHandle<()>,
) {
    let gateway = Arc::new(Gateway::new(config));
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = format!("http://{}", listener.local_addr().unwrap());
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
    (gateway, address, shutdown_tx, task)
}

#[tokio::test]
async fn warming_phase_is_visible_before_running() {
    let mut config = config(18084);
    config.warming_delay_ms = 150;
    let (_gateway, base, shutdown, task) = start_gateway(config).await;
    let client = Client::new();
    let request = client
        .post(format!("{base}/v1/chat/completions"))
        .json(&serde_json::json!({"model":"mock","messages":[]}));
    let response_task = tokio::spawn(async move { request.send().await.unwrap().status() });
    let mut saw_warming = false;
    for _ in 0..50 {
        if let Ok(response) = client.get(format!("{base}/__status__")).send().await {
            if response.json::<serde_json::Value>().await.unwrap()["phase"]
                == serde_json::to_value(LifecyclePhase::Warming).unwrap()
            {
                saw_warming = true;
                break;
            }
        }
        sleep(Duration::from_millis(10)).await;
    }
    assert!(saw_warming);
    assert_eq!(response_task.await.unwrap(), 200);
    let _ = shutdown.send(());
    task.await.unwrap();
}

#[tokio::test]
async fn idle_monitor_sleeps_backend_after_quiet_period() {
    let mut config = config(18085);
    config.idle_timeout_ms = 80;
    config.sleep_observe_ms = 40;
    let (gateway, base, shutdown, task) = start_gateway(config).await;
    let client = Client::new();
    assert_eq!(
        client
            .post(format!("{base}/v1/chat/completions"))
            .json(&serde_json::json!({"model":"mock","messages":[]}))
            .send()
            .await
            .unwrap()
            .status(),
        200
    );
    let pid = loop {
        if let Some(pid) = gateway.backend_pid().await {
            break pid;
        }
        sleep(Duration::from_millis(10)).await;
    };
    let mut standby = false;
    for _ in 0..100 {
        let status = client
            .get(format!("{base}/__status__"))
            .send()
            .await
            .unwrap()
            .json::<serde_json::Value>()
            .await
            .unwrap();
        if status["phase"] == serde_json::to_value(LifecyclePhase::Standby).unwrap() {
            standby = true;
            break;
        }
        sleep(Duration::from_millis(20)).await;
    }
    assert!(standby);
    assert!(unsafe { libc::kill(pid as libc::pid_t, 0) } != 0);
    let _ = shutdown.send(());
    task.await.unwrap();
}

#[tokio::test]
async fn zero_idle_timeout_disables_automatic_sleep() {
    let mut config = config(18087);
    config.idle_timeout_ms = 0;
    config.sleep_observe_ms = 20;
    let (gateway, base, shutdown, task) = start_gateway(config).await;
    let client = Client::new();
    assert_eq!(
        client
            .post(format!("{base}/v1/chat/completions"))
            .json(&serde_json::json!({"model":"mock","messages":[]}))
            .send()
            .await
            .unwrap()
            .status(),
        200
    );
    sleep(Duration::from_millis(150)).await;
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
    assert!(gateway.backend_pid().await.is_some());
    let _ = shutdown.send(());
    task.await.unwrap();
}

#[tokio::test]
async fn request_during_sleep_observation_cancels_sleep() {
    let mut config = config(18086);
    config.idle_timeout_ms = 60;
    config.sleep_observe_ms = 300;
    let (_gateway, base, shutdown, task) = start_gateway(config).await;
    let client = Client::new();
    let payload = serde_json::json!({"model":"mock","messages":[]});
    assert_eq!(
        client
            .post(format!("{base}/v1/chat/completions"))
            .json(&payload)
            .send()
            .await
            .unwrap()
            .status(),
        200
    );
    let mut saw_sleeping = false;
    for _ in 0..100 {
        let status = client
            .get(format!("{base}/__status__"))
            .send()
            .await
            .unwrap()
            .json::<serde_json::Value>()
            .await
            .unwrap();
        if status["phase"] == serde_json::to_value(LifecyclePhase::Sleeping).unwrap() {
            saw_sleeping = true;
            break;
        }
        sleep(Duration::from_millis(10)).await;
    }
    assert!(saw_sleeping);
    assert_eq!(
        client
            .post(format!("{base}/v1/chat/completions"))
            .json(&payload)
            .send()
            .await
            .unwrap()
            .status(),
        200
    );
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
    let _ = shutdown.send(());
    task.await.unwrap();
}
