use crate::{
    config::AppConfig,
    kv_cache::KvCacheManager,
    lifecycle::LifecyclePhase,
    observability::{LogKind, RotatingLogger, Stats},
    process::{BackendHandle, BackendProcess},
    resources::ResourceSnapshot,
    slot_affinity::{SlotAffinity, SlotBinding},
    thinking,
    token_guard::{GuardError, TokenGuard, TokenGuardConfig},
};
use anyhow::{anyhow, Result};
use axum::{
    body::{to_bytes, Body},
    extract::{Query, State},
    http::{header, HeaderName, HeaderValue, Request, StatusCode},
    response::{IntoResponse, Response},
    routing::{any, get, post},
    Json, Router,
};
use futures_util::{Stream, StreamExt};
use reqwest::Client;
use serde::{Deserialize, Serialize};
use std::{
    pin::Pin,
    sync::{
        atomic::{AtomicBool, AtomicUsize, Ordering},
        Arc,
    },
    task::{Context, Poll},
    time::Duration,
};
use tokio::{
    net::TcpListener,
    sync::{watch, Mutex, Notify, RwLock},
    time::{sleep, Instant},
};
use tower_http::cors::CorsLayer;

#[derive(Clone)]
pub struct Gateway {
    inner: Arc<GatewayInner>,
}

struct GatewayInner {
    config: AppConfig,
    affinity: Option<Arc<SlotAffinity>>,
    client: Client,
    phase: RwLock<LifecyclePhase>,
    backend: Mutex<Option<Arc<BackendHandle>>>,
    startup_in_progress: Mutex<bool>,
    startup_changed: watch::Sender<u64>,
    inflight: AtomicUsize,
    last_activity: Mutex<Instant>,
    sleep_cancel: Arc<Notify>,
    monitor_stop: Arc<Notify>,
    request_gate: Mutex<()>,
    thinking_mode: Mutex<thinking::ThinkingMode>,
    crash_count: AtomicUsize,
    logger: Option<Arc<RotatingLogger>>,
    stats: Arc<Stats>,
    kv: Arc<KvCacheManager>,
}

#[derive(Debug, Serialize)]
struct StatusResponse {
    phase: LifecyclePhase,
    backend_ready: bool,
    backend_port: u16,
    inflight: usize,
    bindings: Vec<BindingStatus>,
}

#[derive(Debug, Serialize)]
struct BindingStatus {
    key: String,
    app: String,
    slot: usize,
    preemptive: bool,
    kv_cache: bool,
}

impl Gateway {
    pub fn new(config: AppConfig) -> Self {
        let (startup_changed, _) = watch::channel(0_u64);
        let initial_thinking_mode = config.thinking_mode;
        let affinity = if config.slot_count > 1 {
            let path = config
                .slot_bindings_path
                .clone()
                .unwrap_or_else(|| config.data_dir.join("slot_bindings.json"));
            Some(Arc::new(SlotAffinity::new(config.slot_count, path)))
        } else {
            None
        };
        let log_dir = config
            .log_dir
            .clone()
            .unwrap_or_else(|| config.data_dir.join("logs"));
        let logger = RotatingLogger::new(log_dir, config.log_max_bytes)
            .ok()
            .map(Arc::new);
        let backend_base = format!("http://{}:{}", config.backend_host, config.backend_port);
        let kv = Arc::new(KvCacheManager::new(
            Client::new(),
            backend_base,
            config.data_dir.join("kv"),
            config.data_dir.join("kv_cache_index.json"),
            config.slot_count,
            config.context_size.map(|value| value as u64),
        ));
        Self {
            inner: Arc::new(GatewayInner {
                config,
                affinity,
                client: Client::new(),
                phase: RwLock::new(LifecyclePhase::Standby),
                backend: Mutex::new(None),
                startup_in_progress: Mutex::new(false),
                startup_changed,
                inflight: AtomicUsize::new(0),
                last_activity: Mutex::new(Instant::now()),
                sleep_cancel: Arc::new(Notify::new()),
                monitor_stop: Arc::new(Notify::new()),
                request_gate: Mutex::new(()),
                thinking_mode: Mutex::new(initial_thinking_mode),
                crash_count: AtomicUsize::new(0),
                logger,
                stats: Arc::new(Stats::default()),
                kv,
            }),
        }
    }

    pub fn router(self: Arc<Self>) -> Router {
        Router::new()
            .route("/__status__", get(status))
            .route("/__config__", get(config).put(update_config))
            .route("/__control/wake", post(wake_backend))
            .route("/__control/stop", post(stop_backend))
            .route("/health", get(gateway_health))
            .route("/__stats__", get(stats))
            .route("/__logs__", get(logs))
            .route("/__kv__", get(kv_snapshots))
            .route("/__kv/save", post(kv_save))
            .route("/__kv/restore", post(kv_restore))
            .route("/__kv/erase", post(kv_erase))
            .route("/__kv/clear", post(kv_clear))
            .route("/__resources__", get(resources))
            .route("/__backend/slots", get(backend_slots))
            .route("/__backend/props", get(backend_props))
            .route("/__backend/metrics", get(backend_metrics))
            .route("/v1/*path", any(proxy))
            .layer(CorsLayer::very_permissive())
            .with_state(self)
    }

