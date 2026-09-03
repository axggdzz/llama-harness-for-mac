# 第六阶段 D：发布前验证入口

`for-mac/scripts/verify.sh` 提供可重复的本地验收入口，依次执行：

1. `cargo fmt --check`
2. `cargo check`
3. `cargo test`
4. `node --check ui/app.js` 和 Tauri `.app` 构建

脚本要求 `for-mac/ui/node_modules` 已通过 `npm ci` 安装。默认 Tauri 构建只产出
`.app`，不会在受限环境中强制执行 DMG；`npm run build:dmg`、签名、公证、安装升级
和真实 Metal/GGUF smoke 仍需在目标 Apple Silicon 机器上完成。
