#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root_dir="$(cd "$script_dir/.." && pwd)"

cd "$root_dir"
echo "[1/4] cargo fmt --check"
cargo fmt --check
echo "[2/4] cargo check"
cargo check
echo "[3/4] cargo test"
cargo test
echo "[4/4] UI syntax and Tauri app bundle"
node --check ui/app.js
if [[ ! -x ui/node_modules/.bin/tauri ]]; then
  echo "Tauri CLI is missing; run npm ci in for-mac/ui first." >&2
  exit 1
fi
cd ui
npm run build
echo "Verification passed. DMG/signing/notarization remain machine-specific checks."
