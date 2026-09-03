use crate::{
    config::AppConfig,
    lifecycle::LifecyclePhase,
    process::{BackendHandle, BackendProcess},
};
use anyhow::{anyhow, Result};
use axum::{
    body::{to_bytes, Body},
    extract::State,
    http::{header, HeaderName, HeaderValue, Request, StatusCode},
    response::{IntoResponse, Response},
    routing::{any, get},
    Json, Router,
};
use reqwest::Client;
use serde::Serialize;
use std::sync::Arc;
use tokio::{
    net::TcpListener,
    sync::{watch, Mutex, RwLock},
};

#[derive(Clone)]
pub struct Gateway {
    inner: Arc<GatewayInner>,
}

struct GatewayInner {
    config: AppConfig,
    client: Client,
    phase: RwLock<LifecyclePhase>,
    backend: Mutex<Option<Arc<BackendHandle>>>,
    startup_in_progress: Mutex<bool>,
    startup_changed: watch::Sender<u64>,
}

#[derive(Debug, Serialize)]
struct StatusResponse {
    phase: LifecyclePhase,
    backend_ready: bool,
    backend_port: u16,
}

impl Gateway {
    pub fn new(config: AppConfig) -> Self {
        let (startup_changed, _) = watch::channel(0_u64);
        Self {
            inner: Arc::new(GatewayInner {
                config,
                client: Client::new(),
                phase: RwLock::new(LifecyclePhase::Standby),
                backend: Mutex::new(None),
                startup_in_progress: Mutex::new(false),
                startup_changed,
            }),
        }
    }

    pub fn router(self: Arc<Self>) -> Router {
        Router::new()
            .route("/__status__", get(status))
            .route("/health", get(gateway_health))
            .route("/v1/*path", any(proxy))
            .with_state(self)
    }

    pub async fn serve(
        self: Arc<Self>,
        listener: TcpListener,
        shutdown: impl std::future::Future<Output = ()> + Send + 'static,
    ) -> Result<()> {
        let result = axum::serve(listener, self.clone().router())
            .with_graceful_shutdown(shutdown)
            .await;
        self.shutdown().await?;
        result.map_err(|error| anyhow!(error))
    }

    pub async fn shutdown(&self) -> Result<()> {
        let backend = self.inner.backend.lock().await.take();
        if let Some(backend) = backend {
            backend.stop().await?;
        }
        *self.inner.phase.write().await = LifecyclePhase::Standby;
        Ok(())
    }

    pub async fn backend_pid(&self) -> Option<u32> {
        self.inner
            .backend
            .lock()
            .await
            .as_ref()
            .and_then(|backend| backend.pid())
    }

    async fn ensure_backend(&self) -> Result<Arc<BackendHandle>> {
        loop {
            if let Some(backend) = self.inner.backend.lock().await.clone() {
                return Ok(backend);
            }

            let mut changed = self.inner.startup_changed.subscribe();
            let mut in_progress = self.inner.startup_in_progress.lock().await;
            if *in_progress {
                drop(in_progress);
                let _ = changed.changed().await;
                continue;
            }
            *in_progress = true;
            drop(in_progress);

            *self.inner.phase.write().await = LifecyclePhase::Waking;
            let result = self.start_backend().await;
            match result {
                Ok(backend) => {
                    *self.inner.backend.lock().await = Some(backend.clone());
                    *self.inner.phase.write().await = LifecyclePhase::Running;
                    self.finish_startup().await;
                    return Ok(backend);
                }
                Err(error) => {
                    *self.inner.phase.write().await = LifecyclePhase::Standby;
                    self.finish_startup().await;
                    return Err(error);
                }
            }
        }
    }

    async fn start_backend(&self) -> Result<Arc<BackendHandle>> {
        let config = self
            .inner
            .config
            .backend_config()
            .ok_or_else(|| anyhow!("backend executable is not configured"))?;
        let handle = Arc::new(BackendProcess::start(config).await?);
        if let Err(error) = handle.wait_ready().await {
            let _ = handle.stop().await;
            return Err(error);
        }
        Ok(handle)
    }

    async fn finish_startup(&self) {
        *self.inner.startup_in_progress.lock().await = false;
        self.inner
            .startup_changed
            .send_modify(|generation| *generation += 1);
    }

    async fn status(&self) -> StatusResponse {
        StatusResponse {
            phase: *self.inner.phase.read().await,
            backend_ready: self.inner.backend.lock().await.is_some(),
            backend_port: self.inner.config.backend_port,
        }
    }
}

async fn status(State(gateway): State<Arc<Gateway>>) -> impl IntoResponse {
    Json(gateway.status().await)
}

async fn gateway_health(State(gateway): State<Arc<Gateway>>) -> Response {
    let status = gateway.status().await;
    if status.backend_ready {
        Json(status).into_response()
    } else {
        (StatusCode::SERVICE_UNAVAILABLE, Json(status)).into_response()
    }
}

async fn proxy(State(gateway): State<Arc<Gateway>>, request: Request<Body>) -> Response {
    let backend = match gateway.ensure_backend().await {
        Ok(backend) => backend,
        Err(error) => return error_response(StatusCode::BAD_GATEWAY, error.to_string()),
    };

    let path = request
        .uri()
        .path_and_query()
        .map(|value| value.as_str())
        .unwrap_or("/v1");
    let url = format!("{}{}", backend.base_url(), path);
    let method = request.method().clone();
    let headers = request.headers().clone();
    let body = match to_bytes(request.into_body(), 16 * 1024 * 1024).await {
        Ok(body) => body,
        Err(error) => return error_response(StatusCode::BAD_REQUEST, error.to_string()),
    };

    let mut builder = gateway.inner.client.request(method, url).body(body);
    for (name, value) in &headers {
        if *name != header::HOST && *name != header::CONTENT_LENGTH {
            builder = builder.header(name, value);
        }
    }
    let response = match builder.send().await {
        Ok(response) => response,
        Err(error) => {
            *gateway.inner.backend.lock().await = None;
            *gateway.inner.phase.write().await = LifecyclePhase::Standby;
            return error_response(StatusCode::BAD_GATEWAY, error.to_string());
        }
    };

    let mut output = Response::builder().status(response.status());
    for (name, value) in response.headers() {
        if is_hop_by_hop(name) {
            continue;
        }
        output = output.header(name, value);
    }
    match output.body(Body::from_stream(response.bytes_stream())) {
        Ok(response) => response,
        Err(error) => error_response(StatusCode::INTERNAL_SERVER_ERROR, error.to_string()),
    }
}

fn is_hop_by_hop(name: &HeaderName) -> bool {
    matches!(
        name.as_str(),
        "connection"
            | "keep-alive"
            | "proxy-authenticate"
            | "proxy-authorization"
            | "te"
            | "trailer"
            | "transfer-encoding"
            | "upgrade"
    )
}

fn error_response(status: StatusCode, message: String) -> Response {
    let mut response = (
        status,
        Json(serde_json::json!({"error": {"message": message}})),
    )
        .into_response();
    response.headers_mut().insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static("application/json"),
    );
    response
}