    pub async fn serve(
        self: Arc<Self>,
        listener: TcpListener,
        shutdown: impl std::future::Future<Output = ()> + Send + 'static,
    ) -> Result<()> {
        let monitor = tokio::spawn(self.clone().idle_monitor());
        let result = axum::serve(listener, self.clone().router())
            .with_graceful_shutdown(shutdown)
            .await;
        self.inner.monitor_stop.notify_waiters();
        let _ = monitor.await;
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

    pub async fn stop_now(&self) -> Result<()> {
        self.shutdown().await
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
                    *self.inner.phase.write().await = LifecyclePhase::Warming;
                    if self.inner.config.warming_delay_ms > 0 {
                        sleep(Duration::from_millis(self.inner.config.warming_delay_ms)).await;
                    }
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
            inflight: self.inner.inflight.load(Ordering::SeqCst),
            bindings: self
                .inner
                .affinity
                .as_ref()
                .map(|affinity| {
                    affinity
                        .snapshot()
                        .into_iter()
                        .map(|binding: SlotBinding| BindingStatus {
                            key: binding.key,
                            app: binding.app,
                            slot: binding.slot,
                            preemptive: binding.preemptive,
                            kv_cache: binding.kv_cache,
                        })
                        .collect()
                })
                .unwrap_or_default(),
        }
    }

    async fn begin_request(&self) {
        let _gate = self.inner.request_gate.lock().await;
        if *self.inner.phase.read().await == LifecyclePhase::Sleeping {
            self.inner.sleep_cancel.notify_one();
            *self.inner.phase.write().await = LifecyclePhase::Running;
        }
        self.inner.inflight.fetch_add(1, Ordering::SeqCst);
        self.inner.stats.record_request();
        *self.inner.last_activity.lock().await = Instant::now();
    }

    fn finish_request_sync(&self) {
        self.inner.inflight.fetch_sub(1, Ordering::SeqCst);
        if let Ok(mut last_activity) = self.inner.last_activity.try_lock() {
            *last_activity = Instant::now();
        }
    }

    async fn idle_monitor(self: Arc<Self>) {
        let interval_ms = (self.inner.config.idle_timeout_ms / 4).clamp(10, 1_000);
        let mut ticker = tokio::time::interval(Duration::from_millis(interval_ms));
        loop {
            tokio::select! {
                _ = ticker.tick() => self.maybe_sleep().await,
                _ = self.inner.monitor_stop.notified() => break,
            }
        }
    }

    async fn maybe_sleep(&self) {
        let gate = self.inner.request_gate.lock().await;
        if *self.inner.phase.read().await != LifecyclePhase::Running
            || self.inner.inflight.load(Ordering::SeqCst) != 0
            || self.inner.last_activity.lock().await.elapsed()
                < Duration::from_millis(self.inner.config.idle_timeout_ms)
        {
            return;
        }

        *self.inner.phase.write().await = LifecyclePhase::Sleeping;
        drop(gate);
        tokio::select! {
            _ = sleep(Duration::from_millis(self.inner.config.sleep_observe_ms)) => {},
            _ = self.inner.sleep_cancel.notified() => {
                *self.inner.phase.write().await = LifecyclePhase::Running;
                return;
            },
        }

        let _gate = self.inner.request_gate.lock().await;
        if self.inner.inflight.load(Ordering::SeqCst) != 0
            || self.inner.last_activity.lock().await.elapsed()
                < Duration::from_millis(self.inner.config.idle_timeout_ms)
        {
            *self.inner.phase.write().await = LifecyclePhase::Running;
            return;
        }

        let backend = self.inner.backend.lock().await.take();
        if let Some(backend) = backend {
            let _ = backend.stop().await;
        }
        *self.inner.phase.write().await = LifecyclePhase::Standby;
    }
}

async fn status(State(gateway): State<Arc<Gateway>>) -> impl IntoResponse {
    Json(gateway.status().await)
}

async fn config(State(gateway): State<Arc<Gateway>>) -> impl IntoResponse {
    Json(gateway.inner.config.clone())
}

async fn update_config(
    State(_gateway): State<Arc<Gateway>>,
    Json(config): Json<AppConfig>,
) -> Response {
    if let Err(error) = config.validate() {
        return error_response(StatusCode::BAD_REQUEST, error.to_string());
    }
    let path = config.config_path();
    match config.save_to(path) {
        Ok(()) => Json(config).into_response(),
        Err(error) => error_response(StatusCode::INTERNAL_SERVER_ERROR, error.to_string()),
    }
}

