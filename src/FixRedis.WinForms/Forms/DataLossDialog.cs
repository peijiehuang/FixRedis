using FixRedis.Core;

namespace FixRedis.WinForms;

/// <summary>展示全部数据丢失风险及备份位置；默认焦点和回车、退出键都选择拒绝。</summary>
public sealed class DataLossDialog : Form
{
    public DataLossDialog(DataLossRequest request)
    {
        SuspendLayout();
        Text = "确认空库恢复"; Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; ClientSize = new Size(580, 365); AutoScaleMode = AutoScaleMode.Dpi;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 6 };
        foreach (var height in new[] { 38, 58, 64, 26, 68, 56 }) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(grid);
        grid.Controls.Add(new Label { Text = "无法保留当前数据恢复", Font = new Font(Font.FontFamily, 14F), Dock = DockStyle.Fill }, 0, 0);
        grid.Controls.Add(new Label { Text = request.Reason + "\n原始文件已备份并校验。", Dock = DockStyle.Fill }, 0, 1);
        grid.Controls.Add(new Label { Text = "继续将丢弃此 Redis 实例的全部数据，并以空库启动服务。\n此操作可能导致当前生产数据丢失，不会恢复历史生产数据。", ForeColor = Color.Firebrick, BackColor = Color.MistyRose, Padding = new Padding(8), Dock = DockStyle.Fill }, 0, 2);
        grid.Controls.Add(new Label { Text = "本次原始文件备份", ForeColor = Color.DimGray, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 3);
        grid.Controls.Add(new TextBox { Text = request.BackupDirectory, ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, BackColor = SystemColors.Window, BorderStyle = BorderStyle.None }, 0, 4);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) };
        var yes = new Button { Text = "接受全部数据丢失并恢复", DialogResult = DialogResult.Yes, Size = new Size(230, 38), BackColor = Color.Firebrick, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, TabIndex = 1 };
        var no = new Button { Name = "declineButton", Text = "否，保留数据并退出", DialogResult = DialogResult.No, Size = new Size(210, 38), TabIndex = 0 };
        buttons.Controls.Add(yes); buttons.Controls.Add(no); grid.Controls.Add(buttons, 0, 5);
        AcceptButton = no; CancelButton = no; Shown += (_, _) => no.Focus();
        AutoScaleDimensions = new SizeF(96F, 96F); ResumeLayout(true);
    }
}
