# LlamaHarness UI 改版设计（对齐 Auto_Pilot 参考界面）

日期：2026-08-30
状态：已批准（方案 B + 图标资源增补）

## 1. 目标与范围

将 LlamaHarness（WinForms, .NET 8）的 UI 配色与布局对齐参考界面 `views/main_window_tk.py`（Auto_Pilot V5.1.5，深色主题 + 橙黄强调色），**功能逻辑零改动**。

已确认决策：
- **方案 B**：布局对齐但保留 Windows 系统标题栏（不做无边框窗口、不自绘窗口控制按钮）。
- **图标资源**：用户已将参考项目资源拷贝至 `LlamaHarness\bin\Debug\net8.0-windows\static\`（icon/ 32 个 PNG + doc/ 3 份 md + pic/ 截图）。实现时按参考图使用对应图标。

## 2. 全局配色（替换 MainForm.BuildUi 中的色值）

| 用途 | 现值 | 新值 |
|---|---|---|
| 页面背景 cBg | #0F1117 | #1a1a1a |
| 侧边栏/卡片 cCard | #161A23 | #2d2d2d |
| 底部/框架 cBottom | #1A1F29 | #212121 |
| 文本区/网格背景 | — | #1e1e1e（文字 #e0e0e0） |
| 按钮底 / 悬停 / 禁用 | 蓝调 | #3d3d3d / #4a4a4a / #2d2d2d，白字 |
| 主色 cPrimary | #40A9FF 蓝 | **#FFA500 橙黄** |
| 状态绿 / 红 | — | #27AE60 / #E74C3C |
| 一级标题 cTitle | #F5F7FA | #e0e0e0 |
| 辅助 cAux | #86909C | 保留 |

## 3. 左侧边栏（240px → 200px）

自上而下：
1. **应用名区**（高 ~40px）：`控制面板.png` 图标 + "Llama Harness"（medium bold 白字，居中）。替代现 44px 蓝色标题栏（BuildTitleBar 整体移除）。
2. **Control Panel** 分组（小标题 small bold）：启动/唤醒、停止、清空日志、清空缓存、开启思考模式、开启极速模式 + 状态标签 `_lblStatus`。
3. **Configuration** 分组：槽位管理、配置管理、保存配置到…、载入配置。
4. **User Manual** 分组：使用说明、常见问题、更新内容。

按钮样式：`#3d3d3d` 底 / 白字 / FlatStyle.Flat / 1px 边框 #2d2d2d / 左侧 16x16 图标；MouseEnter → #4a4a4a，MouseLeave 还原；禁用态 #2d2d2d 底 + #666666 字（现有 ApplyPhase 相位控制逻辑不变）。

### 图标映射表

| 按钮 | 图标文件 |
|---|---|
| 应用名 | 控制面板.png |
| 启动 / 唤醒 | 设备启动.png |
| 停止 | 设备停止.png |
| 清空日志 | 清除日志.png |
| 清空缓存 | 其他设置.png |
| 开启思考模式 | 附加选项.png |
| 开启极速模式 | 速度设置.png |
| 槽位管理 | 扩展设置.png |
| 配置管理 | 配置管理.png |
| 保存配置到… | 数据上传.png |
| 载入配置 | 路径设置.png |
| 使用说明 | 使用说明.png |
| 常见问题 | 常见问题.png |
| 更新内容 | 更新内容.png |

## 4. 顶部橙色大标题块（~90px，横跨右侧主区）

- 左：多行橙黄（#FFA500）大标题："Llama Harness 智能代理网关\n双槽 KV 复用 · 思考模式拦截"（bold ~16pt）。
- 右：橙黄操作提示（右对齐），如 "思考模式运行中可实时切换\n槽位亲和自动路由"。
- 替代现 BuildTitleBar 的 44px 蓝色条。

## 5. 主内容区 7:3 分栏

### 5.1 左 70%：自定义页签条 + 内容面板

- 页签条：FlowLayoutPanel 内 6 个扁平按钮（日志 / 统计 / 槽位绑定 / 槽位管理 / 系统资源 / 配置管理）。
  - 选中：底 #FFA500 + 黑字（#1e1e1e）；未选：底 #3d3d3d + 白字，悬停 #4a4a4a。
- 内容：用 Panel 容器 + 显隐切换替代原生 TabControl（与参考 custom notebook 一致）。现有 6 个页面的内容构建代码（BuildTabs 中各 tab 的控件）原样迁入对应 Panel。
- 现有 `tabControl.SelectedTab = ...` 跳转（槽位管理/配置管理按钮）改为调用页签切换方法。

### 5.2 右 30%：状态面板（现底部 SideStatsPanel 移入）

自上而下：
1. **服务阶段**：空闲 / 唤醒中 / 运行 / 休眠（PhaseChanged 驱动，颜色语义沿用 ApplyPhase）。
2. **模块状态**：网关 运行中（#27AE60 绿底白字）/ 已停止（#E74C3C 红底白字）按钮式标签，随相位切换。
3. **系统资源**：CPU / 内存 / 显存（复用现有 2s 轮询 OnMetricsTick 数据源）。
4. **运行时长**：调度器自唤醒起计时（OnMetricsTick 内更新）。
5. **Token 统计 / 槽位绑定 / 思考模式**：现有三卡片纵向堆叠（原底部横向 3 列改为纵向 3 行）。

布局结构变化：现 `rightSplit`（水平 SplitContainer，上 tabHost / 下 sidePanel）改为：右侧主区 = 顶部标题块（Dock Top）+ 下方垂直 SplitContainer（左 70% 页签区 | 右 30% 状态面板）。

## 6. 日志区与网格

- 日志 RichTextBox：底 #1e1e1e、字 #e0e0e0；保留现有逐行着色（正常/警告/错误）与防抖机制。
- DataGridView（槽位绑定/槽位管理/统计）：底 #1e1e1e、网格线 #2d2d2d、表头 #2d2d2d 白字。

## 7. 帮助文档接线（新增，随 UI 一并落地）

现 btnHelp/btnFaq/btnChangelog 无事件处理。接线为：点击 → 弹出只读窗体显示 `static/doc/readme.md` / `FAQs.md` / `update.md` 内容（简单 Form + 只读 TextBox，深色配色）。

## 8. 资源持久化（csproj 改动）

`static/` 当前位于输出目录 `bin\Debug\net8.0-windows\static\`，clean/rebuild 会丢失。实现时：
1. 将 `static/` 整体移至源码树 `LlamaHarness/static/`。
2. csproj 增加 `<Content Include="static/**" CopyToOutputDirectory="PreserveNewest" />`。
3. 运行时按 `AppContext.BaseDirectory + "static/icon/xxx.png"` 加载；图标缺失时降级为纯文本按钮（不崩溃）。

## 9. 不改动清单

- SmartScheduler / KvCacheManager / SlotAffinity / TokenGuard 等全部功能逻辑。
- 事件接线、ApplyPhase 相位控制、思考模式状态机、配置读写（config.json 结构不变）。
- 日志防抖、统计解析器、崩溃熔断告警逻辑。

## 10. 风险与约束

- WinForms TabControl 无法直接换肤 → 用 Panel 显隐方案替代（§5.1），需保证 6 页内容迁移无遗漏。
- 图标为中文文件名，注意编码与路径处理；缺失降级（§8.3）。
- 侧边栏 240→200px 后按钮文字宽度收窄，长文本按钮（"保存配置到…"）需验证不截断。
