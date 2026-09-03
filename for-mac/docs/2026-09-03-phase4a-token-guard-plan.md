# macOS TokenGuard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add real-tokenize context protection and deterministic message/content trimming to the macOS gateway.

**Architecture:** A pure `TokenGuard` module receives a mutable JSON request and an injectable async token counter. Gateway integration supplies a reqwest counter pointed at the ready backend and applies the guard after slot routing but before completion forwarding.

**Tech Stack:** Rust 2021, serde_json, Tokio, reqwest, Axum, anyhow.

**Spec:** `for-mac/docs/2026-09-03-phase4a-token-guard-design.md`

## Global Constraints

- All implementation, tests, docs, and configuration stay under `for-mac/`.
- Windows C# baseline remains unchanged.
- Real token counting uses backend `/v1/tokenize`; counter failures degrade to original request.
- Preserve system prefix and latest conversation turn; never split a turn containing tool messages.
- Context overflow rejection is HTTP 400 and must not reach completion endpoint.
- 400 retry/KV invalidation is out of scope for this sub-stage.

---

### Task 1: Pure TokenGuard budget and trimming

**Files:** Create `for-mac/src/token_guard.rs`; modify `for-mac/src/lib.rs`.

- [ ] Write failing tests for budget calculation, disabled/no-message pass-through, token-counter failure fallback, whole-turn binary trimming, tool-message retention, large-content head/tail trimming, and final over-budget rejection.
- [ ] Run `cargo test --manifest-path for-mac/Cargo.toml token_guard -- --nocapture` and observe missing module/API failures.
- [ ] Implement `TokenGuardConfig`, `GuardReport`, token text construction, binary turn search, content fallback, and error type.
- [ ] Re-run targeted tests and then the full library suite.
- [ ] Commit `feat(mac): add TokenGuard budget and trimming core`.

### Task 2: HTTP tokenize counter and configuration

**Files:** Modify `for-mac/src/config.rs`; modify `for-mac/src/token_guard.rs`.

- [ ] Write failing tests for `/v1/tokenize` `tokens` array, `n_tokens`, non-2xx, and malformed responses.
- [ ] Run targeted tests and observe missing HTTP counter behavior.
- [ ] Implement `count_tokens` with bounded request/body timeouts and structured errors; add safe disabled-by-default AppConfig fields.
- [ ] Run unit tests and `cargo check`.
- [ ] Commit `feat(mac): add real HTTP tokenize counter`.

### Task 3: Gateway preflight integration and mock E2E

**Files:** Modify `for-mac/src/gateway.rs`; modify `for-mac/src/bin/mock-llama-server.rs`; modify `for-mac/tests/gateway_e2e.rs`; add `for-mac/tests/token_guard_e2e.rs`.

- [ ] Write failing E2E tests for a configured small context budget: mock tokenize reports over-budget, backend receives trimmed messages; an untrim-able request returns 400 and completion is not called; disabled guard preserves current behavior.
- [ ] Run E2E tests and observe absent gateway preflight and mock tokenize route.
- [ ] Integrate guard after slot injection, preserve explicit `n_slots`, emit guard logs, and return structured 400 rejection.
- [ ] Add mock `/v1/tokenize` and request counters; assert external behavior through HTTP.
- [ ] Run `cargo fmt --check`, `cargo check`, `cargo test`, `git diff --check`, and verify Windows diff is empty.
- [ ] Commit `feat(mac): enforce TokenGuard before completion forwarding`.