async fn wake_backend(State(gateway): State<Arc<Gateway>>) -> Response {
    match gateway.ensure_backend().await {
        Ok(_) => Json(gateway.status().await).into_response(),
        Err(error) => error_response(StatusCode::BAD_GATEWAY, error.to_string()),
    }
}

async fn stop_backend(State(gateway): State<Arc<Gateway>>) -> Response {
    match gateway.stop_now().await {
        Ok(()) => Json(gateway.status().await).into_response(),
        Err(error) => error_response(StatusCode::INTERNAL_SERVER_ERROR, error.to_string()),
    }
}

async fn stats(State(gateway): State<Arc<Gateway>>) -> impl IntoResponse {
    Json(gateway.inner.stats.snapshot())
}

#[derive(Debug, Deserialize)]
struct LogQuery {
    #[serde(default = "default_log_kind")]
    kind: String,
    #[serde(default = "default_log_bytes")]
    max_bytes: usize,
}

fn default_log_kind() -> String {
    "main".to_owned()
}

fn default_log_bytes() -> usize {
    32 * 1024
}

async fn logs(State(gateway): State<Arc<Gateway>>, Query(query): Query<LogQuery>) -> Response {
    let Some(logger) = &gateway.inner.logger else {
        return error_response(StatusCode::NOT_FOUND, "logging is unavailable".to_owned());
    };
    let kind = match query.kind.as_str() {
        "main" => LogKind::Main,
        "error" | "errors" => LogKind::Error,
        value
            if value
                .strip_prefix("slot-")
                .and_then(|v| v.parse::<usize>().ok())
                .is_some() =>
        {
            LogKind::Slot(
                value
                    .strip_prefix("slot-")
                    .unwrap()
                    .parse::<usize>()
                    .unwrap(),
            )
        }
        _ => return error_response(StatusCode::BAD_REQUEST, "unknown log kind".to_owned()),
    };
    match logger.read_tail(kind, query.max_bytes.min(256 * 1024)) {
        Ok(text) => Response::builder()
            .status(StatusCode::OK)
            .header(header::CONTENT_TYPE, "text/plain; charset=utf-8")
            .body(Body::from(text))
            .unwrap(),
        Err(error) => error_response(StatusCode::INTERNAL_SERVER_ERROR, error.to_string()),
    }
}

#[derive(Debug, Deserialize)]
struct KvRequest {
    slot: usize,
    key: String,
}

async fn kv_snapshots(State(gateway): State<Arc<Gateway>>) -> impl IntoResponse {
    Json(gateway.inner.kv.snapshot())
}

async fn kv_save(State(gateway): State<Arc<Gateway>>, Json(input): Json<KvRequest>) -> Response {
    match gateway.inner.kv.save(input.slot, &input.key).await {
        Ok(result) => Json(result.response).into_response(),
        Err(error) => error_response(StatusCode::BAD_REQUEST, error.to_string()),
    }
}

async fn kv_restore(State(gateway): State<Arc<Gateway>>, Json(input): Json<KvRequest>) -> Response {
    match gateway.inner.kv.restore(input.slot, &input.key).await {
        Ok(result) => Json(result.response).into_response(),
        Err(error) => error_response(StatusCode::BAD_REQUEST, error.to_string()),
    }
}

async fn kv_erase(State(gateway): State<Arc<Gateway>>, Json(input): Json<KvRequest>) -> Response {
    match gateway.inner.kv.erase(input.slot).await {
        Ok(response) => Json(response).into_response(),
        Err(error) => error_response(StatusCode::BAD_REQUEST, error.to_string()),
    }
}

async fn kv_clear(State(gateway): State<Arc<Gateway>>) -> Response {
    match gateway.inner.kv.clear_all().await {
        Ok(deleted) => Json(serde_json::json!({"deleted": deleted})).into_response(),
        Err(error) => error_response(StatusCode::INTERNAL_SERVER_ERROR, error.to_string()),
    }
}

async fn resources() -> impl IntoResponse {
    Json(ResourceSnapshot::collect())
}

async fn backend_slots(State(gateway): State<Arc<Gateway>>) -> Response {
    backend_snapshot(&gateway, "/slots").await
}

async fn backend_props(State(gateway): State<Arc<Gateway>>) -> Response {
    backend_snapshot(&gateway, "/props").await
}

async fn backend_metrics(State(gateway): State<Arc<Gateway>>) -> Response {
    backend_snapshot(&gateway, "/metrics").await
}

