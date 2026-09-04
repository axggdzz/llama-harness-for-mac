# 第六阶段 D：发布前验证入口

`for-mac/scripts/verify.sh` 提供可重复的本地验收入口，依次执行：

1. `cargo fmt --check`
2. `cargo check`
3. `cargo test`
4. `node --check ui/app.js` 和 Tauri `.app` 构建

脚本要求 `for-mac/ui/node_modules` 已通过 `npm ci` 安装。默认 Tauri 构建只产出
`.app`，不会在受限环境中强制执行 DMG；`npm run build:dmg`、签名、公证、安装升级
和真实 Metal/GGUF smoke 仍需在目标 Apple Silicon 机器上完成。

本机已确认 `/opt/homebrew/bin/llama-server` 为 `0.3.0`、AppleClang、Darwin
`arm64` 构建；用缺失模型路径启动时能输出明确的 GGUF 加载错误并退出，说明
提前退出检测链路可用。Hugging Face 测试模型下载在当前网络环境超时，因此真实
推理、SSE、KV 和休眠唤醒仍待模型文件可获取后执行。

使用该真实二进制接入 Rust 网关的失败路径也已验证：请求返回结构化 `502`
（`backend exited before readiness`），网关退出后不保留测试后端。

llama.cpp 0.3.0 的 KV API 还要求启动参数包含 `--slot-save-path <dir>`，请求中的
`filename` 必须是 basename。配置解析会自动识别该参数，保存时向后端发送 basename，
再将后端目录中的文件复制到 Application Support 本地缓存；`n_saved=0` 但
`n_written>0` 的响应按有效快照处理。
