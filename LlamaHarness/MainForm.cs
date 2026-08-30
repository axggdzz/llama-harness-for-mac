namespace LlamaHarness;

/// <summary>
/// 主窗口：参数区（黄金底参默认值）+ 操作区 + 日志区（自动滚动）。
/// 进程管控与智能调度全部委托给 SmartScheduler；本类只负责 UI 渲染、控件启停状态。
/// UI 状态机防重复启动：唤醒/运行/休眠期间禁用启动按钮与全部参数控件。
/// </summary>
public class MainForm : Form
{
    // ════════════ 全局配色（对齐 Auto_Pilot 参考界面：深色底 + 橙黄强调）════════════
    private static readonly Color C_Bg = Color.FromArgb(0x1A, 0x1A, 0x1A);        // #1a1a1a 页面背景
    private static readonly Color C_Card = Color.FromArgb(0x2D, 0x2D, 0x2D);      // #2d2d2d 侧边栏/卡片/按钮底
    private static readonly Color C_Frame = Color.FromArgb(0x21, 0x21, 0x21);     // #212121 框架/状态面板底
    private static readonly Color C_TextBg = Color.FromArgb(0x1E, 0x1E, 0x1E);    // #1e1e1e 文本区/网格底
    private static readonly Color C_TextFg = Color.FromArgb(0xE0, 0xE0, 0xE0);    // #e0e0e0 正文文字
    private static readonly Color C_Btn = Color.FromArgb(0x3D, 0x3D, 0x3D);       // #3d3d3d 按钮底
    private static readonly Color C_BtnHover = Color.FromArgb(0x4A, 0x4A, 0x4A);  // #4a4a4a 按钮悬停
    private static readonly Color C_Primary = Color.FromArgb(0xFF, 0xA5, 0x00);   // #FFA500 橙黄强调（大标题/选中页签）
    private static readonly Color C_Title = Color.FromArgb(0xE0, 0xE0, 0xE0);     // #e0e0e0 一级标题
    private static readonly Color C_Aux = Color.FromArgb(0x86, 0x90, 0x9C);       // #86909C 辅助说明
    private static readonly Color C_Green = Color.FromArgb(0x27, 0xAE, 0x60);     // #27AE60 运行中
    private static readonly Color C_Red = Color.FromArgb(0xE7, 0x4C, 0x3C);       // #E74C3C 已停止/异常
    private static readonly Color C_Warn = Color.FromArgb(0xFF, 0x98, 0x00);      // #FF9800 过渡态（唤醒/休眠）

    private readonly AppConfig _config;
    private readonly SmartScheduler _scheduler;

