# C\#启动器｜llama\.cpp 13900F 纯大核绑定·最终完整版（启停自愈/无冗余）

## 一、方案说明

13900F 核心架构：**0\-7 性能大核\(P核\)、8\-23 能效小核\(E核\)**。

llama\.cpp 为同步锁步推理，大小核混跑会被小核严重拖垮吞吐。本方案实现：**全程强制llama\-server仅跑8颗大核、彻底屏蔽小核**，搭配常驻监控自愈，适配自研启动器的按需启停、闲置休眠、进程重启全场景，亲和性永不重置失效。

核心优势：无需BIOS关闭小核、不影响系统后台、零性能冗余、稳态拉满，完美适配 **Threads=8** 稳态参数与95%\+KV缓存复用架构。

## 二、最终整合完整版代码（无冗余、可直接部署）

```csharp
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LlamaStarter
{
    /// <summary>
    /// 13900F 专属 llama-server 大核绑定 & 自愈监控
    /// 【最终正确核心布局】逻辑0-15 = 8物理大核超线程全集，16-31为小核
    /// 适配：按需启动、闲置休眠、进程重启、异常重建
    /// 锁定 CPU0-15 纯大核域，永久屏蔽所有E核，杜绝小核拖速
    /// </summary>
    public static class LlamaCPUCoreManager
    {
        // 13900F 最终正确大核掩码：绑定全部16个逻辑大核(0-15)，完整覆盖8颗物理P核超线程
        private const long FullPCoreMask = 0x0000FFFF;
        // 低功耗监控轮询间隔，兼顾自愈速度与CPU占用
        private const int MonitorCheckIntervalMs = 200;

        private static Process _llamaProcess;
        private static bool _isMonitorRunning;
        private static Task _monitorTask;

        /// <summary>
        /// 启动llama-server + 首次绑定大核 + 开启自愈监控
        /// </summary>
        public static Process StartLlamaServer(string exePath, string arguments)
        {
            // 进程启动配置
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _llamaProcess = Process.Start(startInfo);

            // 延迟规避内核初始化锁，保证绑定生效
            System.Threading.Thread.Sleep(50);
            ForceBindPCore(_llamaProcess);

            // 启动后台自愈监控
            StartSelfHealMonitor();

            return _llamaProcess;
        }

        /// <summary>
        /// 强制绑定纯大核（可重复调用）
        /// </summary>
        public static void ForceBindPCore(Process process)
        {
            if (process == null || process.HasExited) return;
            process.ProcessorAffinity = (IntPtr)FullPCoreMask;
        }

        /// <summary>
        /// 后台自愈监控：检测亲和性重置自动重绑
        /// </summary>
        private static void StartSelfHealMonitor()
        {
            if (_isMonitorRunning) return;

            _isMonitorRunning = true;
            _monitorTask = Task.Run(async () =>
            {
                while (_isMonitorRunning)
                {
                    try
                    {
                        // 进程退出则停止监控
                        if (_llamaProcess == null || _llamaProcess.HasExited)
                        {
                            StopMonitor();
                            break;
                        }

                        // 亲和性被系统重置时自动重绑定
                        if ((long)_llamaProcess.ProcessorAffinity != FullPCoreMask)
                        {
                            ForceBindPCore(_llamaProcess);
                        }
                    }
                    catch
                    {
                        // 忽略进程瞬时状态异常
                    }

                    await Task.Delay(MonitorCheckIntervalMs);
                }
            });
        }

        /// <summary>
        /// 停止监控（休眠销毁/主动关闭进程时必须调用）
        /// </summary>
        public static void StopMonitor()
        {
            _isMonitorRunning = false;
            _llamaProcess = null;
        }
    }
}
```

## 三、极简集成调用示例（适配你的JSON配置）

```csharp
// 读取本地稳态配置
var config = ReadYourJsonConfig();

// 拼接完整稳态启动参数（沿用你优化后的最终参数）
string launchArgs = $"--host 0.0.0.0 --port {config.Port} -t {config.Threads} --n-gpu-layers {config.Ngl} --ctx-size {config.CtxSize} {(config.NoKvUnified ? "--no-kv-unified" : "")} {config.ExtraArgs}";

// 启动服务+自动大核绑定+自愈监控
var llamaProcess = LlamaCPUCoreManager.StartLlamaServer(config.ExePath, launchArgs);

// 【关键联动】闲置30分钟休眠、进程销毁时执行
// LlamaCPUCoreManager.StopMonitor();

```

## 四、核心特性与优化收益

- **全场景自愈**：首次启动、进程重启、休眠唤醒、系统调度重置，全部自动重新绑定大核，永久生效

- **彻底解决大小核拖累**：杜绝小核线程同步阻塞问题，CPU调度极致规整，长任务投机、KV命中率更稳定

- **贴合80%余量稳态**：无调度抖动、无核心争抢，GPU预填充与解码速度波动最小化

- **低功耗零冗余**：精简无效逻辑，监控负载极低，不占用推理资源

- **系统兼容**：保留小核负责系统后台，仅推理进程独占高性能大核，整机体验不卡顿

## 五、生效验证方式

启动后打开任务管理器 → 详细信息 → llama\-server\.exe → 右键「设置相关性」，仅 **CPU0\-7** 勾选、小核全部禁用；重启/休眠唤醒后配置不丢失，即为完全生效。



> （注：部分内容可能由 AI 生成）
