# 第六阶段 B：Tauri 2 壳层

`ui/` 现在包含 Tauri 2 配置和 npm 脚本，`src-tauri/` 提供 macOS 窗口、菜单和 DMG/App bundle 打包入口。UI 通过 `llamaGatewayBase` localStorage 变量选择网关地址，默认 `http://127.0.0.1:8080`；网关使用本地 CORS 层允许 Tauri webview 读取状态、统计、资源和原始探针接口。

Tauri CLI 构建需要在 macOS 主机安装 Node.js、Rust 目标和 Tauri 系统依赖；当前 Rust 核心测试不依赖这些 GUI 工具链。发布前需在 Apple Silicon 主机执行 `npm install && npm run build`，再进行签名、公证和 Metal 真机验证。
