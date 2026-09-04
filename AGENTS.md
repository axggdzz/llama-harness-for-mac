## Agent skills

### Issue tracker

Issues and specs live in this repository's GitHub Issues. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context layout with a root `CONTEXT.md` and `docs/adr/`. See `docs/agents/domain.md`.

### Platform separation

The Windows C# baseline lives under `for-win/`, while the Rust/macOS port lives
exclusively under `for-mac/`. Keep implementation, tests, platform-specific
documentation, and build configuration in the corresponding directory. See
`for-win/README.md` and `for-mac/README.md`.
