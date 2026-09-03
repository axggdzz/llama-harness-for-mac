use anyhow::{anyhow, Context, Result};
use fs2::FileExt;
use std::{
    fs::{File, OpenOptions},
    path::Path,
};

pub struct InstanceLock {
    file: File,
}

impl InstanceLock {
    pub fn acquire(path: impl AsRef<Path>) -> Result<Self> {
        let path = path.as_ref();
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)
                .with_context(|| format!("create lock directory {}", parent.display()))?;
        }
        let file = OpenOptions::new()
            .create(true)
            .read(true)
            .write(true)
            .open(path)?;
        file.try_lock_exclusive().map_err(|error| {
            anyhow!("another LlamaHarness instance is already running: {error}")
        })?;
        Ok(Self { file })
    }
}

impl Drop for InstanceLock {
    fn drop(&mut self) {
        let _ = self.file.unlock();
    }
}

#[cfg(test)]
mod tests {
    use super::InstanceLock;

    #[test]
    fn second_lock_on_same_path_is_rejected() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("gateway.lock");
        let first = InstanceLock::acquire(&path).unwrap();
        assert!(InstanceLock::acquire(&path).is_err());
        drop(first);
        assert!(InstanceLock::acquire(&path).is_ok());
    }
}
