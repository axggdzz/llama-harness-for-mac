#[cfg(test)]
mod tests {
    use super::{build_continuation_body, extract_sse_completion};
    use serde_json::json;

    #[test]
    fn detects_length_and_accumulates_content() {
        let sse = "data: {\"choices\":[{\"delta\":{\"content\":\"part\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n";
        let (content, reason, tool_calls) = extract_sse_completion(sse);
        assert_eq!(content, "part");
        assert_eq!(reason.as_deref(), Some("length"));
        assert!(!tool_calls);
    }

    #[test]
    fn continuation_body_appends_assistant_and_instruction() {
        let body = json!({"model":"mock","messages":[{"role":"user","content":"question"}]});
        let next = build_continuation_body(&body, "partial answer").unwrap();
        let messages = next["messages"].as_array().unwrap();
        assert_eq!(messages[1]["role"], "assistant");
        assert_eq!(messages[1]["content"], "partial answer");
        assert_eq!(messages[2]["role"], "user");
    }

    #[test]
    fn truncated_round_hides_done_and_exposes_nonterminal_finish_reason() {
        let sse = "data: {\"choices\":[{\"delta\":{\"content\":\"part\"},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n";
        let normalized = super::normalize_truncated_round(sse);
        assert!(normalized.contains("\"finish_reason\":null"));
        assert!(!normalized.contains("[DONE]"));
    }
}

pub fn extract_sse_completion(sse: &str) -> (String, Option<String>, bool) {
    let mut content = String::new();
    let mut finish_reason = None;
    let mut tool_calls = false;
    for line in sse.lines().filter(|line| line.starts_with("data:")) {
        let payload = line[5..].trim();
        if payload == "[DONE]" {
            continue;
        }
        let Ok(value) = serde_json::from_str::<serde_json::Value>(payload) else {
            continue;
        };
        let Some(choice) = value
            .get("choices")
            .and_then(|v| v.as_array())
            .and_then(|v| v.first())
        else {
            continue;
        };
        if let Some(delta) = choice.get("delta") {
            if let Some(value) = delta.get("content").and_then(serde_json::Value::as_str) {
                content.push_str(value);
            }
            if delta.get("tool_calls").is_some() {
                tool_calls = true;
            }
        }
        if let Some(reason) = choice
            .get("finish_reason")
            .and_then(serde_json::Value::as_str)
        {
            finish_reason = Some(reason.to_owned());
        }
    }
    (content, finish_reason, tool_calls)
}

pub fn build_continuation_body(
    body: &serde_json::Value,
    accumulated: &str,
) -> Option<serde_json::Value> {
    let mut next = body.clone();
    let messages = next.get_mut("messages")?.as_array_mut()?;
    messages.push(serde_json::json!({"role":"assistant","content":accumulated}));
    messages.push(serde_json::json!({"role":"user","content":"请继续输出，不要重复已有内容，延续上文逻辑完成剩余内容"}));
    next.as_object_mut()?
        .insert("stream".into(), serde_json::json!(true));
    Some(next)
}

pub fn normalize_truncated_round(sse: &str) -> String {
    let mut output = String::with_capacity(sse.len());
    for line in sse.split_inclusive('\n') {
        let trimmed = line.trim_end_matches(['\r', '\n']);
        if trimmed.starts_with("data:") {
            let payload = trimmed[5..].trim();
            if payload == "[DONE]" {
                continue;
            }
            if let Ok(mut value) = serde_json::from_str::<serde_json::Value>(payload) {
                let reason = value
                    .get("choices")
                    .and_then(|v| v.as_array())
                    .and_then(|v| v.first())
                    .and_then(|v| v.get("finish_reason"))
                    .and_then(serde_json::Value::as_str);
                if reason == Some("length") {
                    if let Some(choice) = value
                        .get_mut("choices")
                        .and_then(|v| v.as_array_mut())
                        .and_then(|v| v.first_mut())
                        .and_then(|v| v.as_object_mut())
                    {
                        choice.insert("finish_reason".into(), serde_json::Value::Null);
                    }
                    output.push_str("data: ");
                    output.push_str(&value.to_string());
                    output.push_str("\n\n");
                    continue;
                }
            }
        }
        output.push_str(line);
        if !line.ends_with('\n') {
            output.push('\n');
        }
    }
    output
}
