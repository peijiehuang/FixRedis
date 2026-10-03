using System.ComponentModel;
using System.Diagnostics;
using FixRedis.Core;

namespace FixRedis.WinForms;

/// <summary>展示检测信息、进度和日志；文件修复及服务控制委托给核心层。</summary>
public sealed class MainForm : Form
{
    private static readonly Color Muted = Color.FromArgb(98, 107, 122);
    private static readonly Color Blue = Color.FromArgb(33, 95, 198);
    private readonly IRepairService _service;
    private readonly Button _repair = new() { Name = "repairButton", Text = "一键修复", Size = new Size(146, 42), BackColor = Blue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button _openRedis = new() { Name = "openRedisFolderButton", Text = "打开 Redis 文件夹", Dock = DockStyle.Fill, Margin = new Padding(5, 1, 0, 1), Enabled = false };
    private readonly Button _openBackup = new() { Name = "openBackupFolderButton", Text = "打开备份文件夹", Dock = DockStyle.Fill, Margin = new Padding(5, 1, 0, 1) };
    private readonly Label _state = Label("Redis · 等待检测");
    private readonly Label _mode = Label("等待识别");
    private readonly Label _version = Label("—");
    private readonly Label _config = Label("—");
    private readonly Label _data = Label("—");
    private readonly Label _backup = Label("尚未备份");
    private readonly Label _logState = Label("等待操作");
    private readonly Label _result = Label("等待检测。原始数据将在任何修改前备份。");
    private readonly Label _footer = Label("本机 Redis · 日志随本次操作保存");
    private readonly Label[] _stages = [Label("1  检测配置"), Label("2  备份并校验"), Label("3  检查与修复"), Label("4  启动与验证")];
    private readonly TextBox _log = new() { Name = "logTextBox", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F), WordWrap = true, MaxLength = 0 };
    private bool _busy;
    private string? _redisDirectory;

