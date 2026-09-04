# macOS SlotAffinity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a tested Rust SlotAffinity layer that recognizes client headers, assigns stable slots, protects preemptive/tool sessions, persists bindings in Application Support, and injects `n_slots` into gateway requests.

**Architecture:** `SlotAffinity` owns a short-lock in-memory binding table and performs persistence from cloned snapshots outside the lock. The gateway creates it only when `slot_count > 1`, allocates a slot before backend forwarding, and treats eviction as an observable event without invoking KV APIs yet.

**Tech Stack:** Rust 2021, Tokio/Axum, HTTP `HeaderMap`, serde/serde_json, std `Mutex`/`SystemTime`, tempfile-based tests.

**Spec:** `for-mac/docs/2026-09-03-phase3-slot-affinity-design.md`

## Global Constraints

- All Rust/macOS implementation, tests, docs, and build configuration stay under `for-mac/`.
- The `for-win/` Windows C# implementation remains unchanged.
- `slot_count` is at least 1; slot IDs are zero-based.
- Unknown requests use a random slot and never create a persistent binding.
- Persistence failures do not fail request routing; malformed, duplicate, and out-of-range records are skipped.
- No KV save/restore/erase calls are introduced in this phase.

---

### Task 1: SlotAffinity models and header recognition

**Files:**
- Create: `for-mac/src/slot_affinity.rs`
- Modify: `for-mac/src/lib.rs`
- Test: inline `#[cfg(test)]` module in `for-mac/src/slot_affinity.rs`

**Interfaces:**
- Produces `SlotBinding`, `SlotAllocation`, `SlotAffinity::new`, `SlotAffinity::affinity_key`, and read-only snapshot APIs for later tasks.

- [ ] **Step 1: Write the failing test**

Add tests that construct `HeaderMap` values and assert the four priority rules, case-insensitive matching, and `None` for unknown requests. Add a compile-level test that `SlotAffinity::new(2, temp_path)` exposes `slot_count() == 2` and an empty snapshot.

- [ ] **Step 2: Run tests to verify they fail**

Run `cargo test --manifest-path for-mac/Cargo.toml slot_affinity -- --nocapture`.
Expected: compile failure because `slot_affinity` and its public types do not exist.

- [ ] **Step 3: Write minimal implementation**

Define serializable internal binding records and public clones. Implement `affinity_key(&HeaderMap)` with the exact priority in the design, `app_name`, constructor with `slot_count.max(1)`, and empty snapshot/slot count accessors. Export the module from `lib.rs`.

- [ ] **Step 4: Run tests to verify they pass**

Run the same targeted command and expect all header/model tests to pass.

- [ ] **Step 5: Commit**

```bash
git add for-mac/src/lib.rs for-mac/src/slot_affinity.rs
git commit -m "feat(mac): add SlotAffinity models and header detection"
```

### Task 2: Allocation, LRU eviction, preemptive and Tool locking

**Files:**
- Modify: `for-mac/src/slot_affinity.rs`
- Test: inline `#[cfg(test)]` module in `for-mac/src/slot_affinity.rs`

**Interfaces:**
- Consumes Task 1 `SlotBinding` and `SlotAllocation`.
- Produces `allocate`, `set_preemptive`, `is_preemptive`, `mark_tool_locked`, `unmark_tool_locked`, `set_kv_cache`, and `enforce_preemptive_cap`.

- [ ] **Step 1: Write the failing tests**

Add behavior tests for: first key gets an unused slot and repeats reuse it; a third key in two occupied slots evicts the least recently active non-preemptive binding; a preemptive binding is not evicted by a normal key; preemptive count never exceeds `slot_count - 1`; Tool-locked preemptive victims are selected before ordinary preemptive victims; unknown requests return a slot with `key=None`; concurrent allocation of one key leaves one binding.

- [ ] **Step 2: Run tests to verify they fail**

Run `cargo test --manifest-path for-mac/Cargo.toml slot_affinity -- --nocapture`.
Expected: failures for missing allocation and lock-management methods.

- [ ] **Step 3: Write minimal implementation**

Use `std::sync::Mutex<Inner>` with a `HashMap<String, BindingRecord>` and a Tool-lock set. Keep lock-held work bounded: locate an existing binding, free slot, or LRU victim; clone the evicted record; update activity and release the lock before persistence. Enforce the preemptive cap when setting or creating a binding. Use a deterministic short retry loop outside the mutex when every slot is protected, then return a random unbound slot after the configured wait limit.