async fn backend_snapshot(gateway: &Gateway, path: &str) -> Response {
    let backend = match gateway.ensure_backend().await {
        Ok(backend) => backend,
        Err(error) => return error_response(StatusCode::BAD_GATEWAY, error.to_string()),
    };
    let response = match gateway
        .inner
        .client
        .get(format!("{}{}", backend.base_url(), path))
        .send()
        .await
    {
        Ok(response) => response,
        Err(error) => return error_response(StatusCode::BAD_GATEWAY, error.to_string()),
    };
    let status = response.status();
    let headers = response.headers().clone();
    match response.bytes().await {
        Ok(bytes) => response_from_bytes(status, &headers, axum::body::Bytes::from(bytes)),
        Err(error) => error_response(StatusCode::BAD_GATEWAY, error.to_string()),
    }
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
    gateway.begin_request().await;
    let lease = Arc::new(RequestLease::new(gateway.clone()));
    let response = proxy_inner(&gateway, request, lease.clone()).await;
    lease.finish_if_not_handed_off();
    response
}

async fn proxy_inner(
    gateway: &Gateway,
    request: Request<Body>,
    lease: Arc<RequestLease>,
) -> Response {
    if gateway.inner.config.crash_recovery_enabled
        && gateway.inner.crash_count.load(Ordering::Acquire)
            >= gateway.inner.config.max_crash_count.max(1)
    {
        return error_response(
            StatusCode::SERVICE_UNAVAILABLE,
            "backend recovery circuit breaker is open".to_owned(),
        );
    }
    let allocation = gateway.inner.affinity.as_ref().map(|affinity| {
        affinity.allocate(
            request.headers(),
            &gateway.inner.config.auto_preemptive_prefixes,
        )
    });
    if let Some(allocation) = &allocation {
        gateway.inner.stats.record_slot(allocation.slot);
        if let Some(logger) = &gateway.inner.logger {
            let _ = logger.write(LogKind::Slot(allocation.slot), "request allocated");
        }
    }
    let backend = match gateway.ensure_backend().await {
        Ok(backend) => backend,
        Err(error) => return error_response(StatusCode::BAD_GATEWAY, error.to_string()),
    };

    let path = request
        .uri()
        .path_and_query()
        .map(|value| value.as_str())
        .unwrap_or("/v1")
        .to_owned();
    let url = format!("{}{}", backend.base_url(), path);
    let method = request.method().clone();
    let is_chat_completion = method == axum::http::Method::POST
        && request.uri().path().trim_end_matches('/') == "/v1/chat/completions";
    let headers = request.headers().clone();
    let body = match to_bytes(request.into_body(), 16 * 1024 * 1024).await {
        Ok(body) => body,
        Err(error) => return error_response(StatusCode::BAD_REQUEST, error.to_string()),
    };
    if let Some(logger) = &gateway.inner.logger {
        let _ = logger.write(LogKind::Main, &format!("request {} {}", method, path));
        if gateway.inner.config.request_dump_enabled {
            let _ = logger.write(
                LogKind::Main,
                &format!("request_dump {}", String::from_utf8_lossy(&body)),
            );
        }
    }
    let allocated_slot = allocation.as_ref().map(|item| item.slot);
    let allocated_key = allocation.as_ref().and_then(|item| item.key.clone());
    if let Some(evicted) = allocation.as_ref().and_then(|item| item.evicted.as_ref()) {
        if evicted.kv_cache {
            if let Err(error) = gateway.inner.kv.save(evicted.slot, &evicted.key).await {
                tracing::warn!(target = "kv", %error, key = %evicted.key, "KV save before slot eviction failed");
            }
        }
    }
    if let (Some(slot), Some(key)) = (allocated_slot, allocated_key.as_deref()) {
        if gateway.inner.kv.has_snapshot(key) {
            match gateway.inner.kv.restore(slot, key).await {
                Ok(_) => gateway.inner.stats.record_restore(true),
                Err(error) => {
                    gateway.inner.stats.record_restore(false);
                    tracing::warn!(target = "kv", %error, %key, "KV restore skipped");
                }
            }
        } else {
            gateway.inner.stats.record_restore(false);
        }
    }
    let mut body = body;
    if allocation.is_some() || gateway.inner.config.token_guard_enabled || is_chat_completion {
        if let Ok(mut value) = serde_json::from_slice::<serde_json::Value>(&body) {
            if let Some(allocation) = allocation {
                inject_slot_value(&mut value, allocation.slot);
            }
            if is_chat_completion {
                let mut mode = gateway.inner.thinking_mode.lock().await;
                if thinking::apply(&mut value, &mut mode) {
                    tracing::debug!(target = "thinking", mode = ?*mode, "thinking mode applied");
                }
            }
            if gateway.inner.config.token_guard_enabled {
                if let Some(context_size) = gateway.inner.config.context_size {
                    let token_config = TokenGuardConfig {
                        context_size,
                        slot_count: gateway.inner.config.slot_count,
                        reserved_output_tokens: gateway.inner.config.reserved_output_tokens,
                        reserved_prompt_overhead: gateway.inner.config.reserved_prompt_overhead,
                        enabled: true,
                    };
                    let client = &gateway.inner.client;
                    let base_url = backend.base_url();
                    match TokenGuard::guard(&token_config, &mut value, |text| {
                        let base_url = &base_url;
                        async move { TokenGuard::count_tokens(client, base_url, &text).await }
                    })
                    .await
                    {
                        Ok(report) => {
                            tracing::debug!(
                                target = "token_guard",
                                modified = report.modified,
                                skipped = report.skipped,
                                estimated_tokens = ?report.estimated_tokens,
                                final_tokens = ?report.final_tokens,
                                budget = report.budget,
                                deleted_turns = report.deleted_turns,
                                "[TOKEN-GUARD] request evaluated"
                            );
                        }
                        Err(error) => {
                            if let Some(GuardError::OverBudget { budget, tokens }) =
                                error.downcast_ref::<GuardError>()
                            {
                                tracing::warn!(
                                    target = "token_guard",
                                    budget,
                                    tokens,
                                    "[TOKEN-GUARD-REJECTED] request exceeds context budget"
                                );
                                return token_guard_error_response(*budget, *tokens);
                            }
                            return error_response(
                                StatusCode::INTERNAL_SERVER_ERROR,
                                error.to_string(),
                            );
                        }
                    }
                }
            }
            if let Ok(serialized) = serde_json::to_vec(&value) {
                body = axum::body::Bytes::from(serialized);
            }
        }
    }

    let response =
        match send_backend_request(&gateway.inner.client, &method, &url, &headers, body.clone())
            .await
        {
            Ok(response) => response,
            Err(error) => {
                let backend = gateway.inner.backend.lock().await.take();
                *gateway.inner.phase.write().await = LifecyclePhase::Standby;
                if let Some(backend) = backend {
                    let _ = backend.stop().await;
                }
                return error_response(StatusCode::BAD_GATEWAY, error.to_string());
            }
        };

    let response = if gateway.inner.config.context_overflow_recovery
        && response.status() == StatusCode::BAD_REQUEST
    {
        let status = response.status();
        let response_headers = response.headers().clone();
        let bytes = match response.bytes().await {
            Ok(bytes) => bytes,
            Err(error) => return error_response(StatusCode::BAD_GATEWAY, error.to_string()),
        };
        if is_context_overflow(&bytes) {
            if let Some(slot) = allocated_slot {
                let _ = erase_backend_slot(&gateway.inner.client, &backend.base_url(), slot).await;
            }
            match send_backend_request(&gateway.inner.client, &method, &url, &headers, body.clone())
                .await
            {
                Ok(retry) => retry,
                Err(error) => {
                    return error_response(StatusCode::BAD_GATEWAY, error.to_string());
                }
            }
        } else {
            return response_from_bytes(status, &response_headers, bytes);
        }
    } else {
        response
    };

    if gateway.inner.config.crash_recovery_enabled && response.status().is_server_error() {
        let response_headers = response.headers().clone();
        let status = response.status();
        let bytes = match response.bytes().await {
            Ok(bytes) => bytes,
            Err(error) => return error_response(StatusCode::BAD_GATEWAY, error.to_string()),
        };
        if is_oom_response(&bytes) || backend.has_oom_evidence() {
            let crashes = gateway.inner.crash_count.fetch_add(1, Ordering::AcqRel) + 1;
            tracing::error!(
                target = "recovery",
                crashes,
                "backend OOM/bad_alloc detected; stopping backend"
            );
            let backend = gateway.inner.backend.lock().await.take();
            *gateway.inner.phase.write().await = LifecyclePhase::Standby;
            if let Some(backend) = backend {
                let _ = backend.stop().await;
            }
            return error_response(
                StatusCode::SERVICE_UNAVAILABLE,
                format!("backend out of memory; recovery attempt recorded ({crashes})"),
            );
        }
        return response_from_bytes(status, &response_headers, bytes);
    }
    gateway.inner.crash_count.store(0, Ordering::Release);

    if gateway.inner.config.continuation_enabled && is_streaming_body(&body) {
        let max = gateway.inner.config.max_continuations;
        let status = response.status();
        let response_headers = response.headers().clone();
        let stream = stream_sse_with_continuation(
            &gateway.inner.client,
            &method,
            &url,
            &headers,
            body,
            response,
            max,
            gateway.inner.config.continuation_timeout_ms,
            continuation_token_config(&gateway.inner.config),
            backend.base_url(),
        );
        let mut output = Response::builder().status(status);
        for (name, value) in &response_headers {
            if !is_hop_by_hop(name) {
                output = output.header(name, value);
            }
        }
        let stream = RequestStream::new(stream, lease.clone());
        lease.handoff();
        return match output.body(Body::from_stream(stream)) {
            Ok(response) => response,
            Err(error) => error_response(StatusCode::INTERNAL_SERVER_ERROR, error.to_string()),
        };
    }

    if gateway.inner.config.continuation_enabled && !is_streaming_body(&body) {
        let max = gateway.inner.config.max_continuations;
        match collect_json_with_continuation(
            &gateway.inner.client,
            &method,
            &url,
            &headers,
            body,
            response,
            max,
            gateway.inner.config.continuation_timeout_ms,
            continuation_token_config(&gateway.inner.config),
            backend.base_url(),
        )
        .await
        {
            Ok((status, response_headers, bytes)) => {
                record_json_usage(&gateway.inner.stats, &bytes);
                return response_from_bytes(status, &response_headers, bytes);
            }
            Err(error) => return error_response(StatusCode::BAD_GATEWAY, error.to_string()),
        }
    }

    let mut output = Response::builder().status(response.status());
    for (name, value) in response.headers() {
        if is_hop_by_hop(name) {
            continue;
        }
        output = output.header(name, value);
    }
    let stream = RequestStream::new(response.bytes_stream(), lease.clone());
    match output.body(Body::from_stream(stream)) {
        Ok(response) => {
            lease.handoff();
            response
        }
        Err(error) => error_response(StatusCode::INTERNAL_SERVER_ERROR, error.to_string()),
    }
}

