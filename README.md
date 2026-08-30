# Llama Harness — 智能代理网关

> **双槽 KV 复用 · 思考模式拦截 · 崩溃自愈**

## 简介

Llama Harness 是一个 WinForms 桌面应用，作为 llama.cpp（llama-server）的智能代理网关。核心能力：

- **双槽 KV 复用**：多槽并行推理，按请求指纹自动路由到固定槽位，最大化 KV 缓存命中率
- **思考模式拦截**：运行时切换思考/极速模式，智能拦截思考链
- **崩溃自愈**：bad\_alloc 自动恢复、进程崩溃熔断、GPU 显存 OOM 检测
- **手动资源监控**：CPU / 内存 / 显存 + llama.cpp 三接口（/slots /props /metrics）实时采集

## 功能特性

| 模块   | 说明                                                     |
| ---- | ------------------------------------------------------ |
| 日志   | 实时请求日志、Token 统计、运行时长                                   |
| 统计   | 请求数、Token 吞吐、成功率                                       |
| 槽位绑定 | 按请求指纹（DSH规则/WebUI/Trae/DSH Agent）自动路由到固定槽位             |
| 槽位管理 | 多槽状态管理、KV 缓存索引                                         |
| 系统资源 | 手动刷新：CPU/内存/显存 + llama.cpp 三接口（/slots /props /metrics） |
| 配置管理 | 模型路径、端口、ctx、并行槽数、线程、附加参数等                              |
| 信息展示 | 使用说明、常见问题、更新内容（Markdown 渲染）                            |

## 技术栈

- **语言**：C#（.NET，WinForms）
- **后端**：llama.cpp（llama-server），HTTP API 通信
- **GPU**：NVIDIA CUDA（nvidia-smi 采集显存）
- **构建**：MSBuild / Visual Studio

## 项目结构

```
lunch/
├── LlamaHarness/          # 主程序（WinForms）
│   ├── MainForm.cs        # 主窗体（七页签布局）
│   ├── LlamaCppMonitor.cs # llama.cpp 三接口监控（DTO + Collector）
│   ├── SystemMetrics.cs   # CPU/内存/显存采集
│   ├── SlotAffinity.cs    # 槽位亲和路由
│   ├── CrashRecovery.cs   # 崩溃熔断与自愈
│   ├── AppConfig.cs       # 配置管理（config/config.json）
│   └── ...                # 其他模块
├── config/                # 配置文件目录
│   ├── config.json        # 主配置
│   ├── slot_bindings.json # 槽位绑定持久化
│   └── kv_cache_index.json# KV 缓存索引
├── logs/                  # 日志文件目录
│   ├── harness.log        # 主日志
│   ├── slot.log           # 槽位日志
│   ├── warn_error.log     # 警告/错误日志
│   └── unhandled.log      # 未处理异常日志
└── docs/                  # 设计文档
```

## 构建与运行

### 前置条件

- Windows 10+
- .NET SDK（.NET 6+）
- llama.cpp（llama-server 可执行文件）
- NVIDIA GPU + CUDA（可选，用于 GPU 推理）

### 构建

```bash
# MSBuild
msbuild LlamaHarness.sln /p:Configuration=Release

# 或 Visual Studio 打开解决方案后 F5 运行
```

### 运行

1. 启动 `LlamaHarness.exe`
2. 在「配置管理」页签设置模型路径、端口、并行槽数等参数
3. 点击「启动/唤醒」按钮
4. 应用将自动启动 llama-server 并监听指定端口

## 配置说明

配置文件位于 `config/config.json`：

| 字段          | 说明                   | 示例                                           |
| ----------- | -------------------- | -------------------------------------------- |
| exe         | llama-server 可执行文件路径 | `D:\AI\llama.cpp\...\llama-server.exe`       |
| model       | 模型文件路径               | `D:\AI\Models\Qwen3.8-27B-Ridge-3.7bpw.gguf` |
| port        | 监听端口                 | `8080`                                       |
| ctx         | 上下文长度                | `222882`                                     |
| parallel    | 并行槽数                 | `2`                                          |
| threads     | CPU 线程数              | `8`                                          |
| extra\_args | 附加启动参数               | `--host 127.0.0.1 --batch-size 2048`         |

## 架构设计

详见 [docs/架构设计说明书.md](docs/架构设计说明书.md)（v2.5）。

核心模块：

- **MainForm**：七页签 UI（日志/统计/槽位绑定/槽位管理/系统资源/配置管理/信息展示）
- **SlotAffinity**：请求指纹识别 + 槽位路由（P1 DSH规则 → P2 WebUI → P3 Trae → P4 DSH Agent）
- **CrashRecovery**：bad\_alloc 熔断 + GPU OOM 检测 + 自动重启
- **LlamaCppMonitor**：手动触发采集 /slots /props /metrics 三接口，并行请求、独立容错
- **SystemMetrics**：CPU（GetSystemTimes）、内存（GlobalMemoryStatusEx）、显存（nvidia-smi）

## 许可证

MIT License