    public MainForm(IRepairService service)
    {
        SuspendLayout();
        _service = service; Text = AppInfo.WindowTitle; Font = new Font("Microsoft YaHei UI", 9F);
        ForeColor = Color.FromArgb(32, 38, 50); BackColor = Color.FromArgb(245, 246, 248);
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(980, 730); MinimumSize = new Size(820, 680); AutoScaleMode = AutoScaleMode.Dpi;
        _repair.FlatAppearance.BorderSize = 0; BuildLayout();
        AutoScaleDimensions = new SizeF(96F, 96F); ResumeLayout(true);
        _repair.Click += async (_, _) => await RunRepairAsync(); Shown += async (_, _) => await InspectAsync();
        _openRedis.Click += (_, _) => OpenFolder(_redisDirectory);
        _openBackup.Click += (_, _) => OpenFolder(OperationStore.DefaultRootPath);
    }
    private static Label Label(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, AutoEllipsis = true };
    private static TableLayoutPanel Grid(int columns, params float[] rows)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows.Length, Margin = Padding.Empty };
        foreach (var row in rows) grid.RowStyles.Add(new RowStyle(row < 0 ? SizeType.Percent : SizeType.Absolute, Math.Abs(row)));
        if (columns == 1) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }
    private void BuildLayout()
    {
        var root = Grid(1, 86, 140, 54, 30, -100, 56, 25); root.Padding = new Padding(24, 15, 24, 10);
        Controls.Add(root);
        var header = Grid(2, -100); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        var headings = Grid(1, 40, 30); var title = Label("Redis 服务恢复 · " + AppInfo.DisplayVersion); title.Font = new Font(Font.FontFamily, 17F);
        headings.Controls.Add(title, 0, 0); var subtitle = Label("傻瓜式一键修复，轻轻松松恢复 Redis"); subtitle.ForeColor = Muted;
        headings.Controls.Add(subtitle, 0, 1); header.Controls.Add(headings, 0, 0);
        var actions = Grid(1, 48, 25); var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
        buttonPanel.Controls.Add(_repair); actions.Controls.Add(buttonPanel, 0, 0);
        var hint = Label("无法修复时，由你确认是否丢弃数据"); hint.ForeColor = Muted; hint.TextAlign = ContentAlignment.MiddleRight;
        actions.Controls.Add(hint, 0, 1); header.Controls.Add(actions, 1, 0); root.Controls.Add(header, 0, 0);
        var info = Grid(1, 38, -100); info.BackColor = Color.White; info.Padding = new Padding(14, 6, 14, 6);
        var facts = Grid(6, -100);
        foreach (var width in new float[] { 45, 200, 65, 300, 45, 100 }) facts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        Control[] factControls = [Label("服务"), _state, Label("持久化"), _mode, Label("版本"), _version];
        for (int index = 0; index < factControls.Length; index++) { if (index % 2 == 0) factControls[index].ForeColor = Muted; facts.Controls.Add(factControls[index], index, 0); }
        info.Controls.Add(facts, 0, 0);
        var paths = Grid(3, -33, -33, -34); paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85)); paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        string[] names = ["配置文件", "数据文件", "本次备份"]; Label[] values = [_config, _data, _backup];
        for (int row = 0; row < 3; row++) { var label = Label(names[row]); label.ForeColor = Muted; paths.Controls.Add(label, 0, row); paths.Controls.Add(values[row], 1, row); }
        paths.Controls.Add(_openRedis, 2, 0); paths.SetColumnSpan(_data, 2); paths.Controls.Add(_openBackup, 2, 2);
        info.Controls.Add(paths, 0, 1); root.Controls.Add(info, 0, 1);
        var flow = Grid(4, -100);
        for (int index = 0; index < 4; index++) { flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); _stages[index].ForeColor = Muted; flow.Controls.Add(_stages[index], index, 0); }
        root.Controls.Add(flow, 0, 2);
        var logHeader = Grid(2, -100); logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70)); logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        logHeader.Controls.Add(Label("操作日志"), 0, 0); _logState.TextAlign = ContentAlignment.MiddleRight; _logState.ForeColor = Muted; logHeader.Controls.Add(_logState, 1, 0);
        root.Controls.Add(logHeader, 0, 3); root.Controls.Add(_log, 0, 4);
        _result.Name = "resultLabel"; _result.Padding = new Padding(10, 0, 10, 0); _result.Margin = new Padding(0, 10, 0, 5); _result.BackColor = Color.FromArgb(237, 243, 255); _result.ForeColor = Blue;
        root.Controls.Add(_result, 0, 5); _footer.ForeColor = Muted;
        var footer = Grid(2, -100); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        var repository = new LinkLabel { Name = "githubLink", Text = "GitHub 项目", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, LinkColor = Blue };
        repository.Links.Add(0, repository.Text.Length, AppInfo.RepositoryUrl);
        repository.LinkClicked += (_, _) => OpenRepository();
        footer.Controls.Add(_footer, 0, 0); footer.Controls.Add(repository, 1, 0); root.Controls.Add(footer, 0, 6);
    }
    private async Task InspectAsync()
    {
        SetBusy(true);
        try { await _service.InspectAsync(new Progress<RepairUpdate>(Apply)); _result.Text = "等待修复。原始数据将在任何修改前备份。"; }
        catch (Exception error) { Apply(new RepairUpdate("错误", "初始检测失败：" + error.Message)); _result.Text = "无法完成检测，请查看日志。"; }
        finally { SetBusy(false); }
    }
    private async Task RunRepairAsync()
    {
        if (_busy) return;
        SetBusy(true); _log.Clear(); _backup.Text = "尚未备份"; _footer.Text = "正在创建本次操作日志…";
        for (int index = 0; index < 4; index++) { _stages[index].Text = $"{index + 1}  " + StageName(index); _stages[index].ForeColor = Muted; }
        try
        {
            var result = await _service.RepairAsync(new Progress<RepairUpdate>(Apply), ConfirmAsync);
            _result.Text = result.Message; _result.ForeColor = result.Outcome is RepairOutcome.Failed or RepairOutcome.Declined ? Color.Firebrick : Color.SeaGreen;
            _logState.Text = result.Outcome is RepairOutcome.Failed or RepairOutcome.Declined ? "操作已停止" : "操作完成";
            if (result.BackupDirectory is not null) _footer.Text = "本次操作日志：" + Path.Combine(result.BackupDirectory, "operation.log");
        }
        catch (Exception error) { Apply(new RepairUpdate("错误", error.Message)); _result.Text = "操作失败，请查看日志。"; _result.ForeColor = Color.Firebrick; }
        finally { SetBusy(false); }
    }
    private Task<bool> ConfirmAsync(DataLossRequest request)
    {
        // 修复在后台执行，确认框必须切回界面线程；用户选择通过任务返回核心层。
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            try { using var dialog = new DataLossDialog(request); completion.SetResult(dialog.ShowDialog(this) == DialogResult.Yes); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task;
    }
    private static string StageName(int index) => new[] { "检测配置", "备份并校验", "检查与修复", "启动与验证" }[index];
    private void OpenRepository()
    {
        try { using var process = Process.Start(new ProcessStartInfo(AppInfo.RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { MessageBox.Show(this, "无法打开浏览器，请访问：" + AppInfo.RepositoryUrl + "\n\n" + error.Message, "打开 GitHub 项目失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void OpenFolder(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            var message = directory is null ? "尚未识别 Redis 安装目录，请先完成检测。" : "文件夹尚不存在：" + directory;
            Apply(new RepairUpdate("提示", message));
            MessageBox.Show(this, message, "打开文件夹", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(directory);
            using var process = Process.Start(start);
            Apply(new RepairUpdate("信息", "已请求打开文件夹：" + directory));
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            Apply(new RepairUpdate("错误", "打开文件夹失败：" + error.Message));
            MessageBox.Show(this, error.Message, "打开文件夹失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void SetBusy(bool busy) { _busy = busy; _repair.Enabled = !busy; _repair.Text = busy ? "正在处理…" : "一键修复"; }
    private void Apply(RepairUpdate update)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] [{update.Level}] {update.Message}{Environment.NewLine}"); _log.SelectionStart = _log.TextLength; _log.ScrollToCaret();
        if (update.Installation is { } installation)
        {
            _redisDirectory = Path.GetDirectoryName(installation.ServerPath); _openRedis.Enabled = _redisDirectory is not null;
            _mode.Text = installation.ModeLabel; _version.Text = installation.Version; _config.Text = installation.ConfigPath;
            _data.Text = installation.Mode == PersistenceMode.RdbAndAof ? installation.AofPath + "（另备份 RDB）" : installation.UsesAof ? installation.AofPath : installation.RdbPath;
        }
        if (update.BackupDirectory is not null) _backup.Text = update.BackupDirectory;
        if (update.Service is { } service)
        {
            _state.Text = "Redis · " + (service.State switch { ServiceState.Stopped => "已停止", ServiceState.Running => "运行中", ServiceState.StartPending => "启动中", _ => "状态异常" });
            _state.ForeColor = service.State == ServiceState.Running ? Color.SeaGreen : Color.Firebrick;
        }
        if (update.Stage is { } stage && update.State is { } state)
        {
            _stages[stage].Text = (state switch { StageState.Active => "●", StageState.Done => "✓", StageState.Failed => "!", _ => (stage + 1).ToString() }) + "  " + StageName(stage);
            _stages[stage].ForeColor = state switch { StageState.Done => Color.SeaGreen, StageState.Failed => Color.Firebrick, StageState.Active => Blue, _ => Muted };
            if (state == StageState.Active) _logState.Text = "正在" + StageName(stage);
            if (state == StageState.Failed) _logState.Text = update.Level == "等待" ? "等待你的选择" : "操作失败";
            if (state is StageState.Active or StageState.Failed) _result.ForeColor = state == StageState.Failed ? Color.Firebrick : Blue;
            if (state is StageState.Active or StageState.Failed) _result.Text = update.Message;
        }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy) { e.Cancel = true; _result.Text = "操作正在进行，请等待完成后关闭窗口。"; }
        base.OnFormClosing(e);
    }
}
