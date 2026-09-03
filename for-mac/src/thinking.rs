use serde_json::{Map, Value};

#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ThinkingMode {
    Off,
    Low,
    Medium,
    XHigh,
}

impl Default for ThinkingMode {
    fn default() -> Self {
        Self::Off
    }
}

impl ThinkingMode {
    pub fn effort(self) -> Option<&'static str> {
        match self {
            Self::Off => None,
            Self::Low => Some("low"),
            Self::Medium => Some("medium"),
            Self::XHigh => Some("xhigh"),
        }
    }
}

pub fn apply(body: &mut Value, mode: &mut ThinkingMode) -> bool {
    let Some(object) = body.as_object_mut() else {
        return false;
    };
    let mut changed = false;
    if let Some(messages) = object.get_mut("messages").and_then(Value::as_array_mut) {
        for message in messages.iter_mut().rev() {
            if message.get("role").and_then(Value::as_str) != Some("user") {
                break;
            }
            let Some(content) = message
                .get("content")
                .and_then(Value::as_str)
                .map(str::to_owned)
            else {
                continue;
            };
            let mut next = content.clone();
            let mut selected = None;
            for (command, candidate) in [
                ("开启思考模式", ThinkingMode::XHigh),
                ("关闭思考模式", ThinkingMode::Off),
                ("开启轻度推理模式", ThinkingMode::Low),
                ("开启中度推理模式", ThinkingMode::Medium),
                ("开启深度推理模式", ThinkingMode::XHigh),
            ] {
                if next.contains(command) {
                    next = next.replace(command, "");
                    selected = Some(candidate);
                }
            }
            if let Some(selected) = selected {
                *mode = selected;
                let cleaned = next.trim();
                message["content"] = Value::String(if cleaned.is_empty() {
                    "（思考/推理模式已切换，请简短确认）".to_owned()
                } else {
                    cleaned.to_owned()
                });
                changed = true;
                break;
            }
        }
    }
    if object.remove("thinking").is_some() {
        changed = true;
    }
    if object.remove("reasoning_effort").is_some() {
        changed = true;
    }
    let mut kwargs = match object.remove("chat_template_kwargs") {
        Some(Value::Object(map)) => map,
        Some(value) => {
            changed = true;
            let mut map = Map::new();
            map.insert("_previous".into(), value);
            map
        }
        None => Map::new(),
    };
    if kwargs.remove("reasoning_effort").is_some() {
        changed = true;
    }
    if kwargs.remove("enable_thinking").is_some() {
        changed = true;
    }
    match mode.effort() {
        Some(effort) => {
            kwargs.insert("reasoning_effort".into(), Value::String(effort.into()));
            kwargs.insert("enable_thinking".into(), Value::Bool(true));
        }
        None => {
            kwargs.insert("enable_thinking".into(), Value::Bool(false));
        }
    }
    object.insert("chat_template_kwargs".into(), Value::Object(kwargs));
    let _ = changed;
    true
}

#[cfg(test)]
mod tests {
    use super::{apply, ThinkingMode};
    use serde_json::json;

    #[test]
    fn off_mode_cleans_client_fields_and_disables_thinking() {
        let mut body = json!({"thinking":true,"reasoning_effort":"high","chat_template_kwargs":{"reasoning_effort":"high","enable_thinking":true,"foo":1},"messages":[{"role":"user","content":"hello"}]});
        let mut mode = ThinkingMode::Off;
        assert!(apply(&mut body, &mut mode));
        assert_eq!(body["chat_template_kwargs"]["enable_thinking"], false);
        assert!(body.get("thinking").is_none());
        assert!(body.get("reasoning_effort").is_none());
        assert!(body["chat_template_kwargs"]
            .get("reasoning_effort")
            .is_none());
        assert_eq!(body["chat_template_kwargs"]["foo"], 1);
    }

    #[test]
    fn user_command_switches_mode_and_is_removed_from_prompt() {
        let mut body =
            json!({"messages":[{"role":"user","content":"开启中度推理模式\n请分析这个问题"}]});
        let mut mode = ThinkingMode::Off;
        assert!(apply(&mut body, &mut mode));
        assert_eq!(mode, ThinkingMode::Medium);
        assert_eq!(body["messages"][0]["content"], "请分析这个问题");
        assert_eq!(body["chat_template_kwargs"]["reasoning_effort"], "medium");
        assert_eq!(body["chat_template_kwargs"]["enable_thinking"], true);
    }

    #[test]
    fn xhigh_and_low_modes_map_to_expected_effort() {
        for (command, expected, mode) in [
            ("开启思考模式", "xhigh", ThinkingMode::XHigh),
            ("开启深度推理模式", "xhigh", ThinkingMode::XHigh),
            ("开启轻度推理模式", "low", ThinkingMode::Low),
        ] {
            let mut body = json!({"messages":[{"role":"user","content":command}]});
            let mut current = ThinkingMode::Off;
            apply(&mut body, &mut current);
            assert_eq!(current, mode);
            assert_eq!(body["chat_template_kwargs"]["reasoning_effort"], expected);
        }
    }
}
