## Agent skills

### Issue tracker

Issues and specs live in this repository's GitHub Issues. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context layout with a root `CONTEXT.md` and `docs/adr/`. See `docs/agents/domain.md`.

### Platform separation

The repository root is the Windows C# baseline. The Rust/macOS port lives exclusively under `for-mac/`; keep implementation, tests, and Mac-specific documentation there. See `for-mac/README.md`.