fn is_streaming_body(body: &axum::body::Bytes) -> bool {
    serde_json::from_slice::<serde_json::Value>(body)
        .ok()
        .and_then(|value| value.get("stream").and_then(serde_json::Value::as_bool))
        .unwrap_or(false)
}

fn stream_sse_with_continuation(
    client: &Client,
    method: &axum::http::Method,
    url: &str,
    headers: &axum::http::HeaderMap,
    body: axum::body::Bytes,
    response: reqwest::Response,
    max_continuations: usize,
    timeout_ms: u64,
    token_config: Option<TokenGuardConfig>,
    backend_base_url: String,
) -> impl Stream<Item = Result<axum::body::Bytes>> + 'static {
    let client = client.clone();
    let method = method.clone();
    let url = url.to_owned();
    let headers = headers.clone();
    async_stream::try_stream! {
        let mut current_body = body;
        let mut response = response;
        let mut accumulated = String::new();
        for round in 0..=max_continuations {
            let status = response.status();
            let mut incoming = response.bytes_stream();
            let mut pending = Vec::new();
            let mut continue_round = false;
            while let Some(chunk) = incoming.next().await {
                pending.extend_from_slice(&chunk?);
                loop {
                    let Some((end, delimiter_len)) = find_sse_delimiter(&pending) else { break; };
                    let event = pending.drain(..end).collect::<Vec<_>>();
                    pending.drain(..delimiter_len);
                    let text = String::from_utf8_lossy(&event).to_string();
                    let (content, reason, has_tool_calls) = crate::continuation::extract_sse_completion(&text);
                    accumulated.push_str(&content);
                    if status.is_success() && reason.as_deref() == Some("length") && !has_tool_calls && round < max_continuations {
                        continue_round = true;
                        pending.clear();
                        break;
                    }
                    let mut output = event;
                    output.extend_from_slice(b"\n\n");
                    yield axum::body::Bytes::from(output);
                }
                if continue_round { break; }
            }
            if !status.is_success() {
                if !pending.is_empty() { yield axum::body::Bytes::from(pending); }
                break;
            }
            if !continue_round {
                if !pending.is_empty() { yield axum::body::Bytes::from(pending); }
                break;
            }
            let value: serde_json::Value = serde_json::from_slice(&current_body)
                .map_err(|error| anyhow!("continuation request is not valid JSON: {error}"))?;
            let mut next = crate::continuation::build_continuation_body(&value, &accumulated)
                .ok_or_else(|| anyhow!("continuation request has no messages array"))?;
            if let Some(token_config) = &token_config {
                TokenGuard::guard(token_config, &mut next, |text| {
                    let client = client.clone();
                    let base_url = backend_base_url.clone();
                    async move { TokenGuard::count_tokens(&client, &base_url, &text).await }
                }).await?;
            }
            current_body = axum::body::Bytes::from(serde_json::to_vec(&next)?);
            response = tokio::time::timeout(
                Duration::from_millis(timeout_ms.max(1)),
                send_backend_request(&client, &method, &url, &headers, current_body.clone()),
            ).await??;
        }
    }
}

