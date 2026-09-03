#[cfg(test)]
mod tests {
    use super::{parse_vm_stat, ResourceSnapshot};

    #[test]
    fn parses_macos_vm_stat_pages() {
        let sample = "Pages free:                               100.\nPages active:                           200.\nPages inactive:                         50.\nPages speculative:                      10.\n";
        let (free, active, inactive, speculative) = parse_vm_stat(sample);
        assert_eq!((free, active, inactive, speculative), (100, 200, 50, 10));
    }

    #[test]
    fn snapshot_serializes_capability_note() {
        let snapshot = ResourceSnapshot::unavailable("test");
        assert!(!snapshot.available);
        assert_eq!(snapshot.gpu_backend, "test");
    }
}

use serde::Serialize;
use std::process::Command;

#[derive(Debug, Clone, Serialize)]
pub struct ResourceSnapshot {
    pub available: bool,
    pub cpu_usage_percent: f32,
    pub total_memory_bytes: u64,
    pub used_memory_bytes: u64,
    pub memory_pressure_percent: f32,
    pub gpu_backend: String,
}

impl ResourceSnapshot {
    pub fn collect() -> Self {
        let mut system = sysinfo::System::new_all();
        system.refresh_all();
        let total = system.total_memory().saturating_mul(1024);
        let used = system.used_memory().saturating_mul(1024);
        let cpu = system.global_cpu_info().cpu_usage();
        Self {
            available: true,
            cpu_usage_percent: cpu,
            total_memory_bytes: total,
            used_memory_bytes: used,
            memory_pressure_percent: if total == 0 {
                0.0
            } else {
                used as f32 * 100.0 / total as f32
            },
            gpu_backend: "Metal/统一内存（显存独立指标不可用）".to_owned(),
        }
    }

    pub fn unavailable(note: &str) -> Self {
        Self {
            available: false,
            cpu_usage_percent: 0.0,
            total_memory_bytes: 0,
            used_memory_bytes: 0,
            memory_pressure_percent: 0.0,
            gpu_backend: note.to_owned(),
        }
    }

    pub fn macos_vm_stat() -> Option<(u64, u64, u64, u64)> {
        let output = Command::new("/usr/bin/vm_stat").output().ok()?;
        if !output.status.success() {
            return None;
        }
        let text = String::from_utf8(output.stdout).ok()?;
        Some(parse_vm_stat(&text))
    }
}

fn parse_vm_stat(text: &str) -> (u64, u64, u64, u64) {
    fn value(text: &str, prefix: &str) -> u64 {
        text.lines()
            .find_map(|line| line.strip_prefix(prefix))
            .and_then(|v| v.trim().trim_end_matches('.').parse().ok())
            .unwrap_or(0)
    }
    (
        value(text, "Pages free:"),
        value(text, "Pages active:"),
        value(text, "Pages inactive:"),
        value(text, "Pages speculative:"),
    )
}
