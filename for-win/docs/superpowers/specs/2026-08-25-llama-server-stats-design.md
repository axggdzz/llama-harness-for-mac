# llama-server 实时统计功能设计规格

日期：2026-08-25
状态：已评审通过（含 f_sim_best 扩展）

## 1. 背景与目标

从 llama-server 进程日志中**实时解析** `print_timing` 块，按每个 API 请求（task ID）输出一行统计：输入 token 速度、输出 token 速度、投机解码命中率、f_sim_best、总耗时。展示为独立统计表 + 累计汇总行。

**成功标准：**
- 一次 API 请求完成后，表格自动追加/补全一行，无需人工干预
- 指标与 llama-server 日志数值一致
- 不改动现有代理/唤醒/休眠链路

## 2. 范围（YAGNI）

**范围内：**
- 纯解析器组件 `LlamaStatsParser`（正则识别 print_timing 块）
- MainForm 底部新增统计表面板（DataGridView + 汇总标签 + 清空按钮）
- 会话边界处理（唤醒 = 新会话，清空统计）

**范围外（不做）：**
- CSV / JSON 持久化（用户选择内存 + 汇总行）
- 按会话聚合视图（每请求一行已确认）
- 轮询 `/metrics` API（用户明确要日志实时统计）

## 3. 数据源格式

llama-server 每个请求完成后输出（实测样例）：

```text
slot print_timing: id 0 task 0 prompt eval time =   55893.54 ms /  4751 tokens (    1.18 ms per ...)
slot print_timing: id 0 task 0 eval time =     2313.07 ms /    92 tokens (   25.42 ms per ...)
slot print_timing: id 0 task 0 total time =     58182.36 ms / 47409 tokens
slot print_timing: id 0 task 0 draft acceptance =    0.65000   52 accepted /   80 generated, m...
```

f_sim_best（定制构建字段，假设与上述指标同属 print_timing 块）：

```text
slot print_timing: id 0 task 0 f_sim_best = <数值>
```

## 4. 解析规则（LlamaStatsParser，纯逻辑无 UI 依赖）

- **快速路径**：行内不含 `print_timing` 直接跳过（绝大多数行）
- **归组键**：`task ID`（正则 `print_timing:\s+id\s+\d+\s+task\s+(\d+)`），同一 task 的多行合并为一轮记录
- **增量更新**：看到某 task 第一行即建行并触发事件，后续行原地补全（draft acceptance / f_sim_best 晚于 total time 到达也正确合并）
- **指标提取**：

| 列 | 来源 | 计算 |
|---|---|---|
| 时间 | 建行时刻 | — |
| 输入 tokens | `prompt eval time = X ms / N tokens` | N |
| 输入速度 (t/s) | 同上 | N ÷ (X/1000) |
| 输出 tokens | `eval time = X ms / N tokens`（负向后顾排除 "prompt eval"） | N |
| 输出速度 (t/s) | 同上 | N ÷ (X/1000) |
| 命中率 | `draft acceptance = ... A accepted / G generated` | A/G，显示百分比 |
| f_sim_best | `f_sim_best` 后第一个数值（位置不敏感） | 原值 |
| 总耗时 (s) | `total time = X ms` | X/1000 |

- **会话边界**：llama-server 每次唤醒 task ID 从 0 重新计数 → MainForm 在调度器进入 Waking 阶段时调用 `parser.Reset()`，清空表格与记录，防止跨会话 ID 冲突
- **线程安全**：Feed（进程输出线程）与 Reset（UI 线程）经内部锁串行化；事件在锁外触发

## 5. UI 设计（MainForm）

- 日志区与统计表用 **SplitContainer** 上下分栏（可拖拽），替代原单一日志区
- 统计表面板：
  - 顶部：累计汇总标签 + "清空统计"按钮
  - 主体：DataGridView，列 = 时间 | 输入tokens | 输入速度(t/s) | 输出tokens | 输出速度(t/s) | 命中率(accepted/generated) | f_sim_best | 总耗时(s)
  - 只读、不可增行、列自动填充宽度
- **汇总行内容**：请求数、总输入 tokens @ 平均速度、总输出 tokens @ 平均速度、加权命中率（总 accepted / 总 generated）
- 事件链路（不改 SmartScheduler）：
  `_scheduler.Log` → `parser.Feed(line)` → `RoundUpdated` / `SessionReset` → MainForm `BeginInvoke` 更新表格

## 6. 边界情况

| 场景 | 行为 |
|---|---|
| 日志无 print_timing（版本差异） | 表格保持空，不报错 |
| 无投机解码（无 draft acceptance 行） | 命中率列显示 "—" |
| 无 f_sim_best 行 | 该列显示 "—" |
| 进程被 Kill 时半截块 | 已建行的部分数据保留，缺失列留默认值，不影响后续请求 |
| 唤醒新会话 | Reset 清空表格（task ID 重用防护） |

## 7. 测试策略

GUI + 外部进程，手动验证为主：

1. 发一次 API 请求 → 表格出现一行，数值与日志 print_timing 一致
2. 连续多次请求 → 每请求一行，命中率/速度各自独立
3. 停止并再次唤醒 → 表格清空（新会话）
4. 关闭投机解码参数后请求 → 命中率列 "—"
5. "清空统计"按钮 → 表格立即清空