fn find_sse_delimiter(bytes: &[u8]) -> Option<(usize, usize)> {
    if let Some(position) = bytes.windows(4).position(|window| window == b"\r\n\r\n") {
        return Some((position, 4));
    }
    bytes
        .windows(2)
        .position(|window| window == b"\n\n")
        .map(|position| (position, 2))
}

#[allow(dead_code)]
async fn collect_sse_with_continuation(
    client: &Client,
    method: &axum::http::Method,
    url: &str,
    headers: &axum::http::HeaderMap,
    mut body: axum::body::Bytes,
    mut response: reqwest::Response,
    max_continuations: usize,
    timeout_ms: u64,
    token_config: Option<TokenGuardConfig>,
    backend_base_url: String,
) -> Result<(
    reqwest::StatusCode,
    reqwest::header::HeaderMap,
    axum::body::Bytes,
)> {
    let mut output = String::new();
    let mut accumulated = String::new();
    let mut round = 0usize;
    loop {
        let status = response.status();
        let response_headers = response.headers().clone();
        let bytes = response.bytes().await?;
        if !status.is_success() {
            return Ok((status, response_headers, axum::body::Bytes::from(bytes)));
        }
        let text = String::from_utf8_lossy(&bytes).to_string();
        let (content, reason, has_tool_calls) = crate::continuation::extract_sse_completion(&text);
        accumulated.push_str(&content);
        let should_continue =
            reason.as_deref() == Some("length") && !has_tool_calls && round < max_continuations;
        if !should_continue {
            output.push_str(&text);
            return Ok((status, response_headers, axum::body::Bytes::from(output)));
        }
        output.push_str(&crate::continuation::normalize_truncated_round(&text));
        let value: serde_json::Value = serde_json::from_slice(&body)
            .map_err(|error| anyhow!("continuation request is not valid JSON: {error}"))?;
        let next = crate::continuation::build_continuation_body(&value, &accumulated)
            .ok_or_else(|| anyhow!("continuation request has no messages array"))?;
        let mut next = next;
        if let Some(token_config) = &token_config {
            TokenGuard::guard(token_config, &mut next, |text| {
                let base_url = &backend_base_url;
                async move { TokenGuard::count_tokens(client, base_url, &text).await }
            })
            .await?;
        }
        body = axum::body::Bytes::from(serde_json::to_vec(&next)?);
        let send = send_backend_request(client, method, url, headers, body.clone());
        response = tokio::time::timeout(Duration::from_millis(timeout_ms.max(1)), send).await??;
        round += 1;
    }
}

