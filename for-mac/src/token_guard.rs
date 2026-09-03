#[cfg(test)]
mod tests {
    use super::{GuardError, TokenGuard, TokenGuardConfig};
    use axum::{extract::Json, http::StatusCode, routing::post, Router};
    use serde_json::json;
    use tokio::net::TcpListener;

    #[test]
    fn budget_reserves_output_and_prompt_overhead_per_slot() {
        let config = TokenGuardConfig {
            context_size: 100,
            slot_count: 2,
            reserved_output_tokens: 10,
            reserved_prompt_overhead: 5,
            enabled: true,
        };
        assert_eq!(TokenGuard::budget(&config), 35);
    }

    #[tokio::test]
    async fn disabled_or_tokenize_failure_keeps_body_unchanged() {
        let mut body = json!({"messages":[{"role":"user","content":"hello"}]});
        let original = body.clone();
        let disabled = TokenGuardConfig {
            context_size: 100,
            slot_count: 1,
            reserved_output_tokens: 0,
            reserved_prompt_overhead: 0,
            enabled: false,
        };
        let report = TokenGuard::guard(&disabled, &mut body, |_text| async { Ok(999usize) })
            .await
            .unwrap();
        assert!(!report.modified && report.skipped);
        assert_eq!(body, original);

        let enabled = TokenGuardConfig {
            context_size: 10,
            slot_count: 1,
            reserved_output_tokens: 0,
            reserved_prompt_overhead: 0,
            enabled: true,
        };
        let report = TokenGuard::guard(&enabled, &mut body, |_text| async {
            Err(anyhow::anyhow!("tokenizer unavailable"))
        })
        .await
        .unwrap();
        assert!(!report.modified && report.skipped);
        assert_eq!(body, original);
    }

    #[tokio::test]
    async fn removes_old_complete_turns_but_keeps_system_latest_and_tool_messages() {
        let mut body = json!({"messages":[
            {"role":"system","content":"rules"},
            {"role":"user","content":"old question"},
            {"role":"assistant","content":"old answer"},
            {"role":"tool","content":"old tool result"},
            {"role":"user","content":"latest question"},
            {"role":"assistant","content":"latest answer"},
            {"role":"tool","content":"latest tool result"}
        ]});
        let config = TokenGuardConfig {
            context_size: 20,
            slot_count: 1,
            reserved_output_tokens: 0,
            reserved_prompt_overhead: 0,
            enabled: true,
        };
        let report = TokenGuard::guard(&config, &mut body, |text| async move {
            Ok(text.split_whitespace().count())
        })
        .await
        .unwrap();
        assert!(report.modified);
        let messages = body["messages"].as_array().unwrap();
        assert_eq!(messages.len(), 4);
        assert_eq!(messages[0]["role"], "system");
        assert_eq!(messages[1]["content"], "latest question");
        assert_eq!(messages[3]["role"], "tool");
    }

    #[tokio::test]
    async fn trims_large_content_head_and_tail() {
        let content = format!("HEAD {} TAIL", "x".repeat(500));
        let mut body = json!({"messages":[{"role":"user","content":content}]});
        let config = TokenGuardConfig {
            context_size: 220,
            slot_count: 1,
            reserved_output_tokens: 0,
            reserved_prompt_overhead: 0,
            enabled: true,
        };
        let report = TokenGuard::guard(&config, &mut body, |text| async move { Ok(text.len()) })
            .await
            .unwrap();
        assert!(report.modified);
        let value = body["messages"][0]["content"].as_str().unwrap();
        assert!(value.starts_with("HEAD"));
        assert!(value.contains("[已截断 - Token Guard]"));
        assert!(value.contains("TAIL"));
    }

    #[tokio::test]
    async fn rejects_when_minimum_message_set_stays_over_budget() {
        let mut body = json!({"messages":[{"role":"user","content":"short"}]});
        let config = TokenGuardConfig {
            context_size: 1,
            slot_count: 1,
            reserved_output_tokens: 0,
            reserved_prompt_overhead: 0,
            enabled: true,
        };
        let result =
            TokenGuard::guard(&config, &mut body, |text| async move { Ok(text.len()) }).await;
        assert!(result.unwrap_err().downcast_ref::<GuardError>().is_some());
    }

    async fn token_server(status: StatusCode, body: serde_json::Value) -> String {
        let app = Router::new().route(
            "/v1/tokenize",
            post(move || {
                let body = body.clone();
                async move { (status, Json(body)) }
            }),
        );
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        tokio::spawn(async move {
            axum::serve(listener, app).await.unwrap();
        });
        format!("http://{address}")
    }

