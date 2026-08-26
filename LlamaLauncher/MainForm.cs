namespace LlamaLauncher;

/// <summary>
/// 主窗口：参数区（黄金底参默认值）+ 操作区 + 日志区（自动滚动）。
/// 进程管控与智能调度全部委托给 SmartScheduler；本类只负责 UI 渲染、控件启停状态。
/// UI 状态机防重复启动：唤醒/运行/休眠期间禁用启动按钮与全部参数控件。
/// </summary>
public class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly SmartScheduler _scheduler;

    // —— 参数控件 ——
    private readonly TextBox _txtExe = new() { Dock = DockStyle.Fill };
    private readonly Button _btnBrowseExe = new() { Text = "浏览…", Size = new Size(100, 32) };
    private readonly TextBox _txtModel = new() { Dock = DockStyle.Fill };
    private readonly Button _btnBrowseModel = new() { Text = "浏览…", Size = new Size(100, 32) };
    private readonly NumericUpDown _numPort = new() { Minimum = 1, Maximum = 65534, Dock = DockStyle.Fill };
    private readonly NumericUpDown _numCtx = new() { Minimum = 256, Maximum = 1_048_576, Dock = DockStyle.Fill };
    private readonly NumericUpDown _numNgl = new() { Minimum = 0, Maximum = 999, Dock = DockStyle.Fill };
    private readonly NumericUpDown _numParallel = new() { Minimum = 1, Maximum = 128, Dock = DockStyle.Fill };
    private readonly CheckBox _chkNoKv = new() { Text = "启用 --no-kv-unified", Dock = DockStyle.Fill };
    private readonly NumericUpDown _numThreads = new() { Minimum = 1, Maximum = 512, Dock = DockStyle.Fill };
    private readonly TextBox _txtExtra = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _chkAuto = new() { Text = "智能按需模式（代理监听 + 闲置自动休眠，推荐）", Dock = DockStyle.Fill };
    private readonly NumericUpDown _numIdleMin = new() { Minimum = 1, Maximum = 120, Dock = DockStyle.Fill };

    // —— 操作区 ——
    // 所有按钮统一尺寸 100x32（与浏览按钮一致）；不用 Dock，Dock 会覆盖固定 Size
    private readonly Button _btnStart = new() { Text = "启动 / 唤醒", Size = new Size(100, 32) };
    private readonly Button _btnStop = new() { Text = "停止", Size = new Size(100, 32), Enabled = false };
    private readonly Button _btnClearLog = new() { Text = "清空日志", Size = new Size(100, 32) };
    private readonly Label _lblStatus = new() { Text = "空闲", Dock = DockStyle.Fill, ForeColor = Color.Gray };

    // —— 系统资源统计（操作行下方，2 秒轮询）——
    private readonly SystemMetrics _metrics = new();
    private readonly Label _lblRes = new()
    {
        // 必须是 Top：窗口内只能有一个 Fill 控件（_split），
        // 若此处用 Fill 会与之竞争导致两者被压成零尺寸
        Dock = DockStyle.Top,
        Height = 24,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DimGray,
        Margin = new Padding(8, 0, 8, 4),
    };
    private readonly System.Windows.Forms.Timer _metricsTimer = new() { Interval = 2000 };

    // —— 日志区 ——
    private readonly TextBox _txtLog = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
        BackColor = Color.FromArgb(30, 30, 30),
        ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9F),
    };

    // —— 统计区（实时解析 print_timing）——
    private readonly LlamaStatsParser _statsParser = new();
    private readonly SplitContainer _split = new()
    {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        SplitterWidth = 5,
    };
    private readonly Label _lblSummary = new()
    {
        Dock = DockStyle.Fill,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(4, 6, 4, 2),
    };
    private readonly Button _btnClearStats = new()
    {
        Text = "清空统计",
        Size = new Size(100, 32),
    };
    private readonly DataGridView _gridStats = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToResizeRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.FromArgb(245, 245, 245),
        RowTemplate = new DataGridViewRow { Height = 22 },
    };

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
        WireEvents();

        // 调度器事件 → UI（内部统一 BeginInvoke）
        _scheduler.Log += AppendLog;
        _scheduler.StatusChanged += OnSchedulerStatus;
        _scheduler.PhaseChanged += OnPhaseChanged;

        // 统计：日志行喂给解析器；解析结果/会话重置回 UI
        _scheduler.Log += line => _statsParser.Feed(line);
        _statsParser.RoundUpdated += OnRoundUpdated;
        _statsParser.SessionReset += OnSessionReset;

        if (loadError != null)
            AppendLog(loadError);
        AutoFindExe();

        // 首帧渲染后再启动监听/布局，避免构造期间 BeginInvoke
        Shown += OnShown;
    }

    private void OnShown(object? sender, EventArgs e)
    {
        // 日志区占 60%、统计区占 40%（用户可拖拽调整）
        _split.SplitterDistance = Math.Max(_split.Height * 3 / 5, 100);
        _scheduler.Initialize();

        // 资源轮询：CPU 需两次采样取差值，首次 tick 建立基准
        _metricsTimer.Tick += OnMetricsTick;
        _metricsTimer.Start();
    }

    /// <summary>每 2 秒刷新资源标签。nvidia-smi 查询可能阻塞数百毫秒，放后台线程执行。</summary>
    private void OnMetricsTick(object? sender, EventArgs e)
    {
        Task.Run(() =>
        {
            double cpu = _metrics.GetCpuPercent();
            var (used, total) = _metrics.GetMemory();
            string? vram = _metrics.GetVramText();
            if (IsDisposed) return; // 窗口已关闭，丢弃本轮结果
            BeginInvoke(() => _lblRes.Text =
                $"CPU: {cpu:F0}%   |   内存: {used:F1}/{total:F1} GB   |   显存: {(vram ?? "—（未检测到 nvidia-smi）")}");
        });
    }

    // ==================== UI 构建 ====================

    private void BuildUi()
    {
        Text = "llama.cpp 智能启动器";
        ClientSize = new Size(880, 640);
        MinimumSize = new Size(700, 480);
        StartPosition = FormStartPosition.CenterScreen;

        // 参数区（表格布局：标签 | 控件 | 浏览按钮）
        var paramPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            Padding = new Padding(8, 8, 8, 2),
            AutoSize = true,
        };
        paramPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paramPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paramPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow(paramPanel, MakeLabel("llama-server.exe："), _txtExe, _btnBrowseExe);
        AddRow(paramPanel, MakeLabel("模型文件（.gguf）："), _txtModel, _btnBrowseModel);
        AddRow(paramPanel, MakeLabel("端口（--port，智能模式下为代理监听端口）："), _numPort, null);
        AddRow(paramPanel, MakeLabel("上下文长度（-c）："), _numCtx, null);
        AddRow(paramPanel, MakeLabel("GPU 层数（-ngl）："), _numNgl, null);
        AddRow(paramPanel, MakeLabel("并发（--parallel）："), _numParallel, null);
        AddRow(paramPanel, MakeLabel("--no-kv-unified："), _chkNoKv, null);
        AddRow(paramPanel, MakeLabel("线程数（-t）："), _numThreads, null);
        AddRow(paramPanel, MakeLabel("附加参数（可选）："), _txtExtra, null);
        AddRow(paramPanel, MakeLabel("闲置休眠分钟数："), _numIdleMin, null);
        AddRow(paramPanel, MakeLabel("模式："), _chkAuto, null);

        // 操作区
        var opPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            Padding = new Padding(8, 6, 8, 6),
            AutoSize = true,
        };
        opPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        opPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        opPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        opPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        opPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        opPanel.Controls.Add(_lblStatus, 0, 0);
        opPanel.Controls.Add(_btnClearLog, 1, 0);
        opPanel.Controls.Add(_btnStop, 2, 0);
        opPanel.Controls.Add(_btnStart, 3, 0);

        // —— 统计面板（汇总行 + 表格）——
        var statsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(8, 2, 8, 6),
        };
        statsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        statsPanel.Controls.Add(_lblSummary, 0, 0);
        statsPanel.Controls.Add(_btnClearStats, 1, 0);
        statsPanel.Controls.Add(_gridStats, 0, 1);
        statsPanel.SetColumnSpan(_gridStats, 2);

        // 日志 / 统计上下分栏（可拖拽；SplitterDistance 在 Shown 后按实际高度分配）
        _split.Panel1.Controls.Add(_txtLog);
        _split.Panel2.Controls.Add(statsPanel);

        // Dock 添加顺序：先 Fill，再 Top（Top 控件按添加顺序自下而上堆叠）
        Controls.Add(_split);
        Controls.Add(_lblRes);   // 资源行：位于操作行下方
        Controls.Add(opPanel);
        Controls.Add(paramPanel);

        // 统计表格列
        _gridStats.Columns.AddRange(
            MakeGridCol("时间"),
            MakeGridCol("输入tokens"),
            MakeGridCol("输入速度(t/s)"),
            MakeGridCol("输出tokens"),
            MakeGridCol("输出速度(t/s)"),
            MakeGridCol("命中率(accepted/generated)"),
            MakeGridCol("f_sim_best"),
            MakeGridCol("总耗时(s)"));
    }

    private static DataGridViewTextBoxColumn MakeGridCol(string header) => new()
    {
        HeaderText = header,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    private static Label MakeLabel(string text) => new Label
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 7, 8, 7),
    };

    private static void AddRow(TableLayoutPanel panel, Control label, Control value, Control? extra)
    {
        int row = panel.RowStyles.Count;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(label, 0, row);
        panel.Controls.Add(value, 1, row);
        if (extra != null)
            panel.Controls.Add(extra, 2, row);
    }

    // ==================== 配置 <-> UI ====================

    private void LoadConfigToUi()
    {
        _txtExe.Text = _config.ExePath;
        _txtModel.Text = _config.ModelPath;
        _numPort.Value = Math.Clamp(_config.Port, (int)_numPort.Minimum, (int)_numPort.Maximum);
        _numCtx.Value = Math.Clamp(_config.CtxSize, (int)_numCtx.Minimum, (int)_numCtx.Maximum);
        _numNgl.Value = Math.Clamp(_config.Ngl, (int)_numNgl.Minimum, (int)_numNgl.Maximum);
        _numParallel.Value = Math.Clamp(_config.Parallel, (int)_numParallel.Minimum, (int)_numParallel.Maximum);
        _chkNoKv.Checked = _config.NoKvUnified;
        _numThreads.Value = Math.Clamp(_config.Threads, (int)_numThreads.Minimum, (int)_numThreads.Maximum);
        _txtExtra.Text = _config.ExtraArgs;
        _chkAuto.Checked = _config.AutoMode;
        _numIdleMin.Value = Math.Clamp(_config.IdleMinutes, (int)_numIdleMin.Minimum, (int)_numIdleMin.Maximum);
    }

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

        _btnClearLog.Click += (_, _) => _txtLog.Clear();
        _btnClearStats.Click += (_, _) => _statsParser.Reset();
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
        _config.Save();
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

    /// <summary>调度器状态文本（非 UI 线程）→ 状态栏。</summary>
    private void OnSchedulerStatus(string text)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => _lblStatus.Text = text);
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

    /// <summary>一轮统计更新（进程输出线程）→ 表格行增量刷新 + 汇总。</summary>
    private void OnRoundUpdated(LlamaStatsParser.RoundStats s)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            var row = FindStatRow(s.Id);
            if (row == null)
            {
                int idx = _gridStats.Rows.Add();
                row = _gridStats.Rows[idx];
                row.Tag = s.Id;
            }
            FillStatRow(row, s);
            UpdateSummary();
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

    /// <summary>累计汇总：请求数、总 tokens、平均速度、加权命中率。</summary>
    private void UpdateSummary()
    {
        var rounds = _statsParser.GetRounds();
        if (rounds.Count == 0)
        {
            _lblSummary.Text = "请求: 0";
            return;
        }
        double inTok = rounds.Sum(r => r.PromptTokens);
        double outTok = rounds.Sum(r => r.EvalTokens);
        // 平均速度按总时长加权（= 总tokens ÷ 总时间），比逐行算术平均更准确
        double inMs = rounds.Sum(r => r.PromptMs);
        double outMs = rounds.Sum(r => r.EvalMs);
        long acc = rounds.Where(r => r.HasDraft).Sum(r => r.DraftAccepted);
        long gen = rounds.Where(r => r.HasDraft).Sum(r => r.DraftGenerated);
        _lblSummary.Text = $"请求: {rounds.Count} | " +
            $"输入: {(long)inTok} tokens @ {(inMs > 0 ? inTok / (inMs / 1000.0) : 0):F1} t/s | " +
            $"输出: {(long)outTok} tokens @ {(outMs > 0 ? outTok / (outMs / 1000.0) : 0):F1} t/s | " +
            (gen > 0 ? $"命中率: {acc}/{gen} ({acc * 100.0 / gen:F1}%)" : "命中率: —");
    }

    private void ApplyPhase(SmartScheduler.Phase phase)
    {
        bool busy = phase is SmartScheduler.Phase.Waking
                    or SmartScheduler.Phase.Running
                    or SmartScheduler.Phase.Sleeping;
        _btnStart.Enabled = !busy;
        _btnStop.Enabled = busy;

        // 唤醒/运行/休眠期间禁用全部参数控件，防止运行中改参
        var paramControls = new Control[]
        {
            _txtExe, _btnBrowseExe, _txtModel, _btnBrowseModel,
            _numPort, _numCtx, _numNgl, _numParallel, _chkNoKv, _numThreads, _txtExtra,
            _chkAuto, _numIdleMin,
        };
        foreach (var c in paramControls)
            c.Enabled = !busy;

        _lblStatus.ForeColor = phase switch
        {
            SmartScheduler.Phase.Running => Color.Green,
            SmartScheduler.Phase.Waking => Color.DarkOrange,
            SmartScheduler.Phase.Sleeping => Color.Red,
            _ => Color.Gray,
        };
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _metricsTimer.Stop();
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
        _config.Save();
        _scheduler.Dispose();
    }

    // ==================== 日志 ====================

    /// <summary>追加一行带时间戳的日志并自动滚到底部。可来自任意线程。</summary>
    private void AppendLog(string line)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        if (!IsHandleCreated)
        {
            _txtLog.AppendText(entry);
            return;
        }
        BeginInvoke(() =>
        {
            _txtLog.AppendText(entry);
            // TextBox 无 ScrollToEnd，用选中位置滚动到末尾
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.SelectionLength = 0;
        });
    }
}
