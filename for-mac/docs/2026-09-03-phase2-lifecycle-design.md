# 第二阶段：调度生命周期设计

第二阶段在第一阶段网关之上加入基础资源调度，不依赖真实模型。后端 readiness 成功后先进入 `Warming`，预热期间的推理请求继续等待同一个启动任务；预热完成才进入 `Running`。

网关维护推理请求的在途计数和最后活动时间。后台 idle monitor 在 `Running` 且无在途请求、超过 `idle_timeout_ms` 时进入 `Sleeping`，等待可配置的观察期（生产默认 10 秒）。观察期内新推理请求会取消休眠、恢复 `Running` 并复用已启动后端；观察期结束仍空闲才停止后端进程组并回到 `Standby`。

为避免竞态，开始请求和休眠最终提交共享 `request_gate`。请求在途计数在代理管道入口增加、出口减少；状态/健康探针不刷新闲置计时。监听器关闭时先停止 idle monitor，再执行后端 stop/join，保证不会在网关退出后留下后台进程。

测试通过动态回环端口和 mock llama-server 覆盖：Warming 状态可见、空闲自动休眠、睡眠观察期取消，以及后端 PID 退出。`warming_delay_ms`、`idle_timeout_ms` 和 `sleep_observe_ms` 仅用于调度策略与测试加速，不改变第一阶段固定前端端口语义。
