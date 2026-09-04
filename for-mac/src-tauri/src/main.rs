#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use llama_harness_mac::{config::{apply_environment_overrides, parse_backend_args, AppConfig}, gateway::Gateway, instance::InstanceLock};
use std::{path::PathBuf, sync::Arc};
use tauri::{Manager, RunEvent};
use tokio::sync::oneshot;

fn main() {
    let mut config = AppConfig::default();
    apply_environment_overrides(&mut config);
    if let Ok(saved) = AppConfig::load_from(config.config_path()) {
        config = saved;
    }
    apply_environment_overrides(&mut config);
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
    let _instance_lock = match InstanceLock::acquire(config.data_dir.join("gateway.lock")) {
        Ok(lock) => lock,
        Err(error) => {
            eprintln!("{error}");
            return;
        }
    };
    let gateway = Arc::new(Gateway::new(config.clone()));
    let (shutdown_tx, shutdown_rx) = oneshot::channel();
    let address = format!("127.0.0.1:{}", config.gateway_port);
    if let Ok(listener) = tauri::async_runtime::block_on(tokio::net::TcpListener::bind(&address)) {
        let gateway_task = gateway.clone();
        tauri::async_runtime::spawn(async move {
            if let Err(error) = gateway_task.serve(listener, async {
                let _ = shutdown_rx.await;
            }).await {
                eprintln!("gateway stopped: {error}");
            }
        });
    } else {
        eprintln!("gateway address {address} is already in use");
        return;
    }
    let mut shutdown_tx = Some(shutdown_tx);
    tauri::Builder::default()
        .menu(|handle| {
            tauri::menu::MenuBuilder::new(handle)
                .text("refresh", "刷新")
                .text("about", "关于 LlamaHarness")
                .build()
        })
        .on_menu_event(|app, event| {
            if event.id().as_ref() == "refresh" {
                if let Some(window) = app.get_webview_window("main") {
                    let _ = window.eval("window.refresh && window.refresh()") ;
                }
            }
        })
        .build(tauri::generate_context!())
        .expect("error while building LlamaHarness UI")
        .run(move |_app, event| {
            if matches!(event, RunEvent::ExitRequested { .. } | RunEvent::Exit) {
                if let Some(sender) = shutdown_tx.take() {
                    let _ = sender.send(());
                }
            }
        });
}