    // —— 参数控件（Configuration 面板内）——
    private readonly TextBox _txtExe = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly Button _btnBrowseExe = new() { Text = "…", Size = new Size(32, 28), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White };
    private readonly TextBox _txtModel = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly Button _btnBrowseModel = new() { Text = "…", Size = new Size(32, 28), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White };
    private readonly NumericUpDown _numPort = new() { Minimum = 1, Maximum = 65534, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numCtx = new() { Minimum = 256, Maximum = 1_048_576, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numNgl = new() { Minimum = 0, Maximum = 999, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numParallel = new() { Minimum = 1, Maximum = 128, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly CheckBox _chkNoKv = new() { Text = "--no-kv-unified", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly NumericUpDown _numThreads = new() { Minimum = 1, Maximum = 512, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly TextBox _txtExtra = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly CheckBox _chkAuto = new() { Text = "智能按需模式（推荐）", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) }; // AutoSize：紧跟"模式:"标签同行
    private readonly NumericUpDown _numIdleMin = new() { Minimum = 1, Maximum = 120, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly TextBox _txtPcoreMask = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly CheckBox _chkForceStream = new() { Text = "强制流式", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly TextBox _txtKvCachePath = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly CheckBox _chkTokenGuard = new() { Text = "Token Guard（防上下文超长）", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly NumericUpDown _numReservedTokens = new() { Minimum = 512, Maximum = 131_072, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly CheckBox _chkContinuation = new() { Text = "输出续接（截断自动续写）", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly NumericUpDown _numMaxContinuations = new() { Minimum = 1, Maximum = 50, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly NumericUpDown _numContTimeout = new() { Minimum = 30, Maximum = 3600, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    private readonly CheckBox _chkCrashRecover = new() { Text = "bad_alloc 自动恢复（快照接续/重放）", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly NumericUpDown _numMaxRestarts = new() { Minimum = 0, Maximum = 10, Dock = DockStyle.Fill, BackColor = Color.FromArgb(45, 45, 45), ForeColor = Color.White };
    // §4.2 自动强占（冻结防驱逐）：按应用类型前缀，勾选 → 该类型绑定强制 Preemptive=true
    private readonly CheckBox _chkAutoPreDshRule = new() { Text = "DSH规则", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkAutoPreWebui = new() { Text = "WebUI", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkAutoPreTrae = new() { Text = "Trae", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly CheckBox _chkAutoPreDshAgent = new() { Text = "DSH Agent", AutoSize = true, ForeColor = Color.FromArgb(200, 200, 200) };
    private readonly ToolTip _tooltip = new();

    // —— 操作按钮（Control Panel 区）——
    private Button _btnStart;
    private Button _btnStop;
    private Button _btnClearLog;
    private Button _btnClearCache;
    private Button _btnThinkOn;   // 开启思考模式 → XHigh（深度推理）
    private Button _btnTurbo;     // 开启极速模式 → Off（不注入思考参数）
    private Button _btnExportCfg;
    private Button _btnImportCfg;

    // —— 思考模式状态标签（侧边统计面板）——
    private readonly Label _lblThinking = new()
    {
        Text = "思考: 极速",
        ForeColor = Color.Silver,
    };

    // —— 系统资源统计（2 秒轮询）——
    private readonly SystemMetrics _metrics = new();
    private Label _lblResDetail;
    private readonly System.Windows.Forms.Timer _metricsTimer = new() { Interval = 2000 };
    private int _metricsBusy;
    private bool _crashAlertShown; // 崩溃熔断红色告警状态（防重复告警；窗口滑出后自动恢复）

    // —— 日志区（RichTextBox：按行独立着色 + 防抖）——
    private readonly RichTextBox _txtLog = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        WordWrap = false,
        BorderStyle = BorderStyle.None, // 无边框，消除白边
        BackColor = C_TextBg,
        ForeColor = C_TextFg,
        Font = new Font("Consolas", 9F),
    };
    private readonly Queue<(string line, string entry)> _logQueue = new();
    private readonly System.Windows.Forms.Timer _logFlushTimer = new() { Interval = 150 };

    // —— 统计区（实时解析 print_timing）——
    private readonly LlamaStatsParser _statsParser = new();
    private readonly Label _lblSummary = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = C_Primary,
        Font = new Font("Consolas", 9F),
        Margin = new Padding(4, 4, 4, 4),
    };
    private readonly Button _btnClearStats = new()
    {
        Text = "清空统计",
        Size = new Size(80, 26),
        FlatStyle = FlatStyle.Flat,
        BackColor = C_Btn,
        ForeColor = Color.White,
    };
    private readonly DataGridView _gridStats = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToResizeRows = false,
        BorderStyle = BorderStyle.None, // 无边框，消除白边
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = C_TextBg,
        ForeColor = C_TextFg,
        GridColor = C_Card,
        RowHeadersVisible = false,
        RowTemplate = new DataGridViewRow { Height = 22 },
    };

    // —— 槽位绑定表格（页签3）——
    private DataGridView _gridSlots;
    // —— 槽位管理页/表格（页签4，强占/KV缓存可编辑）——
    private Panel _tabSlotMgmt;
    private DataGridView _gridSlotMgmt;
    // —— 配置管理页（页签6）——
    private Panel _tabConfig;

    // —— 自定义页签区（替代原生 TabControl：扁平按钮页签条 + Panel 显隐切换，对齐参考界面）——
    private SplitContainer _contentSplit;   // 左 70% 页签区 | 右 30% 状态面板
    private Button[] _tabButtons = null!;
    private Panel[] _tabPages = null!;
    private int _currentTab = 0;

    // —— 右侧状态面板（原底部 SideStatsPanel 移入）——
    private Label _lblStatus;       // 服务阶段卡片：调度器状态文本（运行中 · N个在途任务…），替代原侧边栏实例
    private Label _lblModuleState;  // 模块状态（网关 运行中绿 / 已停止红）
    private Label _lblResSummary;   // 系统资源单行摘要（CPU/内存/显存）
    private Label _lblRunTime;      // 运行时长（自本次唤醒起）
    private DateTime? _wakeTime;    // 本次唤醒时刻（非 Running 为 null）

    // —— 图标缓存（static/icon/*.png，缺失时降级纯文本按钮）——
    private static readonly Dictionary<string, Image> IconCache = new(StringComparer.OrdinalIgnoreCase);
    // —— 参数控件清单（BuildUi 一次构建；ApplyPhase 按相位批量启停，审计：原实现每次调用重建数组）——
    private Control[] _paramControls;
    // —— 槽位管理表 key→行索引（审计：原实现每轮刷新线性扫 Tag，O(n²)）——
    private readonly Dictionary<string, int> _slotMgmtRowIdx = new(StringComparer.Ordinal);
    // —— 槽位日志（槽位绑定页下方，独立持久化 slot.log）——
    private RichTextBox _txtSlotLog;

    // —— 侧边统计标签 ——
    private Label _lblTokenSummary;
    private Label _lblSlotSummary;

    public MainForm()
    {
        _config = AppConfig.Load(out string? loadError);
        _scheduler = new SmartScheduler(_config)
        {
            AutoMode = _config.AutoMode,
            IdleMinutes = Math.Clamp(_config.IdleMinutes, 1, 120),
        };

        BuildUi();
        LoadConfigToUi();
        UpdatePortControlState(); // 智能模式下监听器占用端口，禁止编辑
        WireEvents();

        // 调度器事件 → UI（内部统一 BeginInvoke）
        _scheduler.Log += AppendLog;
        _scheduler.StatusChanged += OnSchedulerStatus;
        _scheduler.PhaseChanged += OnPhaseChanged;
        // C-007：统计重置由调度器状态机驱动（Waking 时自动触发），不再依赖 UI 监听 PhaseChanged
        _scheduler.StatsReset += () => _statsParser.Reset();
        // 思考模式状态变更 → UI 标签
        _scheduler.ThinkingModeChanged += OnThinkingModeChanged;
        // 槽位绑定变更 → 刷新槽位表格
        _scheduler.SlotBindingChanged += RefreshSlotBindings;
        // 槽位日志（绑定/驱逐/KV Cache）→ 槽位页 RichTextBox + slot.log 持久化
        _scheduler.SlotLog += OnSlotLog;

        // 启动时按当前附加参数显示初始思考模式（唤醒时会按实际启动参数权威重置）
        RefreshThinkingLabel();
        AppendLog($"思考模式初始状态：「{SmartScheduler.LabelOf(SmartScheduler.DetermineInitialThinkingMode(_config.ExtraArgs))}」");

        // 统计：日志行喂给解析器；解析结果/会话重置回 UI
        _scheduler.Log += line => _statsParser.Feed(line);
        _statsParser.RoundUpdated += OnRoundUpdated;
        _statsParser.RoundRemoved += OnRoundRemoved;
        _statsParser.SessionReset += OnSessionReset;

        if (loadError != null)
            AppendLog(loadError);
        AutoFindExe();

        // 首帧渲染后再启动监听/布局，避免构造期间 BeginInvoke
        Shown += OnShown;
    }

    private void OnShown(object? sender, EventArgs e)
    {
        _scheduler.Initialize();

        // 资源轮询：CPU 需两次采样取差值，首次 tick 建立基准
        _metricsTimer.Tick += OnMetricsTick;
        _metricsTimer.Start();

        // 日志防抖定时器：批量消费队列，减少 RichTextBox 重绘闪烁。
        // 常驻运行（不 Stop/Start）：跨线程操作 WinForms Timer 会导致 SetTimer 绑定错误消息循环而永久停摆
        _logFlushTimer.Tick += OnLogFlush;
        _logFlushTimer.Start();
    }

    /// <summary>每 2 秒刷新系统资源页签 + 轮询崩溃熔断状态（红色告警）。</summary>
    private void OnMetricsTick(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _metricsBusy, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                double cpu = _metrics.GetCpuPercent();
                var (used, total) = _metrics.GetMemory();
                string? vram = await _metrics.GetVramTextAsync();
                bool tripped = CrashRecovery.IsTripped;
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    _lblResDetail.Text =
                        $"CPU:      {cpu:F0}%\n" +
                        $"内存:     {used:F1} / {total:F1} GB\n" +
                        $"显存:     {(vram ?? "—（未检测到 nvidia-smi）")}";
                    // 右侧状态面板：资源单行摘要 + 运行时长（自本次唤醒起）
                    _lblResSummary.Text = $"CPU {cpu:F0}% | 内存 {used:F1}/{total:F1}GB";
                    _lblRunTime.Text = _wakeTime is DateTime wt ? (DateTime.Now - wt).ToString(@"hh\:mm\:ss") : "—";

                    // 崩溃熔断红色告警：状态切换时醒目日志 + 状态栏变红；窗口滑出后恢复
                    if (tripped && !_crashAlertShown)
                    {
                        _crashAlertShown = true;
                        AppendLog("⚠⚠ 崩溃熔断器已跳闸：10 分钟内 ≥3 次 bad_alloc，自动恢复已停止。请加内存 / 降上下文后手动重试！");
                        _lblStatus.ForeColor = Color.FromArgb(0xF5, 0x3F, 0x3F);
                        _lblStatus.Text = "⚠ 崩溃熔断：自动恢复已停止，需人工介入";
                    }
                    else if (!tripped && _crashAlertShown)
                    {
                        _crashAlertShown = false;
                        ApplyPhase(_scheduler.CurrentPhase); // 按当前阶段重渲染状态栏颜色
                    }
                });
            }
            finally
            {
                Interlocked.Exchange(ref _metricsBusy, 0);
            }
        });
    }

    // ==================== UI 构建 ====================

    private void BuildUi()
    {
        Text = "Llama Harness";
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(1000, 600);
        StartPosition = FormStartPosition.CenterScreen;

        BackColor = C_Bg;
        ForeColor = C_TextFg;

        var tabArea = BuildTabArea();
        var leftPanel = BuildLeftPanel();
        var titleBlock = BuildTitleBlock();
        var statusPanel = BuildStatusPanel();

        // 参数控件清单一次构建（审计：原实现每次 ApplyPhase 调用都重建数组）
        _paramControls = new Control[]
        {
            _txtExe, _btnBrowseExe, _txtModel, _btnBrowseModel,
            _numPort, _numCtx, _numNgl, _numParallel, _chkNoKv, _numThreads, _txtExtra,
            _chkAuto, _numIdleMin, _txtPcoreMask, _chkForceStream, _txtKvCachePath,
            _chkTokenGuard, _numReservedTokens,
            _chkContinuation, _numMaxContinuations, _numContTimeout,
            _chkCrashRecover, _numMaxRestarts,
            _chkAutoPreDshRule, _chkAutoPreWebui, _chkAutoPreTrae, _chkAutoPreDshAgent,
            _btnExportCfg, _btnImportCfg, // 运行中禁止导入/导出，避免改参冲突
        };

        // ════════════ 右侧主区：顶部橙色标题块 + 下方 7:3 分栏（左页签 | 右状态面板）════════════
        var rightContent = new Panel { Dock = DockStyle.Fill, BackColor = C_Bg };
        _contentSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4,
            BackColor = C_Bg,
        };
        _contentSplit.Panel1.Controls.Add(tabArea);
        _contentSplit.Panel2.Controls.Add(statusPanel);
        rightContent.Controls.Add(_contentSplit);
        rightContent.Controls.Add(titleBlock); // Dock Top，后添加 → 位于最上

        // ════════════ 主布局：左侧边栏(200px) | 右侧主区 ════════════
        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4,
            BackColor = C_Bg,
        };
        mainSplit.Panel1.Controls.Add(leftPanel);
        mainSplit.Panel2.Controls.Add(rightContent);
        Controls.Add(mainSplit);

        Shown += (_, _) =>
        {
            mainSplit.SplitterDistance = 240; // 侧边栏 240px（容纳按钮文字，避免滚动条）
            _contentSplit.SplitterDistance = Math.Max(500, (int)(_contentSplit.Width * 0.7)); // 7:3 分栏
        };
    }

    /// <summary>左侧边栏 (200px)：应用名 + Control Panel + Configuration + User Manual。
    /// 按钮带参考界面 PNG 图标（static/icon），悬停变亮，对齐 Auto_Pilot 侧边栏样式。</summary>
    private Panel BuildLeftPanel()
    {
        // 侧边栏：#2d2d2d 深灰底（对齐参考 sidebar_bg），宽度 240（容纳按钮文字，避免滚动条）。
        // 层次感靠灰度差异：侧栏 #2d2d2d < 按钮 #3d3d3d < 悬停 #4a4a4a，非纯黑。
        var leftPanel = new Panel
        {
            Dock = DockStyle.Left,
            Width = 240,
            BackColor = C_Card, // #2d2d2d
            Padding = new Padding(0), // 边距由下方 Percent 列控制（等比缩放）
            AutoScroll = false, // 禁用滚动条（内容高度足够容纳全部按钮）
        };

        // ── 应用名区（替代原顶部标题栏；扁平 Button 样式化，支持图标+文本并排）──
        var lblAppName = new Button
        {
            Text = "Llama Harness",
            Image = LoadIcon("控制面板.png"),
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = 44,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
            BackColor = C_BtnHover, // #4a4a4a（默认色，原悬停色）
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
            Cursor = Cursors.Default,
        };
        lblAppName.FlatAppearance.BorderSize = 0; // 无边框，消除白边
        lblAppName.MouseEnter += (_, _) => lblAppName.BackColor = C_Card; // 悬停 → #2d2d2d（原默认色）
        lblAppName.MouseLeave += (_, _) => lblAppName.BackColor = C_BtnHover; // 还原 → #4a4a4a

        // ── Control Panel ──
        var lblCtrlTitle = MakeSectionTitle("Control Panel");
        _btnStart = MakeBtn("启动 / 唤醒", "设备启动.png");
        _btnStop = MakeBtn("停止", "设备停止.png", enabled: false);
        _btnClearLog = MakeBtn("清空日志", "清除日志.png", h: 30);
        _btnClearCache = MakeBtn("清空缓存", "其他设置.png", h: 30);
        _btnThinkOn = MakeBtn("开启思考模式", "附加选项.png", h: 30);
        _btnTurbo = MakeBtn("开启极速模式", "速度设置.png", h: 30);
        // _lblStatus 已移至右侧"服务阶段"卡片（替换原 _lblPhase），此处不再创建侧边栏实例

        // ── Configuration ──
        var lblCfgTitle = MakeSectionTitle("Configuration");
        var btnSlotMgmt = MakeBtn("槽位管理", "扩展设置.png", h: 30);
        var btnOpenConfig = MakeBtn("配置管理", "配置管理.png");
        _btnExportCfg = MakeBtn("保存配置到…", "数据上传.png", h: 30);
        _btnImportCfg = MakeBtn("载入配置", "路径设置.png", h: 30);
        btnSlotMgmt.Click += (_, _) => SelectTab(3); // 槽位管理页
        btnOpenConfig.Click += (_, _) => SelectTab(5); // 配置管理页

        // ── User Manual（接线：显示 static/doc 对应文档）──
        var lblManualTitle = MakeSectionTitle("User Manual");
        var btnHelp = MakeBtn("使用说明", "使用说明.png", h: 30);
        var btnFaq = MakeBtn("常见问题", "常见问题.png", h: 30);
        var btnChangelog = MakeBtn("更新内容", "更新内容.png", h: 30);
        btnHelp.Click += (_, _) => ShowDocForm("使用说明", "static/doc/readme.md");
        btnFaq.Click += (_, _) => ShowDocForm("常见问题", "static/doc/FAQs.md");
        btnChangelog.Click += (_, _) => ShowDocForm("更新内容", "static/doc/update.md");

        // ── 布局网格：[12.5% | 75% | 12.5%] 三列——所有按键/容器放中列，
        // 等宽（含应用名）+ 居中 + 左右间隔等宽；Percent 列保证侧边栏放大时边距等比缩放 ──
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f)); // 左边距（等比）
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75f));   // 内容列（所有按键等宽）
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f)); // 右边距（等比）

        void AddRow(Control c, int h)
        {
            int row = grid.RowStyles.Count;
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, h)); // 固定行高（px），不随内容拉伸
            c.Dock = DockStyle.Fill; // 撑满中列 → 宽度统一
            grid.Controls.Add(c, 1, row);
        }

        AddRow(lblAppName, 44); // 应用名与下方按键等宽
        AddRow(lblCtrlTitle, 30);
        AddRow(_btnStart, 34);
        AddRow(_btnStop, 34);
        AddRow(_btnClearLog, 30);
        AddRow(_btnClearCache, 30);
        AddRow(_btnThinkOn, 30);
        AddRow(_btnTurbo, 30);
        AddRow(lblCfgTitle, 30);
        AddRow(btnSlotMgmt, 30);
        AddRow(btnOpenConfig, 34);
        AddRow(_btnExportCfg, 30);
        AddRow(_btnImportCfg, 30);
        AddRow(lblManualTitle, 30);
        AddRow(btnHelp, 30);
        AddRow(btnFaq, 30);
        AddRow(btnChangelog, 30);

        leftPanel.Controls.Add(grid);
        return leftPanel;
    }

    /// <summary>从 static/icon 加载图标并缩放到 16x16（缓存；文件缺失返回 null → 按钮降级纯文本）。
    /// 参考图标原始尺寸 300px+，必须缩放，否则挤占按钮文字区域。
    /// 黑色图标自动反转为白色（统一深色主题下的图标颜色）。</summary>
    private static Image? LoadIcon(string fileName)
    {
        if (IconCache.TryGetValue(fileName, out var cached)) return cached;
        var path = Path.Combine(AppContext.BaseDirectory, "static", "icon", fileName);
        try
        {
            if (File.Exists(path))
            {
                using var src = new Bitmap(path); // 构造时同步读入内存，不持有文件句柄
                var img = new Bitmap(16, 16);     // 缩放到 16x16（侧边栏按钮图标标准尺寸）
                using (var g = Graphics.FromImage(img))
                    g.DrawImage(src, 0, 0, 16, 16);

                // 黑色图标 → 白色：检测非透明像素平均亮度，偏黑（<128）才反转，白底黑图保持原样
                int totalA = 0, totalLum = 0;
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        var p = img.GetPixel(x, y);
                        if (p.A > 0)
                        {
                            totalA++;
                            totalLum += (p.R + p.G + p.B) / 3; // 简单亮度估算
                        }
                    }
                if (totalA > 0 && totalLum / totalA < 128) // 平均亮度偏黑 → 反转
                {
                    for (int y = 0; y < 16; y++)
                        for (int x = 0; x < 16; x++)
                        {
                            var p = img.GetPixel(x, y);
                            if (p.A > 0)
                                img.SetPixel(x, y, Color.FromArgb(p.A, 255 - p.R, 255 - p.G, 255 - p.B));
                        }
                }

                IconCache[fileName] = img;
                return img;
            }
        }
        catch
        {
            // 图标损坏/不可读：降级纯文本按钮
        }
        return null;
    }

    /// <summary>帮助文档窗体（只读深色 TextBox 显示 static/doc 下对应 md；文件缺失时提示）。</summary>
    private void ShowDocForm(string title, string relPath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relPath.Replace('/', Path.DirectorySeparatorChar)));
        string text;
        try
        {
            text = File.Exists(path) ? File.ReadAllText(path) : "（文档文件缺失：" + relPath + "）";
        }
        catch
        {
            text = "（文档加载失败）";
        }
        var f = new Form
        {
            Text = $"Llama Harness - {title}",
            Size = new Size(780, 580),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = C_TextBg,
            ForeColor = C_TextFg,
        };
        var tb = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = C_TextBg,
            ForeColor = C_TextFg,
            Font = new Font("Microsoft YaHei UI", 10F),
            Text = text,
        };
        f.Controls.Add(tb);
        f.Show(this);
    }

    /// <summary>顶部橙色大标题块 (~90px，对齐参考界面)：左多行橙黄主标题 + 右操作提示。</summary>
    private static Panel BuildTitleBlock()
    {
        var titleBlock = new Panel
        {
            Dock = DockStyle.Top,
            Height = 90,
            BackColor = C_Bg,
            Padding = new Padding(16, 10, 16, 6),
        };
        var lblTitle = new Label
        {
            Text = "Llama Harness 智能代理网关\n双槽 KV 复用 · 思考模式拦截",
            Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
            ForeColor = C_Primary,
            AutoSize = true,
            Dock = DockStyle.Left,
            Margin = new Padding(0, 4, 16, 0),
        };
        var lblHint = new Label
        {
            Text = "思考模式运行中可实时切换\n槽位亲和自动路由 · KV 快照自愈",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = C_Primary,
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        titleBlock.Controls.Add(lblHint); // 先添加（Fill 后布局，占 Left 之外的剩余空间）
        titleBlock.Controls.Add(lblTitle);
        return titleBlock;
    }

    /// <summary>自定义页签区（左 70%）：扁平按钮页签条 + 6 内容页 Panel 显隐切换（替代原生 TabControl，对齐参考界面）。
    /// 选中 = #FFA500 底黑字；未选 = #3d3d3d 底白字。</summary>
    private Panel BuildTabArea()
    {
        var container = new Panel { Dock = DockStyle.Fill, BackColor = C_Bg };

        var tabLog = new Panel { Dock = DockStyle.Fill, BackColor = C_TextBg, Padding = new Padding(4) };
        tabLog.Controls.Add(_txtLog);

        var tabStats = new Panel { Dock = DockStyle.Fill, BackColor = C_Bg, Padding = new Padding(10) };
        tabStats.Controls.Add(BuildStatsPanel());

        // 槽位绑定页：上方绑定表格 + 下方槽位日志（独立持久化 slot.log）
        var tabSlots = new Panel { Dock = DockStyle.Fill, BackColor = C_Bg, Padding = new Padding(10) };
        _txtSlotLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None, // 无边框，消除白边
            BackColor = C_TextBg,
            ForeColor = C_TextFg,
            Font = new Font("Consolas", 9F),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = false,
        };
        _gridSlots = MakeGrid();
        _gridSlots.Dock = DockStyle.Top;
        _gridSlots.Height = 260;
        _gridSlots.Columns.AddRange(MakeGridCol("亲和 Key"), MakeGridCol("应用"), MakeGridCol("槽位"), MakeGridCol("最后活跃"));
        tabSlots.Controls.Add(_txtSlotLog);
        tabSlots.Controls.Add(_gridSlots);

        // 槽位管理页：DataGridView（强占/KV缓存 CheckBox 可编辑）
        _tabSlotMgmt = new Panel { Dock = DockStyle.Fill, BackColor = C_Bg, Padding = new Padding(10) };
        _gridSlotMgmt = MakeGrid();
        _gridSlotMgmt.ReadOnly = false;
        _gridSlotMgmt.Columns.AddRange(
            MakeGridCol("亲和 Key"), MakeGridCol("应用"), MakeGridCol("槽位"),
            MakeCheckCol("强占"), MakeCheckCol("KV缓存"), MakeGridCol("最后活跃"));
        _gridSlotMgmt.CellValueChanged += OnSlotMgmtCellChanged;
        _tabSlotMgmt.Controls.Add(_gridSlotMgmt);

        var tabRes = new Panel { Dock = DockStyle.Fill, BackColor = C_Bg, Padding = new Padding(10) };
        _lblResDetail = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Consolas", 12F),
            ForeColor = C_TextFg,
        };
        tabRes.Controls.Add(_lblResDetail);

        _tabConfig = new Panel { Dock = DockStyle.Fill, BackColor = C_Bg, Padding = new Padding(10), AutoScroll = true };
        _tabConfig.Controls.Add(BuildConfigPanel());

        // 页签条：6 个扁平按钮（选中橙底黑字 / 未选 #3d3d3d 白字，悬停变亮）
        string[] names = { "日志", "统计", "槽位绑定", "槽位管理", "系统资源", "配置管理" };
        _tabButtons = new Button[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int idx = i;
            var b = MakeTabBtn(names[i]);
            b.Click += (_, _) => SelectTab(idx);
            b.MouseEnter += (_, _) => { if (_currentTab != idx) b.BackColor = C_BtnHover; };
            b.MouseLeave += (_, _) => { if (_currentTab != idx) b.BackColor = C_Btn; };
            _tabButtons[i] = b;
        }
        var tabStrip = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true, // 窗口过窄时页签换行，高度自适应
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = C_Bg,
            Margin = new Padding(0),
            Padding = new Padding(4, 2, 4, 2),
        };
        foreach (var b in _tabButtons) tabStrip.Controls.Add(b);

        // 内容宿主：6 页叠放 + Visible 切换
        var host = new Panel { Dock = DockStyle.Fill, BackColor = C_Bg };
        _tabPages = new[] { tabLog, tabStats, tabSlots, _tabSlotMgmt, tabRes, _tabConfig };
        foreach (var p in _tabPages) host.Controls.Add(p);

        container.Controls.Add(host);
        container.Controls.Add(tabStrip); // Dock Top，后添加 → 位于最上
        SelectTab(0);

        // 四周等宽边距（固定 px：左右/上下各 16px，绝对值不随窗口缩放变化，始终等宽）
        var pad = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            BackColor = C_Bg,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        pad.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));  // 左边距（固定 16px）
        pad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // 内容（剩余全部）
        pad.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));  // 右边距（固定 16px）
        pad.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));    // 上边距（固定 16px）
        pad.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // 内容（剩余全部）
        pad.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));    // 下边距（固定 16px）
        pad.Controls.Add(container, 1, 1);
        return pad;
    }

    /// <summary>扁平页签按钮（#3d3d3d 底白字，尺寸自适应文字，无边框）。</summary>
    private static Button MakeTabBtn(string text)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = C_Btn,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        b.FlatAppearance.BorderSize = 0; // 无边框，消除白边
        return b;
    }

    /// <summary>切换页签：内容页显隐 + 选中样式刷新（选中 = #FFA500 底黑字）。</summary>
    private void SelectTab(int index)
    {
        if (index < 0 || index >= _tabPages.Length) return;
        _currentTab = index;
        for (int i = 0; i < _tabPages.Length; i++)
        {
            bool sel = i == index;
            _tabPages[i].Visible = sel;
            _tabButtons[i].BackColor = sel ? C_Primary : C_Btn;
            _tabButtons[i].ForeColor = sel ? C_TextBg : Color.White;
        }
    }

    /// <summary>右侧状态面板 (30% 列，原底部 SideStatsPanel 移入)：
    /// 服务阶段 / 模块状态(绿红) / 系统资源 / 运行时长 / Token 统计 / 槽位绑定 / 思考模式，卡片纵向堆叠。</summary>
    private Panel BuildStatusPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = C_Frame,
            Padding = new Padding(12),
            AutoScroll = true,
        };
        // 单列表格：卡片撑满右列宽度 + 等高分行（缩放时按比例分配）
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.Transparent,
        };

        // 卡片工厂：#2d2d2d 底 + 标题行（加粗 11F，比内容大 2 号）+ 内容标签（垂直居中）
        Panel MakeCard(string title, Label content)
        {
            var card = new Panel
            {
                BackColor = C_Card,
                Padding = new Padding(12),
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 8),
            };
            var lblTitle = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = C_Title,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), // 加粗 + 比内容大 2 号
                TextAlign = ContentAlignment.MiddleLeft,
            };
            content.Dock = DockStyle.Fill; // 等高分行下内容垂直居中
            content.TextAlign = ContentAlignment.MiddleCenter;
            card.Controls.Add(content);
            card.Controls.Add(lblTitle); // 后添加 → Dock Top 位于最上
            return card;
        }

        // 服务阶段卡片：显示调度器状态文本（"运行中 · N个在途任务…"），替代原侧边栏 _lblStatus
        _lblStatus = new Label
        {
            Text = "空闲",
            Font = new Font("Microsoft YaHei UI", 9F),
            ForeColor = C_Aux,
        };
        _lblModuleState = new Label
        {
            Text = "网关 已停止",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = C_Red,
            Padding = new Padding(8, 4, 8, 4),
            // 注：AutoSize 已移除——等高分行下内容 Dock=Fill，标签撑满卡片（绿/红底色块）
        };
        _lblResSummary = new Label
        {
            Text = "CPU: — | 内存: —",
            Font = new Font("Consolas", 9F),
            ForeColor = C_TextFg,
        };
        _lblRunTime = new Label
        {
            Text = "—",
            Font = new Font("Consolas", 11F),
            ForeColor = C_Primary,
        };
        _lblTokenSummary = new Label
        {
            Text = "请求: 0",
            Font = new Font("Consolas", 11F),
            ForeColor = C_Primary,
        };
        _lblSlotSummary = new Label
        {
            Text = "槽位: —",
            Font = new Font("Consolas", 11F),
            ForeColor = C_TextFg,
        };
        _lblThinking.Text = "思考: 极速";
        _lblThinking.Font = new Font("Microsoft YaHei UI", 11F);

        var cards = new[]
        {
            MakeCard("服务阶段", _lblStatus), // 调度器状态文本（运行中 · N个在途任务…）
            MakeCard("模块状态", _lblModuleState),
            MakeCard("系统资源", _lblResSummary),
            MakeCard("运行时长", _lblRunTime),
            MakeCard("Token 统计", _lblTokenSummary),
            MakeCard("槽位绑定", _lblSlotSummary),
            MakeCard("思考模式", _lblThinking),
        };
        for (int i = 0; i < cards.Length; i++)
        {
            // 等高分行：所有卡片高度相同，缩放时按比例分配（Percent 均分）
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / cards.Length));
            grid.Controls.Add(cards[i], 0, i);
        }

        panel.Controls.Add(grid);
        return panel;
    }

    /// <summary>创建统一风格侧边栏按钮（#3d3d3d 底白字 + 左侧图标，悬停变亮；图标缺失降级纯文本）。</summary>
    private static Button MakeBtn(string text, string? iconFile = null, bool enabled = true, int h = 34)
    {
        var b = new Button
        {
            Text = text,
            Size = new Size(168, h),
            FlatStyle = FlatStyle.Flat,
            BackColor = C_Btn,
            ForeColor = Color.White, // 统一白字（禁用态也保持白色，清晰）
            Enabled = enabled,
            Font = new Font("Microsoft YaHei UI", 9F),
            TextAlign = ContentAlignment.MiddleCenter,
            ImageAlign = ContentAlignment.MiddleCenter,
            TextImageRelation = TextImageRelation.ImageBeforeText, // 图标+文字整体居中
        };
        b.FlatAppearance.BorderSize = 0; // 无边框，消除白边
        var img = LoadIcon(iconFile ?? "");
        if (img != null) b.Image = img;
        b.MouseEnter += (_, _) => { if (b.Enabled) b.BackColor = C_BtnHover; };
        b.MouseLeave += (_, _) => { if (b.Enabled) b.BackColor = C_Btn; };
        return b;
    }

    /// <summary>创建统一风格 DataGridView（#1e1e1e 底 / #2d2d2d 网格线，无边框）。</summary>
    private static DataGridView MakeGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        BorderStyle = BorderStyle.None, // 无边框，消除白边
        BackgroundColor = C_TextBg,
        ForeColor = C_TextFg,
        GridColor = C_Card,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
    };

    private static DataGridViewTextBoxColumn MakeGridCol(string header) => new()
    {
        HeaderText = header,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    /// <summary>可编辑 CheckBox 列（槽位管理页：强占/KV缓存开关）。</summary>
    private static DataGridViewCheckBoxColumn MakeCheckCol(string header) => new()
    {
        HeaderText = header,
        SortMode = DataGridViewColumnSortMode.NotSortable,
        AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
    };

    /// <summary>左侧分组标题（small bold，黑底容器——Control Panel / Configuration / User Manual；宽度与按键一致，由侧边栏网格中列控制）。</summary>
    private static Label MakeSectionTitle(string text) => new()
    {
        Text = $"  {text}",
        Dock = DockStyle.Fill,
        ForeColor = C_Title,
        BackColor = Color.Black, // 黑底容器（层次感）
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 4, 0, 4), // 上下内边距，形成容器包裹感
    };

    /// <summary>CheckBox 样式：ForeColor=黑（标签文字清晰）+ FlatStyle.Flat + BackColor=白（勾选框底色白，勾默认黑）。禁用时灰。</summary>
    private void ApplyBlackCheck(Control c)
    {
        if (c is CheckBox cb)
        {
            cb.ForeColor = Color.Black; // 标签文字黑色（清晰）
            cb.FlatStyle = FlatStyle.Flat; // 扁平风格
            cb.BackColor = Color.White; // 勾选框底色白（勾默认黑，明显）
            // 禁用时统一灰（ApplyPhase 中处理 Enabled 切换时同步刷新）
            cb.CheckedChanged += (_, _) =>
            {
                var color = cb.Enabled ? Color.Black : Color.FromArgb(0x88, 0x88, 0x88);
                cb.ForeColor = color;
            };
        }
    }

    /// <summary>构建 Configuration 面板（14 项配置 + 浏览按钮）。字体白色，暗色背景。</summary>
    private Control BuildConfigPanel()
    {
        // 浏览按钮边框清零（对象初始化器内无法引用实例成员，在此统一设置）
        _btnBrowseExe.FlatAppearance.BorderSize = 0;
        _btnBrowseModel.FlatAppearance.BorderSize = 0;

        // 文字框白字（禁用时也保持白字，清晰）+ CheckBox 勾改黑
        foreach (var c in new[] { _txtExe, _txtModel, _txtExtra, _txtPcoreMask, _txtKvCachePath })
            if (c is TextBox tb) tb.ForeColor = Color.White;
        foreach (var c in new[] { _chkNoKv, _chkAuto, _chkForceStream, _chkTokenGuard, _chkContinuation, _chkCrashRecover, _chkAutoPreDshRule, _chkAutoPreWebui, _chkAutoPreTrae, _chkAutoPreDshAgent })
            ApplyBlackCheck(c);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void AddRow(string label, Control value, Control? extra)
        {
            int row = panel.RowStyles.Count;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 6, 4),
            };
            panel.Controls.Add(lbl, 0, row);
            value.Margin = new Padding(0, 2, 0, 2);
            panel.Controls.Add(value, 1, row);
            if (extra != null)
            {
                extra.Margin = new Padding(2, 0, 0, 0);
                panel.Controls.Add(extra, 2, row);
            }
        }

        AddRow("exe:", _txtExe, _btnBrowseExe);
        AddRow("模型:", _txtModel, _btnBrowseModel);
        AddRow("端口:", _numPort, null);
        AddRow("ctx:", _numCtx, null);
        AddRow("ngl:", _numNgl, null);
        AddRow("parallel:", _numParallel, null);
        AddRow("kv:", _chkNoKv, null);
        AddRow("线程:", _numThreads, null);
        AddRow("附加:", _txtExtra, null);
        AddRow("休眠(min):", _numIdleMin, null);
        AddRow("P核掩码:", _txtPcoreMask, null);
        AddRow("流式:", _chkForceStream, null);
        AddRow("缓存路径:", _txtKvCachePath, null);
        AddRow("Token Guard:", _chkTokenGuard, null);
        AddRow("输出预留:", _numReservedTokens, null);
        AddRow("输出续接:", _chkContinuation, null);
        AddRow("最大续接:", _numMaxContinuations, null);
        AddRow("续接超时:", _numContTimeout, null);
        AddRow("崩溃恢复:", _chkCrashRecover, null);
        AddRow("最大重启:", _numMaxRestarts, null);
        var autoPreFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent };
        autoPreFlow.Controls.Add(_chkAutoPreDshRule);
        autoPreFlow.Controls.Add(_chkAutoPreWebui);
        autoPreFlow.Controls.Add(_chkAutoPreTrae);
        autoPreFlow.Controls.Add(_chkAutoPreDshAgent);
        AddRow("自动强占:", autoPreFlow, null);
        // 模式行：标签 + CheckBox 同行（AutoSize 让 CheckBox 紧跟标签，不再撑满整行）
        var chkAutoRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent };
        chkAutoRow.Controls.Add(_chkAuto);
        AddRow("模式:", chkAutoRow, null);

        _tooltip.SetToolTip(_txtExtra, "原样拼入命令行；含空格的路径需加引号");
        _tooltip.SetToolTip(_chkForceStream, "把非流式请求改写为 stream=true。仅适用于能解析 SSE 的客户端。");
        _tooltip.SetToolTip(_txtKvCachePath, "KV Cache 保存目录（--slot-save-path）；多槽时驱逐自动 save，重绑定自动 restore。留空 = 禁用。");
        _tooltip.SetToolTip(_chkTokenGuard, "代理层预估算 + 裁剪，防上下文超长 400。预算 = ctx ÷ parallel − 输出预留。");
        _tooltip.SetToolTip(_numReservedTokens, "为模型生成回复保留的 token 数（默认 8192）。");
        _tooltip.SetToolTip(_chkContinuation, "输出被 max_tokens 截断（finish_reason=length）时自动续写；工具调用/流式分片场景自动隔离不介入。");
        _tooltip.SetToolTip(_numMaxContinuations, "单次请求最多自动续接轮数（防死循环，默认 10）。");
        _tooltip.SetToolTip(_numContTimeout, "单轮推理超时秒数，超时返回已生成内容（默认 300）。");
        _tooltip.SetToolTip(_chkCrashRecover, "检测到 bad_alloc（任务级内存耗尽）时自动恢复：服务端存活→KV 快照接续/全量重放（SSE keep-alive 保活，客户端无感）；进程死亡→自动重启后重放。10 分钟内 ≥3 次崩溃触发熔断停止自动恢复。");
        _tooltip.SetToolTip(_numMaxRestarts, "进程死亡分支的最大自动重启次数（0 = 禁用自动重启，默认 2）。");
        _tooltip.SetToolTip(_chkAutoPreDshRule, "勾选后 DSH 规则引擎会话（dsh_rule_*）槽位自动强占：空闲不被 LRU 驱逐，再次提问零 Prefill 开销。");
        _tooltip.SetToolTip(_chkAutoPreWebui, "勾选后 WebUI 会话（webui_*）槽位自动强占：空闲不被 LRU 驱逐。");
        _tooltip.SetToolTip(_chkAutoPreTrae, "勾选后 Trae Work（trae_global）槽位自动强占：空闲不被 LRU 驱逐。");
        _tooltip.SetToolTip(_chkAutoPreDshAgent, "勾选后 DSH 主 Agent（dsh_agent_global）槽位自动强占：空闲不被 LRU 驱逐。注意 parallel=2 时若两槽都被强占，新会话将排队等待（上限 30s）。");

        return panel;
    }

    /// <summary>构建统计面板（汇总行 + 表格 + 清空按钮）。暗色主题，白色文字。</summary>
    private Control BuildStatsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(4),
            BackColor = C_Bg,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(_lblSummary, 0, 0);
        panel.Controls.Add(_btnClearStats, 1, 0);
        panel.Controls.Add(_gridStats, 0, 1);
        panel.SetColumnSpan(_gridStats, 2);

        _gridStats.DefaultCellStyle.BackColor = C_TextBg;
        _gridStats.DefaultCellStyle.ForeColor = C_TextFg;
        _gridStats.AlternatingRowsDefaultCellStyle.BackColor = C_Frame;
        _gridStats.ColumnHeadersDefaultCellStyle.BackColor = C_Card;
        _gridStats.ColumnHeadersDefaultCellStyle.ForeColor = C_Aux;
        _gridStats.Columns.AddRange(
            MakeGridCol("时间"),
            MakeGridCol("输入tokens"),
            MakeGridCol("输入速度(t/s)"),
            MakeGridCol("输出tokens"),
            MakeGridCol("输出速度(t/s)"),
            MakeGridCol("命中率"),
            MakeGridCol("f_sim_best"),
            MakeGridCol("总耗时(s)"));

        return panel;
    }

    // ==================== 配置 <-> UI ====================

    private void LoadConfigToUi() => WriteConfigToUi(_config);

    /// <summary>把配置对象写入全部 UI 控件（启动时 / 载入配置文件共用）。</summary>
    private void WriteConfigToUi(AppConfig cfg)
    {
        _txtExe.Text = cfg.ExePath;
        _txtModel.Text = cfg.ModelPath;
        _numPort.Value = Math.Clamp(cfg.Port, (int)_numPort.Minimum, (int)_numPort.Maximum);
        _numCtx.Value = Math.Clamp(cfg.CtxSize, (int)_numCtx.Minimum, (int)_numCtx.Maximum);
        _numNgl.Value = Math.Clamp(cfg.Ngl, (int)_numNgl.Minimum, (int)_numNgl.Maximum);
        _numParallel.Value = Math.Clamp(cfg.Parallel, (int)_numParallel.Minimum, (int)_numParallel.Maximum);
        _chkNoKv.Checked = cfg.NoKvUnified;
        _numThreads.Value = Math.Clamp(cfg.Threads, (int)_numThreads.Minimum, (int)_numThreads.Maximum);
        _txtExtra.Text = cfg.ExtraArgs;
        _chkAuto.Checked = cfg.AutoMode;
        _numIdleMin.Value = Math.Clamp(cfg.IdleMinutes, (int)_numIdleMin.Minimum, (int)_numIdleMin.Maximum);
        _txtPcoreMask.Text = cfg.PCoreMask;
        _chkForceStream.Checked = cfg.ForceStream;
        _txtKvCachePath.Text = cfg.KvCachePath;
        _chkTokenGuard.Checked = cfg.TokenGuardEnabled;
        _numReservedTokens.Value = Math.Clamp(cfg.ReservedOutputTokens, (int)_numReservedTokens.Minimum, (int)_numReservedTokens.Maximum);
        _chkContinuation.Checked = cfg.ContinuationEnabled;
        _numMaxContinuations.Value = Math.Clamp(cfg.MaxContinuations, (int)_numMaxContinuations.Minimum, (int)_numMaxContinuations.Maximum);
        _numContTimeout.Value = Math.Clamp(cfg.ContinuationTimeoutSeconds, (int)_numContTimeout.Minimum, (int)_numContTimeout.Maximum);
        _chkCrashRecover.Checked = cfg.CrashRecoveryEnabled;
        _numMaxRestarts.Value = Math.Clamp(cfg.MaxAutoRestarts, (int)_numMaxRestarts.Minimum, (int)_numMaxRestarts.Maximum);
        var autoPreSet = new HashSet<string>(cfg.AutoPreemptiveApps.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        _chkAutoPreDshRule.Checked = autoPreSet.Contains("dsh_rule");
        _chkAutoPreWebui.Checked = autoPreSet.Contains("webui");
        _chkAutoPreTrae.Checked = autoPreSet.Contains("trae_global");
        _chkAutoPreDshAgent.Checked = autoPreSet.Contains("dsh_agent_global");
    }

    /// <summary>智能模式下监听器占用前端端口，改端口需重绑，监听中禁止编辑。</summary>
    private void UpdatePortControlState() => _numPort.Enabled = !_config.AutoMode;

    /// <summary>UI → 共享配置对象（内存同步；持久化时机：唤醒成功 / 模式切换 / 关闭）。</summary>
    private void SyncUiToConfig()
    {
        _config.ExePath = _txtExe.Text.Trim();
        _config.ModelPath = _txtModel.Text.Trim();
        _config.Port = (int)_numPort.Value;
        _config.CtxSize = (int)_numCtx.Value;
        _config.Ngl = (int)_numNgl.Value;
        _config.Parallel = (int)_numParallel.Value;
        _config.NoKvUnified = _chkNoKv.Checked;
        _config.Threads = (int)_numThreads.Value;
        _config.ExtraArgs = _txtExtra.Text.Trim();
        _config.AutoMode = _chkAuto.Checked;
        _config.IdleMinutes = (int)_numIdleMin.Value;
        _config.PCoreMask = _txtPcoreMask.Text.Trim();
        _config.ForceStream = _chkForceStream.Checked;
        _config.KvCachePath = _txtKvCachePath.Text.Trim();
        _config.TokenGuardEnabled = _chkTokenGuard.Checked;
        _config.ReservedOutputTokens = (int)_numReservedTokens.Value;
        _config.ContinuationEnabled = _chkContinuation.Checked;
        _config.MaxContinuations = (int)_numMaxContinuations.Value;
        _config.ContinuationTimeoutSeconds = (int)_numContTimeout.Value;
        _config.CrashRecoveryEnabled = _chkCrashRecover.Checked;
        _config.MaxAutoRestarts = (int)_numMaxRestarts.Value;
        var autoPrePrefixes = new List<string>();
        if (_chkAutoPreDshRule.Checked) autoPrePrefixes.Add("dsh_rule");
        if (_chkAutoPreWebui.Checked) autoPrePrefixes.Add("webui");
        if (_chkAutoPreTrae.Checked) autoPrePrefixes.Add("trae_global");
        if (_chkAutoPreDshAgent.Checked) autoPrePrefixes.Add("dsh_agent_global");
        _config.AutoPreemptiveApps = string.Join(",", autoPrePrefixes);
    }

    /// <summary>自动查找 llama-server.exe：配置路径无效时用搜索结果回填。</summary>
    private void AutoFindExe()
    {
        var found = LlamaFinder.Find(_config.ExePath);
        if (found == null)
        {
            AppendLog("未找到 llama-server.exe，请通过「浏览…」手动指定路径。");
            return;
        }
        var current = _txtExe.Text.Trim();
        if (!File.Exists(current))
            _txtExe.Text = found;
    }

    // ==================== 事件 ====================

    private void WireEvents()
    {
        _btnBrowseExe.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择 llama-server.exe",
                Filter = "llama-server.exe|llama-server.exe|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtExe.Text = dlg.FileName;
        };

        _btnBrowseModel.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "选择模型文件",
                Filter = "GGUF 模型 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtModel.Text = dlg.FileName;
        };

        _btnClearLog.Click += (_, _) =>
        {
            lock (_logQueue) _logQueue.Clear(); // 清空队列，防止残留旧日志追加
            _txtLog.Clear();
        };
        _btnClearCache.Click += OnClearCacheClick;
        // 思考模式状态机 UI 入口：开启思考 → XHigh（深度推理）；开启极速 → Off（不注入思考参数，65+ t/s）
        _btnThinkOn.Click += (_, _) => _scheduler.SetThinkingMode(SmartScheduler.ThinkingLevel.XHigh);
        _btnTurbo.Click += (_, _) => _scheduler.SetThinkingMode(SmartScheduler.ThinkingLevel.Off);
        _btnClearStats.Click += (_, _) => _statsParser.Reset();
        _btnExportCfg.Click += OnExportConfigClick;
        _btnImportCfg.Click += OnImportConfigClick;
        _btnStart.Click += OnStartClick;
        _btnStop.Click += (_, _) =>
        {
            SyncUiToConfig();
            _scheduler.StopNow();
        };

        // 参数编辑实时同步到共享配置（唤醒时自动使用最新值）
        _txtExe.TextChanged += OnParamEdited;
        _txtModel.TextChanged += OnParamEdited;
        _numPort.ValueChanged += OnParamEdited;
        _numCtx.ValueChanged += OnParamEdited;
        _numNgl.ValueChanged += OnParamEdited;
        _numParallel.ValueChanged += OnParamEdited;
        _chkNoKv.CheckedChanged += OnParamEdited;
        _numThreads.ValueChanged += OnParamEdited;
        _txtExtra.TextChanged += OnParamEdited;
        _txtPcoreMask.TextChanged += OnParamEdited;
        _txtKvCachePath.TextChanged += OnParamEdited;
        _chkForceStream.CheckedChanged += OnParamEdited;
        _chkTokenGuard.CheckedChanged += OnParamEdited;
        _numReservedTokens.ValueChanged += OnParamEdited;
        _chkContinuation.CheckedChanged += OnParamEdited;
        _numMaxContinuations.ValueChanged += OnParamEdited;
        _numContTimeout.ValueChanged += OnParamEdited;
        _chkCrashRecover.CheckedChanged += OnParamEdited;
        _numMaxRestarts.ValueChanged += OnParamEdited;
        _chkAutoPreDshRule.CheckedChanged += OnParamEdited;
        _chkAutoPreWebui.CheckedChanged += OnParamEdited;
        _chkAutoPreTrae.CheckedChanged += OnParamEdited;
        _chkAutoPreDshAgent.CheckedChanged += OnParamEdited;
        _numIdleMin.ValueChanged += OnIdleEdited;
        _chkAuto.CheckedChanged += OnAutoModeEdited;

        FormClosing += OnFormClosing;
    }

    private void OnParamEdited(object? sender, EventArgs e) => SyncUiToConfig();

    private void OnIdleEdited(object? sender, EventArgs e)
    {
        SyncUiToConfig();
        _scheduler.IdleMinutes = (int)_numIdleMin.Value;
    }

    private void OnAutoModeEdited(object? sender, EventArgs e)
    {
        SyncUiToConfig();
        _scheduler.SetAutoMode(_config.AutoMode);
        UpdatePortControlState();
        if (!_config.Save(out string? err))
            AppendLog($"警告：配置保存失败：{err}");
    }

    private async void OnStartClick(object? sender, EventArgs e)
    {
        SyncUiToConfig();
        try
        {
            await _scheduler.LaunchNowAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"启动失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ==================== 配置导出 / 导入（独立 json 文件） ====================

    /// <summary>保存配置到…：把当前窗口全部配置项序列化到用户选择的 json 文件。</summary>
    private void OnExportConfigClick(object? sender, EventArgs e)
    {
        SyncUiToConfig();
        using var dlg = new SaveFileDialog
        {
            Title = "保存配置到…",
            Filter = "JSON 配置文件 (*.json)|*.json",
            FileName = "llama-harness-config.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName,
                System.Text.Json.JsonSerializer.Serialize(_config,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            AppendLog($"配置已保存到：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>载入配置：读取 json 文件，校验后写入当前窗口全部配置项。</summary>
    private void OnImportConfigClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "载入配置",
            Filter = "JSON 配置文件 (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var cfg = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(dlg.FileName))
                ?? throw new InvalidOperationException("反序列化结果为空");

            // 数值兜底：与 AppConfig.Load 相同规则，防止越界值
            if (cfg.Port is < 1 or > 65534) cfg.Port = 8080;
            if (cfg.CtxSize <= 0) cfg.CtxSize = 262144;
            if (cfg.Ngl < 0) cfg.Ngl = 999;
            if (cfg.Parallel <= 0) cfg.Parallel = 1;
            if (cfg.Threads <= 0) cfg.Threads = Environment.ProcessorCount;
            if (cfg.IdleMinutes <= 0) cfg.IdleMinutes = 15;

            WriteConfigToUi(cfg);   // 写入全部 UI 控件
            SyncUiToConfig();       // UI → 共享配置对象（下次唤醒即生效）
            RefreshThinkingLabel(); // 附加参数可能变化，同步刷新思考模式标签
            AppendLog($"配置已载入：{dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"载入失败：\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>清空 KV Cache：删除缓存目录下所有 *.bin + erase 全部槽位。</summary>
    private async void OnClearCacheClick(object? sender, EventArgs e)
    {
        var kv = _scheduler.GetKvCache();
        if (kv == null)
        {
            MessageBox.Show(this, "KV Cache 未启用（需要 --parallel > 1 且配置了缓存路径）。\n\n请在配置管理中设置「缓存路径」并把 Parallel 改为 2，然后重新启动。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, "确定清空所有 KV Cache 缓存？\n将删除缓存目录下所有 .bin 文件并擦除全部槽位。", "确认",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _btnClearCache.Enabled = false;
        try
        {
            int deleted = await kv.ClearAllAsync();
            AppendLog($"KV Cache 已清空：删除 {deleted} 个缓存文件，全部槽位已擦除。");
        }
        catch (Exception ex)
        {
            AppendLog($"KV Cache 清空失败：{ex.Message}");
        }
        finally
        {
            _btnClearCache.Enabled = true;
        }
    }

    /// <summary>调度器状态文本（非 UI 线程）→ 状态栏。</summary>
    private void OnSchedulerStatus(string text)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => _lblStatus.Text = text);
    }

    /// <summary>思考模式状态变更（非 UI 线程）→ 标签更新。</summary>
    private void OnThinkingModeChanged(SmartScheduler.ThinkingLevel level)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => UpdateThinkingLabel(level));
    }

    /// <summary>更新思考模式标签文本和颜色（四档：极速/轻度/中度/深度）。</summary>
    private void UpdateThinkingLabel(SmartScheduler.ThinkingLevel level)
    {
        _lblThinking.Text = $"思考: {SmartScheduler.LabelOf(level)}";
        _lblThinking.ForeColor = level switch
        {
            SmartScheduler.ThinkingLevel.Off => Color.Silver,
            SmartScheduler.ThinkingLevel.Low => Color.LightGreen,
            SmartScheduler.ThinkingLevel.Medium => Color.DodgerBlue,
            _ => Color.LightBlue, // XHigh
        };
    }

    /// <summary>按当前启动附加参数刷新思考模式标签（仅显示；权威重置在 SmartScheduler 唤醒时执行）。
    /// --reasoning on → XHigh；--reasoning off 或无该参数 → Off（默认不思考）。</summary>
    private void RefreshThinkingLabel()
    {
        UpdateThinkingLabel(SmartScheduler.DetermineInitialThinkingMode(_config.ExtraArgs));
    }

    /// <summary>槽位绑定变更（非 UI 线程）→ 刷新槽位表格 + 管理表格 + 侧边摘要。</summary>
    private void RefreshSlotBindings()
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            RefreshSlotGrid();
            RefreshSlotMgmtGrid();
        });
    }

    /// <summary>从调度器获取槽位快照并填充绑定表格 + 管理表格 + 侧边摘要标签。</summary>
    private void RefreshSlotGrid()
    {
        var bindings = _scheduler.GetSlotBindings();
        if (bindings == null || bindings.Count == 0)
        {
            _gridSlots.Rows.Clear();
            _lblSlotSummary.Text = "槽位: —（未启用多槽）";
            return;
        }
        _gridSlots.Rows.Clear();
        foreach (var (key, app, slot, lastActive, _, _) in bindings)
            _gridSlots.Rows.Add(key, app, $"slot {slot}", lastActive.ToString("HH:mm:ss"));
        _lblSlotSummary.Text = $"槽位: {bindings.Count} 绑定";
    }

    /// <summary>填充槽位管理表格（强占/KV缓存 CheckBox 可编辑）。</summary>
    private void RefreshSlotMgmtGrid()
    {
        var bindings = _scheduler.GetSlotBindings();
        if (bindings == null)
        {
            _gridSlotMgmt.Rows.Clear();
            _slotMgmtRowIdx.Clear();
            return;
        }
        // 行 Key = 亲和 Key，避免整表 Clear 后重复刷新闪烁；Dictionary 索引消除 O(n²) 线性扫 Tag（审计）
        foreach (var (key, app, slot, lastActive, preemptive, kvCache) in bindings)
        {
            int idx;
            if (!_slotMgmtRowIdx.TryGetValue(key, out idx))
            {
                idx = _gridSlotMgmt.Rows.Add();
                _gridSlotMgmt.Rows[idx].Tag = key;
                _slotMgmtRowIdx[key] = idx;
            }
            var row = _gridSlotMgmt.Rows[idx];
            row.Cells[0].Value = key;
            row.Cells[1].Value = app;
            row.Cells[2].Value = $"slot {slot}";
            row.Cells[3].Value = preemptive;
            row.Cells[4].Value = kvCache;
            row.Cells[5].Value = lastActive.ToString("HH:mm:ss");
        }
    }

    /// <summary>槽位日志事件（非 UI 线程）→ 显示到槽位页 RichTextBox + slot.log 持久化。</summary>
    private void OnSlotLog(string line)
    {
        LogFile.SlotAppend(line); // 文件持久化（独立 slot.log，2MB 轮切）
        if (!IsHandleCreated) return;
        BeginInvoke(() => AppendSlotLog(line));
    }

    /// <summary>追加一行槽位日志到 RichTextBox（带时间戳 + 级别着色），自动滚到底部。字符上限防膨胀。</summary>
    private void AppendSlotLog(string line)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        _txtSlotLog.AppendText(entry);
        if (_txtSlotLog.TextLength > 100_000)
        {
            _txtSlotLog.SelectionStart = 0;
            _txtSlotLog.SelectionLength = 50_000;
            _txtSlotLog.SelectedText = "";
        }
        // 着色本行
        int start = Math.Max(0, _txtSlotLog.TextLength - entry.Length);
        _txtSlotLog.SelectionStart = start;
        _txtSlotLog.SelectionLength = entry.Length;
        _txtSlotLog.SelectionColor = LogFile.Classify(line) switch
        {
            LogFile.Level.Warn => Color.Gold,
            LogFile.Level.Error => Color.Red,
            _ => Color.LightGreen,
        };
        _txtSlotLog.SelectionStart = _txtSlotLog.TextLength;
        _txtSlotLog.SelectionLength = 0;
        _txtSlotLog.ScrollToCaret();
    }

    /// <summary>槽位管理表格 CheckBox 变更 → 回写调度器（SetPreemptive/SetKvCache）。</summary>
    private void OnSlotMgmtCellChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex < 3 || e.RowIndex >= _gridSlotMgmt.Rows.Count) return;
        var row = _gridSlotMgmt.Rows[e.RowIndex];
        if (row.Tag is not string key) return;
        switch (e.ColumnIndex)
        {
            case 3: // 强占
                bool preemptive = row.Cells[3].Value is true;
                row.Cells[3].Value = preemptive;
                _scheduler.SetSlotPreemptive(key, preemptive);
                AppendLog($"槽位管理：{key} 强占模式 → {(preemptive ? "开启" : "关闭")}");
                break;
            case 4: // KV缓存
                bool kvCache = row.Cells[4].Value is true;
                row.Cells[4].Value = kvCache;
                _scheduler.SetSlotKvCache(key, kvCache);
                AppendLog($"槽位管理：{key} KV Cache → {(kvCache ? "开启" : "关闭")}");
                break;
        }
    }

    /// <summary>阶段切换（非 UI 线程）→ 控件启停 + 状态颜色；唤醒 = 新会话，清空统计。</summary>
    private void OnPhaseChanged(SmartScheduler.Phase phase)
    {
        // llama-server 重启后 task ID 从 0 重新计数，必须重置解析器防跨会话 ID 冲突
        if (phase == SmartScheduler.Phase.Waking)
            _statsParser.Reset();
        if (!IsHandleCreated) return;
        BeginInvoke(() => ApplyPhase(phase));
    }

    // ==================== 统计 ====================

    /// <summary>一轮统计更新（进程输出线程）→ 表格行增量刷新 + 汇总；新行自动滚到底部。</summary>
    private void OnRoundUpdated(LlamaStatsParser.RoundStats s)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            var row = FindStatRow(s.Id);
            bool isNew = row == null;
            if (isNew)
            {
                int idx = _gridStats.Rows.Add();
                row = _gridStats.Rows[idx];
                row.Tag = s.Id;
            }
            if (row != null)
                FillStatRow(row, s);
            UpdateSummary();
            // 仅新增行时滚动到最后一行（已有行的更新不打扰阅读）：设 CurrentCell 会自动滚入视图
            if (isNew && row != null)
                _gridStats.CurrentCell = row.Cells[0];
        });
    }

    /// <summary>超出 50 轮上限、最旧轮次被淘汰（解析器线程）→ 删除对应表格行。</summary>
    private void OnRoundRemoved(LlamaStatsParser.RoundStats s)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            var row = FindStatRow(s.Id);
            if (row != null)
                _gridStats.Rows.Remove(row);
            UpdateSummary(); // 行被淘汰后刷新汇总，保持请求数/合计与表格一致
        });
    }

    /// <summary>会话重置（解析器线程）→ 清空表格。</summary>
    private void OnSessionReset()
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _gridStats.Rows.Clear();
            _lblSummary.Text = "请求: 0";
        });
    }

    private DataGridViewRow? FindStatRow(long id)
    {
        foreach (DataGridViewRow r in _gridStats.Rows)
        {
            if (r.Tag is long tag && tag == id) return r;
        }
        return null;
    }

    private static void FillStatRow(DataGridViewRow row, LlamaStatsParser.RoundStats s)
    {
        row.Cells[0].Value = s.Time.ToString("HH:mm:ss");
        row.Cells[1].Value = s.PromptTokens.ToString();
        row.Cells[2].Value = s.PromptSpeed.ToString("F1");
        row.Cells[3].Value = s.EvalTokens.ToString();
        row.Cells[4].Value = s.EvalSpeed.ToString("F1");
        row.Cells[5].Value = s.HasDraft
            ? $"{s.DraftAccepted}/{s.DraftGenerated} ({(s.DraftGenerated > 0 ? s.DraftAccepted * 100.0 / s.DraftGenerated : 0):F1}%)"
            : "—";
        row.Cells[6].Value = s.FSimBest?.ToString("F3") ?? "—";
        row.Cells[7].Value = (s.TotalMs / 1000.0).ToString("F2");
    }

    /// <summary>累计汇总：请求数、总 tokens、平均速度、加权命中率。同步更新侧边统计标签。</summary>
    private void UpdateSummary()
    {
        var rounds = _statsParser.GetRounds();
        if (rounds.Count == 0)
        {
            _lblSummary.Text = "请求: 0";
            _lblTokenSummary.Text = "请求: 0";
            return;
        }
        double inTok = rounds.Sum(r => r.PromptTokens);
        double outTok = rounds.Sum(r => r.EvalTokens);
        double inMs = rounds.Sum(r => r.PromptMs);
        double outMs = rounds.Sum(r => r.EvalMs);
        long acc = rounds.Where(r => r.HasDraft).Sum(r => r.DraftAccepted);
        long gen = rounds.Where(r => r.HasDraft).Sum(r => r.DraftGenerated);
        string summary = $"请求: {rounds.Count} | " +
            $"输入: {(long)inTok} @ {(inMs > 0 ? inTok / (inMs / 1000.0) : 0):F1} t/s | " +
            $"输出: {(long)outTok} @ {(outMs > 0 ? outTok / (outMs / 1000.0) : 0):F1} t/s | " +
            (gen > 0 ? $"命中: {acc}/{gen}" : "");
        _lblSummary.Text = summary;
        _lblTokenSummary.Text = summary;
    }

    private void ApplyPhase(SmartScheduler.Phase phase)
    {
        // 唤醒时刻追踪：进入 Running 记录（离开 Running 清空）→ 运行时长卡片显示
        _wakeTime = phase == SmartScheduler.Phase.Running ? (_wakeTime ?? DateTime.Now) : null;

        bool busy = phase is SmartScheduler.Phase.Waking
                    or SmartScheduler.Phase.Running
                    or SmartScheduler.Phase.Sleeping;
        _btnStart.Enabled = !busy;
        _btnStop.Enabled = busy;
        // 思考模式是运行态状态机：仅 Running 可切换（唤醒会按启动参数重置基线，待机/过渡态点击无意义）
        _btnThinkOn.Enabled = _btnTurbo.Enabled = phase == SmartScheduler.Phase.Running;

        // 模块状态（右侧状态面板）：运行=绿 / 唤醒·休眠=橙过渡 / 待机=红停止
        bool running = phase == SmartScheduler.Phase.Running;
        _lblModuleState.Text = running ? "网关 运行中" : (phase == SmartScheduler.Phase.Standby ? "网关 已停止" : "网关 过渡中");
        _lblModuleState.BackColor = running ? C_Green : (phase == SmartScheduler.Phase.Standby ? C_Red : C_Warn);

        // 唤醒/运行/休眠期间禁用全部参数控件，防止运行中改参（清单在 BuildUi 一次构建）
        foreach (var c in _paramControls)
            c.Enabled = !busy;
        // 智能模式下监听器占用端口，改端口需重绑，监听中禁止编辑
        if (_config.AutoMode)
            _numPort.Enabled = false;

        // 服务阶段卡片（_lblStatus）：相位切换时设基础文本 + 颜色；调度器状态事件会覆盖为详细文本（"运行中 · N个在途任务…"）
        _lblStatus.Text = phase switch
        {
            SmartScheduler.Phase.Running => "运行",
            SmartScheduler.Phase.Waking => "唤醒中",
            SmartScheduler.Phase.Sleeping => "休眠",
            _ => "空闲",
        };
        _lblStatus.ForeColor = phase switch
        {
            SmartScheduler.Phase.Running => Color.Green,
            SmartScheduler.Phase.Waking => Color.DarkOrange,
            SmartScheduler.Phase.Sleeping => Color.DarkOrange,
            _ => Color.Gray, // Standby 待机
        };

        // 禁用按钮字体统一白色（用户要求：禁用态也保持白字，清晰）
        foreach (var b in new[] { _btnStart, _btnStop, _btnClearLog, _btnClearCache, _btnThinkOn, _btnTurbo, _btnExportCfg, _btnImportCfg })
            if (b != null) b.ForeColor = Color.White;

        // CheckBox 禁用时同步刷新为灰（启用时恢复黑）
        var checkColor = busy ? Color.FromArgb(0x88, 0x88, 0x88) : Color.Black;
        foreach (var c in new[] { _chkNoKv, _chkAuto, _chkForceStream, _chkTokenGuard, _chkContinuation, _chkCrashRecover, _chkAutoPreDshRule, _chkAutoPreWebui, _chkAutoPreTrae, _chkAutoPreDshAgent })
            if (c is CheckBox cb)
                cb.ForeColor = checkColor;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _metricsTimer.Stop();
        _logFlushTimer.Stop();
        // 刷出队列中剩余日志（避免最后几条丢失）
        bool hasPending;
        lock (_logQueue) hasPending = _logQueue.Count > 0;
        if (hasPending) OnLogFlush(null!, EventArgs.Empty);
        if (_scheduler.CurrentPhase is SmartScheduler.Phase.Running
            or SmartScheduler.Phase.Waking
            or SmartScheduler.Phase.Sleeping)
        {
            var r = MessageBox.Show(this,
                "llama-server 正在运行，确定停止并关闭？",
                "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _scheduler.StopNow();
        }
        SyncUiToConfig();
        if (!_config.Save(out string? err))
            AppendLog($"警告：配置保存失败：{err}");
        _scheduler.Dispose();
    }

    // ==================== 日志 ====================

    /// <summary>日志字符上限（约数万行）：防止长期运行无限增长拖慢 UI。</summary>
    private const int MaxLogChars = 400_000;

    /// <summary>追加一行带时间戳的日志并按级别着色（正常绿/警告黄/错误红），自动滚到底部。可来自任意线程。
    /// 防抖：日志先入队列，UI 定时器每 150ms 批量消费（一次 AppendText + 逐行着色），减少重绘闪烁。</summary>
    private void AppendLog(string line)
    {
        LogFile.Append(line); // 文件持久化 + 轮切 + 警告/错误独立输出
        var entry = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        lock (_logQueue) _logQueue.Enqueue((line, entry));
        // 注意：禁止在此（后台线程）Start/Stop 定时器——Win32 SetTimer 绑定调用线程的消息循环，
        // 跨线程 Start 会静默失败导致 UI 显示永久停摆。定时器常驻运行，OnLogFlush 空队列时直接返回。
    }

    /// <summary>批量消费日志队列：一次 AppendText + 逐行着色，大幅减少 RichTextBox 重绘次数。（UI 线程）</summary>
    private void OnLogFlush(object? sender, EventArgs e)
    {
        List<(string line, string entry)> batch;
        lock (_logQueue)
        {
            if (_logQueue.Count == 0) return; // 无新日志，直接返回（定时器常驻）
            batch = new List<(string line, string entry)>(_logQueue.Count);
            while (_logQueue.Count > 0) batch.Add(_logQueue.Dequeue());
        }

        try
        {
            // 一次批量 AppendText（减少重绘）
            foreach (var (_, entry) in batch)
                _txtLog.AppendText(entry);

            // 字符上限截断
            if (_txtLog.TextLength > MaxLogChars)
            {
                _txtLog.SelectionStart = 0;
                _txtLog.SelectionLength = _txtLog.TextLength / 2;
                _txtLog.SelectedText = "";
            }

            // 逐行着色：从末尾往前累加 entry.Length 定位每行起点
            int pos = _txtLog.TextLength;
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                var (line, entry) = batch[i];
                pos -= entry.Length;
                int start = Math.Max(0, pos);
                _txtLog.SelectionStart = start;
                _txtLog.SelectionLength = entry.Length;
                _txtLog.SelectionColor = LogFile.Classify(line) switch
                {
                    LogFile.Level.Warn => Color.Gold,
                    LogFile.Level.Error => Color.Red,
                    _ => Color.LightGreen,
                };
            }

            // 滚动到底部
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.SelectionLength = 0;
            _txtLog.ScrollToCaret();
        }
        catch
        {
            // 显示层异常不得杀死日志管道（文件层已持久化），吞掉继续
        }
    }
}
