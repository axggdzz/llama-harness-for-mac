# 第六阶段 B：Tauri 2 壳层

`ui/` 现在包含 Tauri 2 配置和 npm 脚本，`src-tauri/` 提供 macOS 窗口、菜单和 DMG/App bundle 打包入口。构建前会将三个静态资源复制到 `ui/dist/`，避免把 `node_modules` 打进 Tauri 资源目录。UI 通过 `llamaGatewayBase` localStorage 变量选择网关地址，默认 `http://127.0.0.1:8080`；网关使用本地 CORS 层允许 Tauri webview 读取状态、统计、资源和原始探针接口。

在 `for-mac/ui` 执行 `npm install && npm run build` 可构建未签名 `.app`；`npm run build:dmg` 额外生成 DMG（需要当前 macOS 的 `hdiutil`/打包工具）。构建需要 macOS 主机安装 Node.js、Rust 目标和 Tauri 系统依赖；当前 Rust 核心测试不依赖这些 GUI 工具链。发布前还需在 Apple Silicon 主机进行签名、公证和 Metal 真机验证。
