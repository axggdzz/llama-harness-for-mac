//! LlamaHarness macOS port entry point.

use llama_harness_mac::{
    config::{parse_backend_args, AppConfig},
    gateway::Gateway,
    instance::InstanceLock,
};
use std::{path::PathBuf, sync::Arc};

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    tracing_subscriber::fmt::init();
    let mut config = AppConfig::default();
    if let Ok(saved) = AppConfig::load_from(config.config_path()) {
        config = saved;
    }
    let _instance_lock = InstanceLock::acquire(config.data_dir.join("gateway.lock"))?;
    if let Ok(executable) = std::env::var("LLAMA_SERVER") {
        config.backend_executable = Some(PathBuf::from(executable));
    }
    if let Ok(port) = std::env::var("LLAMA_BACKEND_PORT") {
        if let Ok(port) = port.parse() {
            config.backend_port = port;
        }
    }
    if let Ok(args) = std::env::var("LLAMA_SERVER_ARGS") {
        config.backend_args = parse_backend_args(&args);
    }

    let gateway = Arc::new(Gateway::new(config.clone()));
    let address = format!("127.0.0.1:{}", config.gateway_port);
    let listener = tokio::net::TcpListener::bind(&address).await?;
    tracing::info!(%address, "LlamaHarness gateway listening");
    gateway
        .serve(listener, async {
            let _ = tokio::signal::ctrl_c().await;
        })
        .await
}
