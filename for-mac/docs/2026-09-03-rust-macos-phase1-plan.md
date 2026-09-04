# Rust/macOS Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a buildable Rust/macOS gateway that starts a mock or real llama-server on demand, waits for readiness, forwards OpenAI requests including SSE, and cleans up the Unix process group.

**Architecture:** One Cargo package under `for-mac` with focused Rust modules (`config`, `lifecycle`, `process`, `gateway`) and a `mock-llama-server` binary. The gateway owns a shared backend-start future so concurrent inference requests trigger one startup; process management is isolated behind a trait-like factory for black-box tests.

**Tech Stack:** Rust 2021, Tokio, Axum, Reqwest, Serde/Serde JSON, libc, directories, tempfile, tokio-stream.

**Spec:** `for-mac/docs/2026-09-03-rust-macos-phase1-design.md`

## Global Constraints

- Keep all Rust/macOS implementation, tests, docs, and build configuration under `for-mac/`.
- Do not modify or delete the root `LlamaHarness/` Windows implementation.
- Bind the public gateway to `127.0.0.1:8080`; backend uses an independent port.
- Spawn commands from argument arrays; never build a shell command string.
- Use Unix process groups with SIGTERM then timed SIGKILL cleanup.
- Run `cargo fmt --check`, `cargo check`, and `cargo test` after each task.

---

### Task 1: Cargo foundation and lifecycle/config models

**Files:**
- Modify: `for-mac/Cargo.toml`
- Modify: `for-mac/src/main.rs`
- Create: `for-mac/src/lib.rs`
- Create: `for-mac/src/config.rs`
- Create: `for-mac/src/lifecycle.rs`
- Test: inline unit tests in `config.rs` and `lifecycle.rs`

**Interfaces:**
- Produces `AppConfig`, `BackendConfig`, `LifecyclePhase`, and phase transition validation for later modules.

- [ ] Step 1: Add dependencies and module declarations without changing Windows files.
- [ ] Step 2: Write failing tests for macOS default Application Support path selection, fixed gateway port 8080, and valid/invalid phase transitions.
- [ ] Step 3: Run `cd for-mac && cargo test config lifecycle`; verify failures are due to missing types/behavior.
- [ ] Step 4: Implement minimal serde-backed config defaults and lifecycle transition table.
- [ ] Step 5: Run `cargo fmt --check && cargo check && cargo test`; verify green.
- [ ] Step 6: Commit `feat(mac): add phase1 config and lifecycle models`.

### Task 2: Unix backend process manager

**Files:**
- Create: `for-mac/src/process.rs`
- Modify: `for-mac/src/lib.rs`
- Test: unit tests in `process.rs`

**Interfaces:**
- `BackendProcess::start(BackendConfig) -> Result<BackendHandle>`
- `BackendHandle::wait_ready() -> Result<()>`
- `BackendHandle::stop() -> Result<()>`

- [ ] Step 1: Write failing tests using the mock binary command for argument preservation and readiness success/timeout.
- [ ] Step 2: Run targeted tests and confirm the expected missing-process-manager failures.
- [ ] Step 3: Implement Tokio `Command` with `process_group(0)`/`setpgid`, stdout/stderr capture, `/health` polling, and SIGTERM→SIGKILL timeout cleanup.
- [ ] Step 4: Re-run targeted tests, then the full format/check/test commands.
- [ ] Step 5: Commit `feat(mac): manage llama server process groups`.

### Task 3: Mock llama-server fixture

**Files:**
- Create: `for-mac/src/bin/mock-llama-server.rs`
- Create: `for-mac/tests/mock_server.rs`

**Interfaces:**
- Binary accepts `--port`, `--startup-delay-ms`, and `--sse`; serves `/health`, `/v1/chat/completions`, `/slots`, `/props`, and `/metrics`.

- [ ] Step 1: Write black-box tests for delayed readiness, JSON chat completion, and SSE event framing.
- [ ] Step 2: Run tests and observe failure because the binary/routes do not exist.
- [ ] Step 3: Implement the minimal Axum mock with deterministic JSON and `data: ...\\n\\n` SSE events ending in `data: [DONE]\\n\\n`.
- [ ] Step 4: Run the mock tests and all cargo checks.
- [ ] Step 5: Commit `test(mac): add controllable mock llama server`.

### Task 4: Fixed-port gateway and request forwarding

**Files:**
- Create: `for-mac/src/gateway.rs`
- Modify: `for-mac/src/lib.rs`
- Modify: `for-mac/src/main.rs`
- Create: `for-mac/tests/gateway_e2e.rs`

**Interfaces:**
- `Gateway::new(config, backend_factory) -> Gateway`
- `Gateway::serve(listener) -> Result<()>`
- `GET /__status__`, `GET /health`, and `/v1/*` proxy routes.

- [ ] Step 1: Write failing end-to-end tests for fixed `127.0.0.1:8080`, automatic backend startup, ordinary JSON forwarding, and byte-preserved SSE.
- [ ] Step 2: Run the tests and verify failures are caused by absent gateway behavior.
- [ ] Step 3: Implement Axum routes, shared startup future, request method/header/body forwarding, and streaming response body passthrough.
- [ ] Step 4: Add structured 503/502 responses for standby/startup failure and status JSON exposing lifecycle phase/backend port.
- [ ] Step 5: Run `cargo fmt --check && cargo check && cargo test` and fix any race-sensitive test setup.
- [ ] Step 6: Commit `feat(mac): add fixed-port OpenAI gateway`.

### Task 5: Graceful shutdown and phase-1 documentation

**Files:**
- Modify: `for-mac/src/gateway.rs`
- Modify: `for-mac/src/process.rs`
- Create: `for-mac/tests/shutdown_e2e.rs`
- Modify: `for-mac/README.md`

- [ ] Step 1: Write a failing shutdown test that starts the gateway and mock backend, drops the gateway, and verifies the child PID exits.
- [ ] Step 2: Implement shutdown signaling, listener drain, backend stop/join, and test-only PID reporting.
- [ ] Step 3: Run the shutdown test and then the complete validation commands.
- [ ] Step 4: Document phase-1 commands, mock-server usage, and limitations in `for-mac/README.md`.
- [ ] Step 5: Commit `feat(mac): complete phase1 gateway milestone`.
