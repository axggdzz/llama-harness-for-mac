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
    collections::HashMap, convert::Infallible, net::SocketAddr, path::PathBuf, time::Duration,
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
            "/metrics",
            get(|| async { (StatusCode::OK, "mock_requests_total 1\n") }),
        )
        .route(
            "/v1/chat/completions",
            post(move |Json(payload): Json<Value>| async move {
                completion_response(payload, force_sse).await
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

async fn completion_response(payload: Value, force_sse: bool) -> Response {
    let stream = force_sse
        || payload
            .get("stream")
            .and_then(Value::as_bool)
            .unwrap_or(false);
    if !stream {
        return Response::new(Body::from(
            json!({
                "id":"mock-completion",
                "object":"chat.completion",
                "created":0,
                "model":payload.get("model").and_then(Value::as_str).unwrap_or("mock"),
                "choices":[{"index":0,"message":{"role":"assistant","content":"mock response"},"finish_reason":"stop"}],
                "x_n_slots": payload.get("n_slots").cloned().unwrap_or(Value::Null),
                "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}
            })
            .to_string(),
        ));
    }

    let events: Vec<Result<String, Infallible>> = vec![
        Ok("data: {\"id\":\"mock-stream\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"mock\"},\"finish_reason\":null}]}\n\n".to_owned()),
        Ok("data: {\"id\":\"mock-stream\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n".to_owned()),
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