async fn collect_json_with_continuation(
    client: &Client,
    method: &axum::http::Method,
    url: &str,
    headers: &axum::http::HeaderMap,
    mut body: axum::body::Bytes,
    mut response: reqwest::Response,
    max_continuations: usize,
    timeout_ms: u64,
    token_config: Option<TokenGuardConfig>,
    backend_base_url: String,
) -> Result<(
    reqwest::StatusCode,
    reqwest::header::HeaderMap,
    axum::body::Bytes,
)> {
    for round in 0..=max_continuations {
        let status = response.status();
        let response_headers = response.headers().clone();
        let bytes = response.bytes().await?;
        if !status.is_success() {
            return Ok((status, response_headers, axum::body::Bytes::from(bytes)));
        }
        let value: serde_json::Value = serde_json::from_slice(&bytes)
            .map_err(|error| anyhow!("backend JSON response is invalid: {error}"))?;
        let choice = value
            .get("choices")
            .and_then(|v| v.as_array())
            .and_then(|v| v.first());
        let reason = choice
            .and_then(|v| v.get("finish_reason"))
            .and_then(serde_json::Value::as_str);
        let tool_calls = choice
            .and_then(|v| v.get("message"))
            .and_then(|v| v.get("tool_calls"))
            .is_some();
        if reason != Some("length") || tool_calls || round == max_continuations {
            return Ok((
                status,
                response_headers,
                axum::body::Bytes::from(serde_json::to_vec(&value)?),
            ));
        }
        let content = choice
            .and_then(|v| v.get("message"))
            .and_then(|v| v.get("content"))
            .and_then(serde_json::Value::as_str)
            .unwrap_or_default();
        let next = crate::continuation::build_continuation_body(&body_value(&body)?, content)
            .ok_or_else(|| anyhow!("continuation request has no messages array"))?;
        let mut next = next;
        if let Some(token_config) = &token_config {
            TokenGuard::guard(token_config, &mut next, |text| {
                let base_url = &backend_base_url;
                async move { TokenGuard::count_tokens(client, base_url, &text).await }
            })
            .await?;
        }
        body = axum::body::Bytes::from(serde_json::to_vec(&next)?);
        response = tokio::time::timeout(
            Duration::from_millis(timeout_ms.max(1)),
            send_backend_request(client, method, url, headers, body.clone()),
        )
        .await??;
    }
    unreachable!()
}

fn body_value(body: &axum::body::Bytes) -> Result<serde_json::Value> {
    Ok(serde_json::from_slice(body)?)
}

fn record_json_usage(stats: &Stats, bytes: &[u8]) {
    let Ok(value) = serde_json::from_slice::<serde_json::Value>(bytes) else {
        return;
    };
    let Some(usage) = value.get("usage") else {
        return;
    };
    let prompt = usage
        .get("prompt_tokens")
        .and_then(serde_json::Value::as_u64)
        .unwrap_or(0);
    let completion = usage
        .get("completion_tokens")
        .and_then(serde_json::Value::as_u64)
        .unwrap_or(0);
    stats.record_tokens(prompt, completion, completion);
}

fn continuation_token_config(config: &AppConfig) -> Option<TokenGuardConfig> {
    config
        .context_size
        .map(|context_size| TokenGuardConfig {
            context_size,
            slot_count: config.slot_count,
            reserved_output_tokens: config.reserved_output_tokens,
            reserved_prompt_overhead: config.reserved_prompt_overhead,
            enabled: config.token_guard_enabled,
        })
        .filter(|config| config.enabled)
}

