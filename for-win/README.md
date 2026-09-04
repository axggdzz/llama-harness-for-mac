# LlamaHarness for Windows

此目录包含项目的 Windows C# WinForms 基线实现。它是 macOS Rust 版本的功能和 UI 参考，维护时应与 [`for-mac/`](../for-mac/) 分开处理。

## 目录

- `LlamaHarness/`：.NET 8 WinForms 主程序
- `LlamaHarness.Tests/`：xUnit 测试工程
- `docs/`：Windows 架构、设计和验收文档

## 构建与测试

在 `for-win/` 目录执行：

```powershell
dotnet build LlamaHarness/LlamaHarness.csproj -c Release
dotnet test LlamaHarness.Tests/LlamaHarness.Tests.csproj -c Release
```

运行产物位于 `LlamaHarness/bin/Release/net8.0-windows/`。Windows 版本需要 Windows 10/11、.NET 8 SDK、CUDA 版 `llama-server.exe` 和 NVIDIA GPU。

## 基线约束

- Windows 实现使用 WinForms、Windows 进程组、P 核亲和和 NVIDIA 显存采集。
- macOS 迁移代码不得写入此目录；macOS 专用实现、测试、文档和构建配置全部放在 `for-mac/`。
- 根目录的 [`README.md`](../README.md) 说明两个平台的入口和差异。
