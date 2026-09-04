# macOS KV Cache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a tested KV snapshot manager with llama-server slot operations, atomic metadata/index persistence, integrity validation, and mock-backed end-to-end coverage.

**Architecture:** `KvCacheManager` owns an async HTTP client, a synchronized snapshot index, and a per-key in-flight save map. Backend calls stay async; file hashing and atomic writes run in blocking tasks. SlotAffinity remains the routing owner and is not automatically coupled to KV in this phase.

**Tech Stack:** Rust 2021, Tokio, reqwest, serde_json, sha2, tempfile, Axum mock server.

**Spec:** `for-mac/docs/2026-09-03-phase3b-kv-cache-design.md`

## Global Constraints

- All implementation, tests, docs and configuration remain under `for-mac/`.
- Windows C# files remain untouched.
- Backend calls use `/slots/{slot}?action=save|restore|erase` with JSON filename bodies.
- Save requires a non-empty snapshot and positive saved token count.
- Restore never calls the backend when local integrity or metadata checks fail.
- Persistence uses atomic sibling temp-file rename; malformed index degrades to empty state.

---

### Task 1: Snapshot types, safe key paths, and index persistence

**Files:** Create `for-mac/src/kv_cache.rs`; modify `for-mac/src/lib.rs`.

- [ ] Write failing tests for key sanitization, empty snapshots, index serialization/reload, and malformed index fallback.
- [ ] Run `cargo test --manifest-path for-mac/Cargo.toml kv_cache -- --nocapture` and observe missing-module failures.
- [ ] Implement `KvSnapshot`, manager constructor, safe filename derivation, synchronized index, and atomic index load/save.
- [ ] Re-run targeted tests until green.
- [ ] Commit `feat(mac): add KV snapshot index and metadata types`.

### Task 2: Save and restore operations with integrity validation

**Files:** Modify `for-mac/src/kv_cache.rs`; test in the module.

- [ ] Write failing tests using an Axum test server for save response parsing, metadata/hash creation, concurrent same-key save deduplication, restore success, and corrupt/mismatched snapshot rejection.
- [ ] Run targeted tests and observe failures for missing HTTP operations.
- [ ] Implement save/restore, SHA-256 hashing, metadata validation, per-key in-flight save sharing, and structured errors.
- [ ] Re-run targeted tests and then the full library suite.
- [ ] Commit `feat(mac): implement KV save and restore validation`.

### Task 3: Erase and clear-all lifecycle

**Files:** Modify `for-mac/src/kv_cache.rs`; test in the module.

- [ ] Write failing tests for erase forwarding, delete snapshot, clear-all deletion of binary/metadata files, index reset, and continuation after one failed erase.
- [ ] Run targeted tests and observe missing erase/clear behavior.
- [ ] Implement erase, `delete_snapshot`, and `clear_all` with best-effort per-slot handling and index persistence.
- [ ] Run all KV tests and full `cargo test`.
- [ ] Commit `feat(mac): add KV erase and clear-all operations`.

### Task 4: Mock backend coverage and documentation

**Files:** Modify `for-mac/src/bin/mock-llama-server.rs`; add `for-mac/tests/kv_cache_e2e.rs`; modify `for-mac/README.md`.

- [ ] Write failing E2E tests that start the mock server and exercise save, restore, erase and corrupt-file fallback through real HTTP.
- [ ] Run the E2E test and observe missing mock slot endpoints.
- [ ] Add mock POST slot routes with deterministic save/restore responses and implement the E2E assertions.
- [ ] Document the KV environment/configuration path and manual operations.
- [ ] Run `cargo fmt --check`, `cargo check`, `cargo test`, `git diff --check`, and verify Windows diff is empty.
- [ ] Commit `feat(mac): cover KV slot operations with mock backend`.

