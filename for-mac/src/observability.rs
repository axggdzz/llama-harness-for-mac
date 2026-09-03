#[cfg(test)]
mod tests {
    use super::{LogKind, RotatingLogger, Stats};
    use std::sync::Arc;

    #[test]
    fn logger_writes_separate_main_slot_and_error_files_and_rotates() {
        let dir = tempfile::tempdir().unwrap();
        let logger = RotatingLogger::new(dir.path(), 32).unwrap();
        logger.write(LogKind::Main, "main event").unwrap();
        logger.write(LogKind::Slot(2), "slot event").unwrap();
        logger.write(LogKind::Error, "error event").unwrap();
        logger.write(LogKind::Main, &"x".repeat(128)).unwrap();
        assert!(dir.path().join("main.log").is_file());
        assert!(dir.path().join("slot-2.log").is_file());
        assert!(dir.path().join("errors.log").is_file());
        assert!(dir.path().join("main.log.1").is_file());
    }

    #[test]
    fn stats_snapshot_tracks_tokens_speed_restore_slots_and_requests() {
        let stats = Arc::new(Stats::default());
        stats.record_request();
        stats.record_tokens(12, 4, 8);
        stats.record_restore(true);
        stats.record_slot(1);
        let snapshot = stats.snapshot();
        assert_eq!(snapshot.requests, 1);
        assert_eq!(snapshot.prompt_tokens, 12);
        assert_eq!(snapshot.completion_tokens, 4);
        assert_eq!(snapshot.restore_hits, 1);
        assert_eq!(snapshot.slots_in_use, 1);
    }
}
use anyhow::Result;
use serde::Serialize;
use std::{
    collections::HashSet,
    fs::{self, OpenOptions},
    io::Write,
    path::PathBuf,
    sync::{
        atomic::{AtomicU64, Ordering},
        Mutex,
    },
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Debug, Clone, Copy)]
pub enum LogKind {
    Main,
    Slot(usize),
    Error,
}

pub struct RotatingLogger {
    dir: PathBuf,
    max_bytes: u64,
    lock: Mutex<()>,
}

impl RotatingLogger {
    pub fn new(dir: impl Into<PathBuf>, max_bytes: u64) -> Result<Self> {
        let dir = dir.into();
        fs::create_dir_all(&dir)?;
        Ok(Self {
            dir,
            max_bytes: max_bytes.max(1),
            lock: Mutex::new(()),
        })
    }

    pub fn write(&self, kind: LogKind, message: &str) -> Result<()> {
        let _guard = self.lock.lock().expect("logger mutex poisoned");
        let path = self.path(kind);
        let line = format!("{} {}\n", timestamp(), message);
        let current = fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
        if current.saturating_add(line.len() as u64) > self.max_bytes {
            let rotated = path.with_extension(format!(
                "{}1",
                path.extension()
                    .and_then(|e| e.to_str())
                    .map(|e| format!("{e}."))
                    .unwrap_or_default()
            ));
            let _ = fs::remove_file(&rotated);
            if path.is_file() {
                fs::rename(&path, rotated)?;
            }
        }
        let mut file = OpenOptions::new().create(true).append(true).open(path)?;
        file.write_all(line.as_bytes())?;
        file.flush()?;
        Ok(())
    }

    fn path(&self, kind: LogKind) -> PathBuf {
        match kind {
            LogKind::Main => self.dir.join("main.log"),
            LogKind::Slot(slot) => self.dir.join(format!("slot-{slot}.log")),
            LogKind::Error => self.dir.join("errors.log"),
        }
    }
}

#[derive(Debug, Clone, Serialize, Default)]
pub struct StatsSnapshot {
    pub requests: u64,
    pub prompt_tokens: u64,
    pub completion_tokens: u64,
    pub speed_tokens_per_second: u64,
    pub restore_hits: u64,
    pub restore_misses: u64,
    pub slots_in_use: usize,
}

#[derive(Default)]
pub struct Stats {
    requests: AtomicU64,
    prompt_tokens: AtomicU64,
    completion_tokens: AtomicU64,
    speed_tokens_per_second: AtomicU64,
    restore_hits: AtomicU64,
    restore_misses: AtomicU64,
    slots: Mutex<HashSet<usize>>,
}

impl Stats {
    pub fn record_request(&self) {
        self.requests.fetch_add(1, Ordering::Relaxed);
    }
    pub fn record_tokens(&self, prompt: u64, completion: u64, speed_tokens_per_second: u64) {
        self.prompt_tokens.fetch_add(prompt, Ordering::Relaxed);
        self.completion_tokens
            .fetch_add(completion, Ordering::Relaxed);
        self.speed_tokens_per_second
            .store(speed_tokens_per_second, Ordering::Relaxed);
    }
    pub fn record_restore(&self, hit: bool) {
        if hit {
            self.restore_hits.fetch_add(1, Ordering::Relaxed);
        } else {
            self.restore_misses.fetch_add(1, Ordering::Relaxed);
        }
    }
    pub fn record_slot(&self, slot: usize) {
        self.slots
            .lock()
            .expect("stats mutex poisoned")
            .insert(slot);
    }
    pub fn snapshot(&self) -> StatsSnapshot {
        StatsSnapshot {
            requests: self.requests.load(Ordering::Relaxed),
            prompt_tokens: self.prompt_tokens.load(Ordering::Relaxed),
            completion_tokens: self.completion_tokens.load(Ordering::Relaxed),
            speed_tokens_per_second: self.speed_tokens_per_second.load(Ordering::Relaxed),
            restore_hits: self.restore_hits.load(Ordering::Relaxed),
            restore_misses: self.restore_misses.load(Ordering::Relaxed),
            slots_in_use: self.slots.lock().expect("stats mutex poisoned").len(),
        }
    }
}

fn timestamp() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}
