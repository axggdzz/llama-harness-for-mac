# 第六阶段 A：macOS 仪表盘 UI 基础层

`for-mac/ui/` 提供 Tauri 2 可复用的静态前端资源，保留 Windows 版的信息架构：左侧控制面板、七个主页签、暗色层级和底部统计卡片。页面通过标准 HTTP API 消费网关状态，不直接耦合 Rust 内部结构：

- `/__status__`：生命周期、后端状态、SlotAffinity 绑定。
- `/__stats__`：请求/token/restore/slot 统计。
- `/__resources__`：CPU、统一内存和 Metal 能力说明。
- `/__backend/slots`、`/__backend/props`：后端原始探针。

该阶段只固定布局和数据绑定；Tauri 2 壳层、原生配置编辑、菜单、签名和打包属于后续阶段。
