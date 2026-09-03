use axum::{
    body::Body,
    extract::{Json, Path, Query},
    http::{header, HeaderValue, StatusCode},
    response::{IntoResponse, Response},
    routing::{get, post},
    Router,
};
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    convert::Infallible,
    net::SocketAddr,
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc,
    },
    time::Duration,
};
use tokio_stream::iter;

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().collect();
    let port = argument(&args, "--port")
        .and_then(|value| value.parse().ok())
        .unwrap_or(8081);
    let startup_delay_ms = argument(&args, "--startup-delay-ms")
        .and_then(|value| value.parse().ok())
        .unwrap_or(0_u64);
    let force_sse = args.iter().any(|arg| arg == "--sse");
    let overflow_pending = Arc::new(AtomicBool::new(
        args.iter().any(|arg| arg == "--overflow-once"),
    ));
    let length_pending = Arc::new(AtomicBool::new(
        args.iter().any(|arg| arg == "--length-once"),
    ));
    let oom_pending = Arc::new(AtomicBool::new(args.iter().any(|arg| arg == "--oom-once")));
    let oom_marker = argument(&args, "--oom-marker").map(PathBuf::from);
    if let Some(marker) = &oom_marker {
        if marker.exists() {
            oom_pending.store(false, Ordering::Release);
        } else {
            oom_pending.store(true, Ordering::Release);
        }
    }

    if startup_delay_ms > 0 {
        tokio::time::sleep(Duration::from_millis(startup_delay_ms)).await;
    }

    let app = Router::new()
        .route("/health", get(|| async { Json(json!({"status":"ok"})) }))
        .route("/slots", get(|| async { Json(json!([])) }))
        .route(
            "/slots/:slot",
            post(
                |Path(_slot): Path<usize>,
                 Query(query): Query<HashMap<String, String>>,
                 body: Option<Json<Value>>| async move {
                    let action = query.get("action").map(String::as_str).unwrap_or_default();
                    match action {
                        "save" => {
                            let filename = body
                                .and_then(|Json(value)| {
                                    value
                                        .get("filename")
                                        .and_then(Value::as_str)
                                        .map(str::to_owned)
                                })
                                .unwrap_or_else(|| "mock-kv.bin".to_owned());
                            let path = PathBuf::from(filename);
                            if let Some(parent) = path.parent() {
                                let _ = tokio::fs::create_dir_all(parent).await;
                            }
                            tokio::fs::write(path, b"mock-kv-data").await.unwrap();
                            Json(json!({"n_saved": 3, "n_written": 12})).into_response()
                        }
                        "restore" => Json(json!({"status":"restored"})).into_response(),
                        "erase" => Json(json!({"status":"erased"})).into_response(),
                        _ => (
                            StatusCode::BAD_REQUEST,
                            Json(json!({"error":"unknown action"})),
                        )
                            .into_response(),
                    }
                },
            ),
        )
        .route("/props", get(|| async { Json(json!({"mock":true})) }))
        .route(
            "/v1/tokenize",
            post(|Json(payload): Json<Value>| async move {
                let content = payload
                    .get("content")
                    .and_then(Value::as_str)
                    .unwrap_or_default();
                Json(json!({"n_tokens": content.chars().count()}))
            }),
        )
        .route(
            "/metrics",
            get(|| async { (StatusCode::OK, "mock_requests_total 1\n") }),
        )
        .route(
            "/v1/chat/completions",
            post(move |Json(payload): Json<Value>| {
                let overflow_pending = overflow_pending.clone();
                let length_pending = length_pending.clone();
                let oom_pending = oom_pending.clone();
                let oom_marker = oom_marker.clone();
                async move {
                    if oom_pending.swap(false, Ordering::AcqRel) {
                        if let Some(marker) = oom_marker {
                            let _ = std::fs::write(marker, b"oom");
                        }
                        return (
                            StatusCode::SERVICE_UNAVAILABLE,
                            Json(json!({"error":{"message":"std::bad_alloc: out of memory"}})),
                        )
                            .into_response();
                    }
                    if overflow_pending.swap(false, Ordering::AcqRel) {
                        return (
                            StatusCode::BAD_REQUEST,
                            Json(json!({"error":{"message":"context size exceeded"}})),
                        )
                            .into_response();
                    }
                    let length = length_pending.swap(false, Ordering::AcqRel);
                    completion_response(payload, force_sse, length).await
                }
            }),
        );
    let addr = SocketAddr::from(([127, 0, 0, 1], port));
    let listener = tokio::net::TcpListener::bind(addr)
        .await
        .expect("bind mock server port");
    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown_signal())
        .await
        .unwrap();
}

fn argument<'a>(args: &'a [String], name: &str) -> Option<&'a str> {
    args.windows(2)
        .find(|pair| pair[0] == name)
        .map(|pair| pair[1].as_str())
}

async fn completion_response(payload: Value, force_sse: bool, finish_length: bool) -> Response {
    let stream = force_sse
        || payload
            .get("stream")
            .and_then(Value::as_bool)
            .unwrap_or(false);
    if !stream {
        let messages = payload
            .get("messages")
            .and_then(Value::as_array)
            .cloned()
            .unwrap_or_default();
        let prompt_chars = messages
            .iter()
            .filter_map(|message| message.get("content").and_then(Value::as_str))
            .map(|value| value.chars().count())
            .sum::<usize>();
        return Response::new(Body::from(
            json!({
                "id":"mock-completion",
                "object":"chat.completion",
                "created":0,
                "model":payload.get("model").and_then(Value::as_str).unwrap_or("mock"),
                "choices":[{"index":0,"message":{"role":"assistant","content":"mock response"},"finish_reason":if finish_length {"length"} else {"stop"}}],
                "x_n_slots": payload.get("n_slots").cloned().unwrap_or(Value::Null),
                "x_message_count": messages.len(),
                "x_prompt_chars": prompt_chars,
                "x_chat_template_kwargs": payload
                    .get("chat_template_kwargs")
                    .cloned()
                    .unwrap_or(Value::Null),
                "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}
            })
            .to_string(),
        ));
    }

    let finish = if finish_length { "length" } else { "stop" };
    let events: Vec<Result<String, Infallible>> = vec![
        Ok("data: {\"id\":\"mock-stream\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"mock\"},\"finish_reason\":null}]}\n\n".to_owned()),
        Ok(format!("data: {{\"id\":\"mock-stream\",\"object\":\"chat.completion.chunk\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"{finish}\"}}]}}\n\n")),
        Ok("data: [DONE]\n\n".to_owned()),
    ];
    let mut response = Response::new(Body::from_stream(iter(events)));
    response.headers_mut().insert(
        header::CONTENT_TYPE,
        HeaderValue::from_static("text/event-stream"),
    );
    response
}

async fn shutdown_signal() {
    let _ = tokio::signal::ctrl_c().await;
}
