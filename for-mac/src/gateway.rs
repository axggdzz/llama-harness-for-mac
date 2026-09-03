use crate::{
    config::AppConfig,
    lifecycle::LifecyclePhase,
    process::{BackendHandle, BackendProcess},
    slot_affinity::{SlotAffinity, SlotBinding},
    token_guard::{GuardError, TokenGuard, TokenGuardConfig},
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
use futures_util::Stream;
use reqwest::Client;
use serde::Serialize;
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
        let affinity = if config.slot_count > 1 {
            let path = config
                .slot_bindings_path
                .clone()
                .unwrap_or_else(|| config.data_dir.join("slot_bindings.json"));
            Some(Arc::new(SlotAffinity::new(config.slot_count, path)))
        } else {
            None
        };
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
    let allocation = gateway.inner.affinity.as_ref().map(|affinity| {
        affinity.allocate(
            request.headers(),
            &gateway.inner.config.auto_preemptive_prefixes,
        )
    });
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
    let mut body = body;
    if allocation.is_some() || gateway.inner.config.token_guard_enabled {
        if let Ok(mut value) = serde_json::from_slice::<serde_json::Value>(&body) {
            if let Some(allocation) = allocation {
                inject_slot_value(&mut value, allocation.slot);
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

    let mut builder = gateway.inner.client.request(method, url).body(body);
    for (name, value) in &headers {
        if *name != header::HOST && *name != header::CONTENT_LENGTH {
            builder = builder.header(name, value);
        }
    }
    let response = match builder.send().await {
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
