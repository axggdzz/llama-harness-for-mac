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
