# LlamaHarness for macOS

This directory contains the Rust/macOS port. The original Windows WinForms implementation remains in the repository root and must not be mixed with this port.

## Scope

- Rust daemon and platform-independent harness core
- macOS process, filesystem, monitoring, and Metal adapters
- macOS desktop UI that preserves the Windows dashboard information architecture
- macOS-specific tests and packaging metadata

The migration specification is tracked in [`docs/spec.md`](docs/spec.md) and published as [GitHub Issue #1](https://github.com/axggdzz/llama-harness-for-mac/issues/1).

## Layout

```text
for-mac/
├── Cargo.toml       # Rust package/workspace entry point
├── src/              # Rust/macOS implementation only
├── tests/            # macOS and mock llama-server tests
└── docs/             # macOS port decisions and verification notes
```

Do not edit the Windows C# implementation as part of the macOS port unless a compatibility note explicitly requires it.

## Development

Build and test from this directory:

```bash
cd for-mac
cargo build
cargo test
```

The first milestone should use a mock llama-server so core gateway behavior can be tested without a model or Metal GPU.

## Phase 1 quick start

The daemon listens on `127.0.0.1:8080`. Configure a real Metal-enabled
`llama-server` through environment variables, then run:

```bash
export LLAMA_SERVER=/absolute/path/to/llama-server
export LLAMA_BACKEND_PORT=8081
export LLAMA_SERVER_ARGS='--model /absolute/path/to/model.gguf --port 8081'
cargo run
```

The first `POST /v1/chat/completions` request starts the backend and waits for
`GET /health`. `stream: true` responses are proxied as byte-preserving SSE.
Press `Ctrl-C` to stop the gateway and the entire Unix backend process group.

For a model-free smoke test, run the mock server directly:

```bash
cargo run --bin mock-llama-server -- --port 18081 --startup-delay-ms 100
curl http://127.0.0.1:18081/v1/chat/completions \
  -H 'content-type: application/json' \
  -d '{"model":"mock","messages":[{"role":"user","content":"hi"}]}'
```

Phase 1 and 2 intentionally do not include the dashboard UI, SlotAffinity, KV
snapshots, TokenGuard, continuation, or crash-recovery policies; those are the
next migration stages described in `docs/spec.md`. Phase 2 adds the
`Standby/Waking/Warming/Running/Sleeping` scheduler, configurable idle sleep,
and cancellation of the sleep observation window when a new request arrives.

Phase 3A adds SlotAffinity header recognition, stable `n_slots` routing, LRU
eviction, preemptive/Tool locking, and atomic binding persistence. KV snapshot
operations remain reserved for Phase 3B.

## Phase 3B KV snapshots

The `kv_cache::KvCacheManager` manages llama-server slot snapshots under a
macOS Application Support-compatible cache directory. It validates file size,
token count, metadata, and SHA-256 before restore; malformed or modified
snapshots are rejected and removed. The manager exposes asynchronous
`save`, `restore`, `erase`, `clear_all`, `snapshot`, and `delete_snapshot`
operations and persists an atomic `kv_cache_index.json`.

## Phase 4A TokenGuard

TokenGuard is enabled with `token_guard_enabled` and `context_size` in
`AppConfig`. Before forwarding a JSON chat request, the gateway calls the
backend's `/v1/tokenize`, reserves output and prompt-headroom tokens per slot,
removes old turns, and trims oversized string content while preserving the
latest turn. Tokenizer failures degrade to the original request; requests that
remain over budget receive a structured HTTP 400 response. The mock backend
implements `/v1/tokenize`, and `tests/token_guard_e2e.rs` covers successful
trimming and rejection.

Phase 4B adds one-shot context-overflow recovery. A backend 400 containing a
known context-overflow marker causes the assigned slot to be erased and the
same request to be retried once; unrelated 400 responses and a second failure
are passed through unchanged. Set `context_overflow_recovery=false` to disable
this behavior.
