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
    private readonly Button _btnBrowseExe = new() { Text = "浏览…", Dock = DockStyle.Right, AutoSize = true };
    private readonly TextBox _txtModel = new() { Dock = DockStyle.Fill };
    private readonly Button _btnBrowseModel = new() { Text = "浏览…", Dock = DockStyle.Right, AutoSize = true };
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
    private readonly Button _btnStart = new() { Text = "启动 / 唤醒", Dock = DockStyle.Right, AutoSize = true };
    private readonly Button _btnStop = new() { Text = "停止", Dock = DockStyle.Right, AutoSize = true, Enabled = false };
    private readonly Button _btnClearLog = new() { Text = "清空日志", Dock = DockStyle.Right, AutoSize = true };
    private readonly Label _lblStatus = new() { Text = "空闲", Dock = DockStyle.Fill, ForeColor = Color.Gray };

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

        if (loadError != null)
            AppendLog(loadError);
        AutoFindExe();

        // 首帧渲染后再启动监听，避免构造期间 BeginInvoke
        Shown += (_, _) => _scheduler.Initialize();
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

        // Dock 添加顺序：先 Fill，再 Top
        Controls.Add(_txtLog);
        Controls.Add(opPanel);
        Controls.Add(paramPanel);
    }

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

    /// <summary>阶段切换（非 UI 线程）→ 控件启停 + 状态颜色。</summary>
    private void OnPhaseChanged(SmartScheduler.Phase phase)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => ApplyPhase(phase));
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