    #[tokio::test]
    async fn count_tokens_accepts_array_and_number_formats() {
        let base = token_server(StatusCode::OK, json!({"tokens":[1,2,3]})).await;
        assert_eq!(
            TokenGuard::count_tokens(&reqwest::Client::new(), &base, "hi")
                .await
                .unwrap(),
            3
        );
        let base = token_server(StatusCode::OK, json!({"n_tokens":7})).await;
        assert_eq!(
            TokenGuard::count_tokens(&reqwest::Client::new(), &base, "hi")
                .await
                .unwrap(),
            7
        );
    }

    #[tokio::test]
    async fn count_tokens_rejects_http_errors_and_unknown_payloads() {
        let base = token_server(StatusCode::BAD_REQUEST, json!({"error":"bad"})).await;
        assert!(
            TokenGuard::count_tokens(&reqwest::Client::new(), &base, "hi")
                .await
                .is_err()
        );
        let base = token_server(StatusCode::OK, json!({"unexpected":true})).await;
        assert!(
            TokenGuard::count_tokens(&reqwest::Client::new(), &base, "hi")
                .await
                .is_err()
        );
    }
}
use anyhow::Result;
use reqwest::Client;
use serde_json::Value;
use std::{fmt, future::Future, time::Duration};

#[derive(Debug, Clone)]
pub struct TokenGuardConfig {
    pub context_size: usize,
    pub slot_count: usize,
    pub reserved_output_tokens: usize,
    pub reserved_prompt_overhead: usize,
    pub enabled: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GuardReport {
    pub modified: bool,
    pub skipped: bool,
    pub estimated_tokens: Option<usize>,
    pub final_tokens: Option<usize>,
    pub budget: usize,
    pub deleted_turns: usize,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum GuardError {
    OverBudget { budget: usize, tokens: usize },
}

impl fmt::Display for GuardError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::OverBudget { budget, tokens } => {
                write!(f, "Token Guard: {tokens} tokens exceed budget {budget}")
            }
        }
    }
}
impl std::error::Error for GuardError {}

pub struct TokenGuard;

impl TokenGuard {
    pub fn budget(config: &TokenGuardConfig) -> usize {
        let slots = config.slot_count.max(1);
        (config.context_size / slots)
            .saturating_sub(
                config
                    .reserved_output_tokens
                    .saturating_add(config.reserved_prompt_overhead),
            )
            .max(1)
    }

    pub async fn guard<F, Fut>(
        config: &TokenGuardConfig,
        body: &mut Value,
        counter: F,
    ) -> Result<GuardReport>
    where
        F: Fn(String) -> Fut + Copy,
        Fut: Future<Output = Result<usize>>,
    {
        let budget = Self::budget(config);
        let Some(messages) = body.get("messages").and_then(Value::as_array) else {
            return Ok(GuardReport {
                modified: false,
                skipped: true,
                estimated_tokens: None,
                final_tokens: None,
                budget,
                deleted_turns: 0,
            });
        };
        if !config.enabled || messages.is_empty() {
            return Ok(GuardReport {
                modified: false,
                skipped: true,
                estimated_tokens: None,
                final_tokens: None,
                budget,
                deleted_turns: 0,
            });
        }
        let original = body.clone();
        let estimated = match counter(messages_text(messages)).await {
            Ok(tokens) => tokens,
            Err(_) => {
                return Ok(GuardReport {
                    modified: false,
                    skipped: true,
                    estimated_tokens: None,
                    final_tokens: None,
                    budget,
                    deleted_turns: 0,
                })
            }
        };
        if estimated <= budget {
            return Ok(GuardReport {
                modified: false,
                skipped: false,
                estimated_tokens: Some(estimated),
                final_tokens: Some(estimated),
                budget,
                deleted_turns: 0,
            });
        }

        let (_, starts) = turn_starts(messages);
        let mut deleted_turns = 0;
        if starts.len() > 1 {
            let max_delete = starts.len() - 1;
            let evaluate = |k: usize, value: &Value| {
                let messages = value
                    .get("messages")
                    .and_then(Value::as_array)
                    .expect("messages array");
                let mut text = String::new();
                for message in &messages[..starts[0]] {
                    append_message_text(&mut text, message);
                }
                for message in &messages[starts[k]..] {
                    append_message_text(&mut text, message);
                }
                counter(text)
            };
            let probe = body.clone();
            let max_count = evaluate(max_delete, &probe).await;
            let max_count = match max_count {
                Ok(tokens) => tokens,
                Err(_) => {
                    *body = original;
                    return Ok(skipped_report(budget));
                }
            };
            if max_count <= budget {
                let mut lo = 1;
                let mut hi = max_delete;
                while lo < hi {
                    let mid = (lo + hi) / 2;
                    let count = match evaluate(mid, &probe).await {
                        Ok(tokens) => tokens,
                        Err(_) => {
                            *body = original;
                            return Ok(skipped_report(budget));
                        }
                    };
                    if count <= budget {
                        hi = mid;
                    } else {
                        lo = mid + 1;
                    }
                }
                deleted_turns = hi;
            } else {
                deleted_turns = max_delete;
            }
            if deleted_turns > 0 {
                let messages = body
                    .get_mut("messages")
                    .and_then(Value::as_array_mut)
                    .expect("messages array");
                let end = starts[deleted_turns];
                messages.drain(starts[0]..end);
            }
        }

        let mut final_tokens = match counter(messages_text(
            body["messages"].as_array().expect("messages array"),
        ))
        .await
        {
            Ok(tokens) => tokens,
            Err(_) => {
                *body = original;
                return Ok(skipped_report(budget));
            }
        };
        let mut content_truncated = false;
        let mut retain = (budget as f64 / final_tokens.max(1) as f64).max(0.1);
        for _ in 0..5 {
            if final_tokens <= budget {
                break;
            }
            let Some((index, content)) =
                largest_string_content(body["messages"].as_array().expect("messages array"))
            else {
                break;
            };
            if content.chars().count() < 200 {
                break;
            }
            let old_len = content.chars().count();
            let mut new_len = ((old_len as f64) * retain) as usize;
            new_len = new_len.max(50).min(old_len.saturating_sub(1));
            let chars = content.chars().collect::<Vec<_>>();
            let head = new_len / 2;
            let tail = new_len - head;
            let kept = format!(
                "{}\n[…]\n{}\n[已截断 - Token Guard]",
                chars[..head].iter().collect::<String>(),
                chars[old_len - tail..].iter().collect::<String>()
            );
            body["messages"][index]["content"] = Value::String(kept);
            content_truncated = true;
            final_tokens = match counter(messages_text(
                body["messages"].as_array().expect("messages array"),
            ))
            .await
            {
                Ok(tokens) => tokens,
                Err(_) => {
                    *body = original;
                    return Ok(skipped_report(budget));
                }
            };
            retain = (retain / 2.0).max(0.1);
        }
        if final_tokens > budget {
            return Err(GuardError::OverBudget {
                budget,
                tokens: final_tokens,
            }
            .into());
        }
        Ok(GuardReport {
            modified: deleted_turns > 0 || content_truncated,
            skipped: false,
            estimated_tokens: Some(estimated),
            final_tokens: Some(final_tokens),
            budget,
            deleted_turns,
        })
    }