- [ ] **Step 4: Run tests to verify they pass**

Run targeted tests, then `cargo test --manifest-path for-mac/Cargo.toml` and expect the complete suite to pass.

- [ ] **Step 5: Commit**

```bash
git add for-mac/src/slot_affinity.rs
git commit -m "feat(mac): implement SlotAffinity allocation and eviction"
```

### Task 3: Application Support persistence and recovery

**Files:**
- Modify: `for-mac/src/config.rs`
- Modify: `for-mac/src/slot_affinity.rs`
- Test: inline `#[cfg(test)]` module in `for-mac/src/slot_affinity.rs`

**Interfaces:**
- Consumes Task 2 binding mutations.
- Produces atomic JSON persistence and startup recovery through `SlotAffinity::new`; config exposes an optional `slot_bindings_path` and `auto_preemptive_prefixes` with safe defaults.

- [ ] **Step 1: Write the failing tests**

Add tests that allocate and mutate a binding, construct a second manager from the same temporary path, and assert slot/preemptive/KV fields restore. Add malformed JSON, duplicate slot, and out-of-range slot fixtures and assert they are ignored without panic. Assert writes replace the target through a temporary file/rename path.

- [ ] **Step 2: Run tests to verify they fail**

Run `cargo test --manifest-path for-mac/Cargo.toml slot_affinity -- --nocapture`.
Expected: recovery tests fail because the constructor does not load or persist records.

- [ ] **Step 3: Write minimal implementation**

Add serde persistence structs with version and slot count. Load defensively, reject invalid timestamps and duplicate slots, and default missing `preemptive` to false and `kv_cache` to true. Persist cloned state by writing a sibling temporary file and renaming it over the destination; emit a tracing warning on errors. Extend `AppConfig` defaults without changing existing phase-1/2 behavior.

- [ ] **Step 4: Run tests to verify they pass**

Run targeted persistence tests and the full suite. Expect all prior lifecycle/gateway tests to remain green.

- [ ] **Step 5: Commit**

```bash
git add for-mac/src/config.rs for-mac/src/slot_affinity.rs
git commit -m "feat(mac): persist SlotAffinity bindings atomically"
```

### Task 4: Gateway slot routing and status observability

**Files:**
- Modify: `for-mac/src/config.rs`
- Modify: `for-mac/src/gateway.rs`
- Modify: `for-mac/src/lib.rs`
- Test: `for-mac/tests/gateway_e2e.rs`

**Interfaces:**
- Consumes Task 2/3 `SlotAffinity::allocate` and `SlotAllocation`.
- Produces gateway behavior where recognized requests receive injected `n_slots`, existing caller values are preserved, and status exposes binding snapshots while `slot_count == 1` remains backward-compatible.

- [ ] **Step 1: Write the failing E2E tests**

Extend the mock backend test to send two requests with the same affinity header and assert the backend receives the same `n_slots`. Send a request containing an explicit `n_slots` and assert it is unchanged. Assert `/__status__` reports the binding and an unknown request does not increase binding count.

- [ ] **Step 2: Run tests to verify they fail**

Run `cargo test --manifest-path for-mac/Cargo.toml --test gateway_e2e -- --nocapture`.
Expected: failures because gateway config has no affinity manager and forwarded JSON is unchanged.

- [ ] **Step 3: Write minimal implementation**

Create `Option<Arc<SlotAffinity>>` in `GatewayInner` when configured slot count is greater than one. Allocate before `ensure_backend`, parse JSON object bodies, inject `n_slots` only when absent, and keep all other headers/body semantics unchanged. Add a serializable binding list/count to status. Record eviction through tracing only; defer KV handling.

- [ ] **Step 4: Run verification**

Run `cargo fmt --check`, `cargo check --manifest-path for-mac/Cargo.toml`, `cargo test --manifest-path for-mac/Cargo.toml`, and `git diff --check`. Also verify `git diff origin/main...HEAD -- for-win/LlamaHarness for-win/LlamaHarness.Tests` is empty.

- [ ] **Step 5: Commit**

```bash
git add for-mac/src/config.rs for-mac/src/gateway.rs for-mac/src/lib.rs for-mac/tests/gateway_e2e.rs
git commit -m "feat(mac): route gateway requests through SlotAffinity"
```