async fn send_backend_request(
    client: &Client,
    method: &axum::http::Method,
    url: &str,
    headers: &axum::http::HeaderMap,
    body: axum::body::Bytes,
) -> Result<reqwest::Response> {
    let mut builder = client.request(method.clone(), url).body(body);
    for (name, value) in headers {
        if *name != header::HOST && *name != header::CONTENT_LENGTH {
            builder = builder.header(name, value);
        }
    }
    Ok(builder.send().await?)
}

async fn erase_backend_slot(client: &Client, base_url: &str, slot: usize) -> Result<()> {
    let response = client
        .post(format!(
            "{}/slots/{slot}?action=erase",
            base_url.trim_end_matches('/')
        ))
        .send()
        .await?;
    if response.status().is_success() {
        Ok(())
    } else {
        Err(anyhow!("KV erase failed with HTTP {}", response.status()))
    }
}

fn is_context_overflow(body: &[u8]) -> bool {
    let text = String::from_utf8_lossy(body).to_ascii_lowercase();
    [
        "context",
        "prompt too long",
        "exceed",
        "maximum context",
        "n_ctx",
    ]
    .iter()
    .any(|needle| text.contains(needle))
}

fn is_oom_response(body: &[u8]) -> bool {
    let text = String::from_utf8_lossy(body).to_ascii_lowercase();
    ["bad_alloc", "bad allocation", "out of memory", "oom"]
        .iter()
        .any(|needle| text.contains(needle))
}

fn response_from_bytes(
    status: reqwest::StatusCode,
    headers: &reqwest::header::HeaderMap,
    bytes: axum::body::Bytes,
) -> Response {
    let mut output = Response::builder().status(status);
    for (name, value) in headers {
        if matches!(
            name.as_str(),
            "connection"
                | "keep-alive"
                | "proxy-authenticate"
                | "proxy-authorization"
                | "te"
                | "trailer"
                | "transfer-encoding"
                | "upgrade"
                | "content-length"
        ) {
            continue;
        }
        if let Ok(name) = HeaderName::from_bytes(name.as_str().as_bytes()) {
            if let Ok(value) = HeaderValue::from_bytes(value.as_bytes()) {
                output = output.header(name, value);
            }
        }
    }
    output.body(Body::from(bytes)).unwrap_or_else(|error| {
        error_response(StatusCode::INTERNAL_SERVER_ERROR, error.to_string())
    })
}

fn inject_slot_value(value: &mut serde_json::Value, slot: usize) -> bool {
    let Some(object) = value.as_object_mut() else {
        return false;
    };
    if object.contains_key("n_slots") {
        return false;
    }
    object.insert("n_slots".to_owned(), serde_json::json!(slot));
    true
}

struct RequestLease {
    gateway: Arc<Gateway>,
    handed_off: AtomicBool,
    finished: AtomicBool,
}

impl RequestLease {
    fn new(gateway: Arc<Gateway>) -> Self {
        Self {
            gateway,
            handed_off: AtomicBool::new(false),
            finished: AtomicBool::new(false),
        }
    }

    fn handoff(&self) {
        self.handed_off.store(true, Ordering::Release);
    }

    fn finish_if_not_handed_off(&self) {
        if !self.handed_off.load(Ordering::Acquire) {
            self.finish();
        }
    }

    fn finish(&self) {
        if !self.finished.swap(true, Ordering::AcqRel) {
            self.gateway.finish_request_sync();
        }
    }
}

struct RequestStream<S> {
    inner: Pin<Box<S>>,
    lease: Arc<RequestLease>,
}

impl<S> RequestStream<S> {
    fn new(inner: S, lease: Arc<RequestLease>) -> Self {
        Self {
            inner: Box::pin(inner),
            lease,
        }
    }
}

impl<S: Stream + 'static> Stream for RequestStream<S> {
    type Item = S::Item;

    fn poll_next(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        let this = self.get_mut();
        match this.inner.as_mut().poll_next(cx) {
            Poll::Ready(None) => {
                this.lease.finish();
                Poll::Ready(None)
            }
            other => other,
        }
    }
}

impl<S> Drop for RequestStream<S> {
    fn drop(&mut self) {
        self.lease.finish();
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

fn token_guard_error_response(budget: usize, tokens: usize) -> Response {
    let mut response = (
        StatusCode::BAD_REQUEST,
        Json(serde_json::json!({
            "error": {
                "message": format!("Token Guard: {tokens} tokens exceed budget {budget}"),
                "type": "invalid_request_error",
                "code": "token_guard_over_budget",
                "token_guard": true,
                "budget": budget,
                "tokens": tokens,
            }
        })),
    )
        .into_response();
    response.headers_mut().insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static("application/json"),
    );
    response
}