    pub async fn count_tokens(
        client: &Client,
        backend_base_url: &str,
        text: &str,
    ) -> Result<usize> {
        let response = tokio::time::timeout(
            Duration::from_secs(30),
            client
                .post(format!(
                    "{}/v1/tokenize",
                    backend_base_url.trim_end_matches('/')
                ))
                .json(&serde_json::json!({"content": text}))
                .send(),
        )
        .await??;
        if !response.status().is_success() {
            anyhow::bail!("tokenize returned HTTP {}", response.status());
        }
        let body: Value = tokio::time::timeout(Duration::from_secs(30), response.json()).await??;
        if let Some(tokens) = body.get("tokens").and_then(Value::as_array) {
            return Ok(tokens.len());
        }
        if let Some(tokens) = body.get("n_tokens").and_then(Value::as_u64) {
            return Ok(tokens as usize);
        }
        anyhow::bail!("tokenize response has no tokens or n_tokens")
    }
}

fn skipped_report(budget: usize) -> GuardReport {
    GuardReport {
        modified: false,
        skipped: true,
        estimated_tokens: None,
        final_tokens: None,
        budget,
        deleted_turns: 0,
    }
}
fn messages_text(messages: &[Value]) -> String {
    let mut text = String::new();
    for message in messages {
        append_message_text(&mut text, message);
    }
    text
}
fn append_message_text(text: &mut String, message: &Value) {
    if let Some(object) = message.as_object() {
        text.push_str(object.get("role").and_then(Value::as_str).unwrap_or(""));
        text.push_str(": ");
        match object.get("content") {
            Some(Value::String(content)) => text.push_str(content),
            Some(value) => text.push_str(&value.to_string()),
            None => {}
        }
        text.push('\n');
    }
}
fn turn_starts(messages: &[Value]) -> (usize, Vec<usize>) {
    let mut starts = Vec::new();
    for (index, message) in messages.iter().enumerate() {
        if message.get("role").and_then(Value::as_str) == Some("user") {
            starts.push(index);
        }
    }
    (starts.len().saturating_sub(1), starts)
}
fn largest_string_content(messages: &[Value]) -> Option<(usize, String)> {
    messages
        .iter()
        .enumerate()
        .filter_map(|(index, message)| {
            message
                .get("content")
                .and_then(Value::as_str)
                .map(|content| (index, content.to_owned()))
        })
        .max_by_key(|(_, content)| content.chars().count())
}
