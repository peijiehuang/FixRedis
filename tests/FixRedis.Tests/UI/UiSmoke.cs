using FixRedis.Core;
using FixRedis.WinForms;

namespace FixRedis.Tests;

internal static class UiSmoke
{
    public static Task RunAsync(string outputDirectory)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            using var form = new MainForm(new DemoService());
            Exception? failure = null;
            form.Shown += async (_, _) =>
            {
                try
                {
                    var button = (Button)form.Controls.Find("repairButton", true).Single();
                    await Task.Delay(200);
                    Assert.True(button.Enabled);
                    Assert.True(form.Text.Contains("V" + typeof(MainForm).Assembly.GetName().Version!.ToString(3), StringComparison.Ordinal));
                    var github = (LinkLabel)form.Controls.Find("githubLink", true).Single();
                    Assert.Equal("https://github.com/peijiehuang/FixRedis", github.Links[0].LinkData as string);
                    Save(form, Path.Combine(outputDirectory, "winforms-main.png"));
                    button.PerformClick(); await Task.Delay(100); Assert.True(!button.Enabled, "Repair button not disabled");
                    var logs = (TextBox)form.Controls.Find("logTextBox", true).Single();
                    Assert.True(logs.Text.Contains("备份", StringComparison.Ordinal), "Live log update missing");
                    form.Close(); Assert.True(!form.IsDisposed, "Busy form closed");
                    await Task.Delay(600); Assert.True(button.Enabled, "Repair button not re-enabled");
                    Assert.True(form.Controls.Find("resultLabel", true).Single().Text.Contains("验证通过", StringComparison.Ordinal));
                    form.ClientSize = new Size(810, 650); await Task.Delay(50); Save(form, Path.Combine(outputDirectory, "winforms-small.png"));
                    using var dialog = new DataLossDialog(new DataLossRequest("RDB 文件损坏，官方检查工具没有修复功能。", @"C:\ProgramData\FixRedis\Backups\20261003-143205-test"));
                    dialog.Shown += (_, _) => dialog.BeginInvoke(() =>
                    {
                        try
                        {
                            var no = (Button)dialog.Controls.Find("declineButton", true).Single();
                            Assert.True(no.Focused && dialog.AcceptButton == no && dialog.CancelButton == no, "Default choice is not refusal");
                            Save(dialog, Path.Combine(outputDirectory, "winforms-confirm.png"));
                        }
                        catch (Exception error) { failure = error; }
                        finally { dialog.Close(); }
                    });
                    dialog.ShowDialog(form);
                }
                catch (Exception error) { failure = error; }
                finally { form.Close(); }
            };
            Application.Run(form);
            if (failure is not null) completion.SetException(failure); else completion.SetResult();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static void Save(Form form, string path)
    { using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path); }

    private sealed class DemoService : IRepairService
    {
        public Task InspectAsync(IProgress<RepairUpdate> progress)
        {
            progress.Report(new RepairUpdate("检测", "已读取实际服务配置。", Installation: new RedisInstallation
            {
                ServerPath = @"C:\Program Files\Redis\redis-server.exe", ConfigPath = @"C:\Program Files\Redis\redis.windows-service.conf", ConfigHash = "test",
                DataDirectory = @"C:\Program Files\Redis", RdbPath = @"C:\Program Files\Redis\dump.rdb", AofPath = @"C:\Program Files\Redis\appendonly.aof", Mode = PersistenceMode.Rdb, Version = "5.0.14.1"
            }, Service: new ServiceSnapshot(ServiceState.Stopped)));
            return Task.CompletedTask;
        }
        public async Task<RepairResult> RepairAsync(IProgress<RepairUpdate> progress, Func<DataLossRequest, Task<bool>> confirm)
        {
            progress.Report(new RepairUpdate("备份", "备份及 SHA-256 校验完成。", 1, StageState.Done, BackupDirectory: @"C:\ProgramData\FixRedis\Backups\20261003-test"));
            await Task.Delay(500);
            progress.Report(new RepairUpdate("验证", "Running，PING=PONG，实例身份匹配。", 3, StageState.Done, Service: new ServiceSnapshot(ServiceState.Running, 1234)));
            return new RepairResult(RepairOutcome.StartedExisting, "服务启动验证通过。", @"C:\ProgramData\FixRedis\Backups\20261003-test");
        }
    }
}
