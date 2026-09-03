# 第六阶段 B：Tauri 2 壳层

`ui/` 现在包含 Tauri 2 配置和 npm 脚本，`src-tauri/` 提供 macOS 窗口、菜单和 DMG/App bundle 打包入口。构建前会将三个静态资源复制到 `ui/dist/`，避免把 `node_modules` 打进 Tauri 资源目录。UI 通过 `llamaGatewayBase` localStorage 变量选择网关地址，默认 `http://127.0.0.1:8080`；网关使用本地 CORS 层允许 Tauri webview 读取状态、统计、资源和原始探针接口。

在 `for-mac/ui` 执行 `npm install && npm run build` 可构建未签名 `.app`；`npm run build:dmg` 额外生成 DMG（需要当前 macOS 的 `hdiutil`/打包工具）。构建需要 macOS 主机安装 Node.js、Rust 目标和 Tauri 系统依赖；当前 Rust 核心测试不依赖这些 GUI 工具链。发布前还需在 Apple Silicon 主机进行签名、公证和 Metal 真机验证。

Tauri 壳层现在直接以 path dependency 托管 Rust gateway：启动 `.app` 会在
`127.0.0.1:8080` 启动网关，退出应用时发送 shutdown 信号并回收后端进程组。
若端口已被独立 daemon 占用，应用会打印错误并退出，避免产生两个网关实例。

本机已验证 `.app` 构建成功；DMG 命令能够生成应用并进入 `hdiutil`，但当前受限运行环境返回“设备未配置”，因此 DMG 需在可用磁盘设备的登录 macOS 环境重新执行。
