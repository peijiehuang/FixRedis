using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FixRedis.Core;

namespace FixRedis.Tests;

internal static class Program
{
    private static readonly string Root = Path.GetFullPath(Path.Combine("artifacts", "tests", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
    public static async Task<int> Main(string[] args)
    {
        // 破坏性的本机服务验收必须显式启用，不会随默认测试或 CI 自动执行。
        if (args.Length == 2 && args[0] == "--local-service") return LocalServiceTests.Run(args[1]);
        if (args.Length > 0 && args[0] == "--private-desktop") return await PrivateDesktop.RunAsync(args.Skip(1).ToArray());
        if (args.Contains("--mutex-probe", StringComparer.Ordinal))
        { using var probe = SingleInstanceLease.TryAcquire(); return probe is null ? 2 : 0; }
        Directory.CreateDirectory(Root);
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("配置解析、引号、重复指令与范围限制", Configuration), ("生产 Redis 只读身份及健康检查", ProductionReadOnly),
            ("正常 RDB：备份后启动并保留数据", ValidRdb), ("截断 RDB：拒绝空库后原文件不变", DeclineRdb),
            ("CRC 损坏 RDB：确认后空库恢复", EmptyRdb), ("AOF 尾部截断：保留完整写入", TailAof),
            ("AOF 中间损坏：截断有效前缀之后", MiddleAof), ("AOF 未完成事务：回退整个事务", TransactionAof),
            ("AOF RDB 前导区损坏：拒绝后不修改", PreambleAof), ("空 AOF：有效文件直接启动", EmptyAof),
            ("AOF 全部数据丢失必须确认", TotalAof), ("双持久化：不回退历史 RDB", DualPersistence),
            ("未启用持久化：不以清空处理启动失败", NoPersistence), ("备份失败：不修复、不启动、不询问清空", BackupFailure),
            ("文件占用：备份失败且原文件不变", LockedFile), ("备份后原文件变化：拒绝应用", ChangedFile),
            ("检查工具权限/版本错误：不误判损坏", ToolFailure), ("服务运行但身份验证失败：不备份数据或启动", RunningFailure),
            ("旧日志损坏信息不触发数据清空", OldLog), ("非数据启动故障不触发清空", EnvironmentalFailure),
            ("AOF 格式有效但命令损坏：依据新增启动日志恢复", SemanticAof), ("认证与错误 PID：不能报告健康", Authentication),
            ("运行时副本角色：不能报告健康", ReplicaRole),
            ("检查进程超时：退出且不悬挂", ProcessTimeout), ("检查进程输出失败：及时停止", OutputFailure), ("重复操作互斥", ConcurrentRepair),
            ("日志密码脱敏及备份清单", Redaction), ("跨进程互斥", CrossProcess),
            ("备份 ACL 与 AOF 替换权限保持", AclPreservation), ("AOF 应用后文件变化：不启动或清空", ChangedAppliedAof),
            ("WinForms 日志、禁用操作、拒绝默认焦点烟测", () => UiSmoke.RunAsync(Root)),
            ("WinForms 实际进程管理员启动检查", () => AdministratorStartupSmoke.RunAsync(Root))
        };
        tests = tests.Concat(AofUnitTests.Cases(Root)).Concat(AofIntegrationTests.Cases(Root)).ToArray();
        if (args.Length == 2 && args[0] == "--filter") tests = tests.Where(test => test.Name.Contains(args[1], StringComparison.Ordinal)).ToArray();
        else if (args.Length == 1 && args[0] == "--headless") tests = tests.Where(test => !test.Name.Contains("WinForms", StringComparison.Ordinal)).ToArray();
        else if (args.Length != 0) { Console.WriteLine("Usage: [--filter text | --headless | --private-desktop ...]"); return 2; }
        if (tests.Length == 0) { Console.WriteLine("No matching tests"); return 2; }
        int failures = 0;
        var results = new List<object>();
        foreach (var test in tests)
        {
            var clock = Stopwatch.StartNew();
            try { await test.Run(); Record(true, null); }
            catch (Exception error) { failures++; Record(false, error.ToString()); }
            void Record(bool passed, string? error)
            {
                var line = $"{(passed ? "PASS" : "FAIL")} {test.Name} ({clock.ElapsedMilliseconds}ms)" + (error is null ? "" : ": " + error);
                Console.WriteLine(line); File.AppendAllText(Path.Combine(Root, "test-report.txt"), line + Environment.NewLine, new UTF8Encoding(false));
                results.Add(new { test.Name, Passed = passed, DurationMs = clock.ElapsedMilliseconds, Error = error });
            }
        }
        await File.WriteAllTextAsync(Path.Combine(Root, "test-results.json"), JsonSerializer.Serialize(new { Total = tests.Length, Passed = tests.Length - failures, Failed = failures, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed; artifacts: {Root}"); return failures == 0 ? 0 : 1;
    }
    private static async Task Configuration()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb);
        File.AppendAllText(f.Installation.ConfigPath, "\n# comment\nappendonly yes\nappendonly no\nsave \"\"\nrequirepass \"quoted\\\"pass\"\n");
        var config = ConfigurationReader.Read(f.Installation.ServerPath, f.Installation.ConfigPath, "5.0.14.1");
        Assert.Equal(PersistenceMode.None, config.Mode); Assert.Equal("quoted\"pass", config.Password); Assert.Equal(f.DirectoryPath, config.DataDirectory);
        foreach (var directive in new[] { "include other.conf", "cluster-enabled yes", "replicaof localhost 6379", "dbfilename ../bad.rdb", "appendonly maybe", "dir \"unterminated" })
        {
            var path = Path.Combine(f.DirectoryPath, "bad.conf"); File.WriteAllText(path, directive);
            await Assert.ThrowsAsync(() => Task.FromResult(ConfigurationReader.Read(f.Installation.ServerPath, path, "5.0.14.1")));
        }
    }
    private static async Task ProductionReadOnly()
    {
        var platform = new WindowsRedisPlatform(new ProcessExecutor()); var installation = await platform.DiscoverAsync();
        var before = (await platform.GetServiceAsync()).ProcessId; var configHash = Hash(installation.ConfigPath); var rdbHash = Hash(installation.RdbPath);
        await platform.VerifyHealthyAsync(installation);
        Assert.Equal(before, (await platform.GetServiceAsync()).ProcessId); Assert.True(configHash.SequenceEqual(Hash(installation.ConfigPath))); Assert.True(rdbHash.SequenceEqual(Hash(installation.RdbPath)));
    }
    private static async Task ValidRdb()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); await f.SeedRdbAsync(); var hash = Hash(f.Installation.RdbPath);
        var result = await f.RunAsync(false); Assert.Equal(RepairOutcome.StartedExisting, result.Outcome); Assert.Equal(0, f.Confirmations);
        Assert.Equal("当前料", await f.CommandAsync("GET", "material")); Assert.True(hash.SequenceEqual(Hash(f.Installation.RdbPath)));
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory!, "manifest.json")));
    }
    private static async Task DeclineRdb()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); await f.SeedRdbAsync(); using (var stream = File.OpenWrite(f.Installation.RdbPath)) stream.SetLength(20);
        var before = Hash(f.Installation.RdbPath); var result = await f.RunAsync(false);
        Assert.Equal(RepairOutcome.Declined, result.Outcome); Assert.Equal(1, f.Confirmations); Assert.Equal(0, f.Starts); Assert.True(before.SequenceEqual(Hash(f.Installation.RdbPath)));
    }
    private static async Task EmptyRdb()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); await f.SeedRdbAsync();
        var bytes = File.ReadAllBytes(f.Installation.RdbPath); bytes[^1] ^= 0x80; File.WriteAllBytes(f.Installation.RdbPath, bytes);
        var result = await f.RunAsync(true); Assert.Equal(RepairOutcome.StartedEmpty, result.Outcome); Assert.Equal(1, f.Confirmations); Assert.Equal("0", await f.CommandAsync("DBSIZE"));
        Assert.True(Directory.GetFiles(f.Installation.DataDirectory, "*.fixredis-quarantine-*").Length > 0);
    }
    private static async Task CheckAof(string tail)
    {
        await using var f = new Fixture(Root, PersistenceMode.Aof); File.WriteAllText(f.Installation.AofPath, Fixture.Resp("SET", "material", "current") + tail, new UTF8Encoding(false));
        var result = await f.RunAsync(false); Assert.Equal(RepairOutcome.RepairedAof, result.Outcome); Assert.Equal(0, f.Confirmations);
        Assert.Equal("current", await f.CommandAsync("GET", "material")); Assert.Equal("1", await f.CommandAsync("DBSIZE"));
        Assert.True(f.Progress.Updates.Any(update => update.Message.Contains("截断", StringComparison.Ordinal)));
    }
    private static Task TailAof() => CheckAof("*3\r\n$3\r\nSET\r\n$4\r\nlost\r\n");
    private static Task MiddleAof() => CheckAof("broken\r\n" + Fixture.Resp("SET", "later", "lost"));
    private static Task TransactionAof() => CheckAof(Fixture.Resp("MULTI") + Fixture.Resp("SET", "transaction", "lost"));
    private static async Task PreambleAof()
    {
        await using var f = new Fixture(Root, PersistenceMode.RdbAndAof); await f.StartAndVerifyAsync(f.Installation);
        await f.CommandAsync("SET", "material", "current"); await f.CommandAsync("BGREWRITEAOF");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var info = await f.CommandAsync("INFO", "persistence");
            if (info.Contains("aof_rewrite_in_progress:0", StringComparison.Ordinal) && info.Contains("aof_last_bgrewrite_status:ok", StringComparison.Ordinal)) break;
            await Task.Delay(100, deadline.Token);
        }
        await f.StopAsync(); Assert.Equal("REDIS", Encoding.ASCII.GetString(File.ReadAllBytes(f.Installation.AofPath).Take(5).ToArray()));
        using (var stream = File.OpenWrite(f.Installation.AofPath)) stream.SetLength(20);
        var hash = Hash(f.Installation.AofPath); var result = await f.RunAsync(false);
        Assert.Equal(RepairOutcome.Declined, result.Outcome); Assert.True(hash.SequenceEqual(Hash(f.Installation.AofPath)));
    }
    private static async Task EmptyAof()
    {
        await using var f = new Fixture(Root, PersistenceMode.Aof); File.WriteAllBytes(f.Installation.AofPath, []);
        Assert.Equal(RepairOutcome.StartedExisting, (await f.RunAsync(false)).Outcome); Assert.Equal(0, f.Confirmations); Assert.Equal("0", await f.CommandAsync("DBSIZE"));
    }
    private static async Task TotalAof()
    {
        await using var f = new Fixture(Root, PersistenceMode.Aof); File.WriteAllText(f.Installation.AofPath, "broken\r\n"); var before = Hash(f.Installation.AofPath);
        Assert.Equal(RepairOutcome.Declined, (await f.RunAsync(false)).Outcome); Assert.Equal(1, f.Confirmations); Assert.Equal(0, f.Starts); Assert.True(before.SequenceEqual(Hash(f.Installation.AofPath)));
    }
    private static async Task DualPersistence()
    {
        await using var f = new Fixture(Root, PersistenceMode.RdbAndAof); await f.SeedRdbAsync(); File.WriteAllText(f.Installation.AofPath, "broken\r\n");
        Assert.Equal(RepairOutcome.StartedEmpty, (await f.RunAsync(true)).Outcome); Assert.Equal("0", await f.CommandAsync("DBSIZE")); Assert.True(!File.Exists(f.Installation.RdbPath));
        Assert.Equal(PersistenceMode.RdbAndAof, ConfigurationReader.Read(f.Installation.ServerPath, f.Installation.ConfigPath, "5.0.14.1").Mode);
    }
    private static async Task NoPersistence()
    {
        await using var f = new Fixture(Root, PersistenceMode.None); File.WriteAllText(f.Installation.RdbPath, "broken");
        Assert.Equal(RepairOutcome.Failed, (await f.RunAsync(true)).Outcome); Assert.Equal(0, f.Confirmations); Assert.Equal("broken", File.ReadAllText(f.Installation.RdbPath));
    }
    private static async Task BackupFailure()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); File.WriteAllText(f.Installation.RdbPath, "broken");
        Assert.Equal(RepairOutcome.Failed, (await f.RunAsync(true, backup: new FailingBackup())).Outcome); Assert.Equal(0, f.Starts); Assert.Equal(0, f.Confirmations);
    }
    private static async Task LockedFile()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); File.WriteAllText(f.Installation.RdbPath, "broken");
        using (File.Open(f.Installation.RdbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.Equal(RepairOutcome.Failed, (await f.RunAsync(true)).Outcome);
        Assert.Equal(0, f.Starts); Assert.Equal(0, f.Confirmations); Assert.Equal("broken", File.ReadAllText(f.Installation.RdbPath));
    }
    private static async Task ChangedFile()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); File.WriteAllText(f.Installation.RdbPath, "broken");
        Assert.Equal(RepairOutcome.Failed, (await f.RunAsync(true, backup: new ChangingBackup())).Outcome); Assert.Equal(0, f.Starts); Assert.Equal(0, f.Confirmations);
    }
    private static async Task ToolFailure()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); File.WriteAllText(f.Installation.RdbPath, "broken");
        foreach (var output in new[] { "Cannot open file", "Can't handle RDB format version 99", "unknown tool output" })
        {
            Assert.Equal(RepairOutcome.Failed, (await f.RunAsync(true, executor: new FakeExecutor(_ => new ProcessResult(1, output)))).Outcome);
            Assert.Equal(0, f.Confirmations); Assert.Equal(0, f.Starts);
        }
    }
    private static async Task RunningFailure()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); var platform = new FailedPlatform(f.Installation, "", true);
        var result = await f.RunAsync(true, platform: platform); Assert.Equal(RepairOutcome.Failed, result.Outcome); Assert.Equal(0, f.Confirmations); Assert.Equal(0, platform.Starts);
        Assert.True(!Directory.Exists(Path.Combine(result.BackupDirectory!, "originals")));
    }
    private static async Task OldLog()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); await f.SeedRdbAsync();
        File.AppendAllText(f.Installation.LogPath!, "RDB CRC error\n"); var platform = new FailedPlatform(f.Installation, "new unrelated failure\n");
        Assert.Equal(RepairOutcome.Failed, (await f.RunAsync(true, platform: platform)).Outcome); Assert.Equal(0, f.Confirmations); Assert.Equal(1, platform.Starts);
    }
    private static async Task EnvironmentalFailure()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); await f.SeedRdbAsync();
        foreach (var line in new[] { "bind: Address already in use", "Permission denied loading RDB", "OOM loading RDB; Wrong RDB checksum", "No space left loading RDB" })
        {
            var platform = new FailedPlatform(f.Installation, line + "\n"); Assert.Equal(RepairOutcome.Failed, (await f.RunAsync(true, platform: platform)).Outcome);
            Assert.Equal(0, f.Confirmations); Assert.Equal(1, platform.Starts);
        }
    }
    private static async Task SemanticAof()
    {
        await using var f = new Fixture(Root, PersistenceMode.Aof); File.WriteAllText(f.Installation.AofPath, Fixture.Resp("unknown-damaged-command"), new UTF8Encoding(false));
        Assert.Equal(RepairOutcome.StartedEmpty, (await f.RunAsync(true)).Outcome); Assert.Equal(1, f.Confirmations); Assert.Equal("0", await f.CommandAsync("DBSIZE"));
    }
    private static async Task Authentication()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb, "test-password"); await f.StartAndVerifyAsync(f.Installation); await f.VerifyHealthyAsync(f.Installation);
        await Assert.ThrowsAsync(async () => await RedisConnection.VerifyAsync(f.Installation, (await f.GetServiceAsync()).ProcessId + 1, CancellationToken.None));
        File.AppendAllText(f.Installation.ConfigPath, "requirepass incorrect\n"); var wrong = ConfigurationReader.Read(f.Installation.ServerPath, f.Installation.ConfigPath, "5.0.14.1");
        await Assert.ThrowsAsync(() => f.VerifyHealthyAsync(wrong));
    }
    private static async Task ProcessTimeout()
    {
        var executor = new ProcessExecutor(TimeSpan.FromMilliseconds(350)); var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync(() => executor.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), ["-NoProfile", "-Command", "Start-Sleep -Seconds 10"]));
        Assert.True(error is RepairException && error.Message.Contains("超时", StringComparison.Ordinal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
    private static async Task ReplicaRole()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); await f.StartAndVerifyAsync(f.Installation);
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("OK", await f.CommandAsync("SLAVEOF", "127.0.0.1", port));
        var error = await Assert.ThrowsAsync(() => f.VerifyHealthyAsync(f.Installation));
        Assert.True(error is RepairException && error.Message.Contains("不支持", StringComparison.Ordinal));
    }
    private static async Task OutputFailure()
    {
        var executor = new ProcessExecutor(TimeSpan.FromSeconds(15)); var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync(() => executor.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoProfile", "-Command", "Write-Output 'started'; Start-Sleep -Seconds 10"], output: _ => throw new IOException("模拟操作日志无法写入")));
        Assert.True(error is IOException && error.Message.Contains("日志无法写入", StringComparison.Ordinal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "Output failure did not terminate the child promptly");
    }
    private static async Task ConcurrentRepair()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb); File.WriteAllText(f.Installation.RdbPath, "broken"); var service = f.Repair();
        var entered = new TaskCompletionSource<bool>(); var decision = new TaskCompletionSource<bool>();
        var first = service.RepairAsync(f.Progress, _ => { entered.SetResult(true); return decision.Task; }); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RepairOutcome.Failed, (await service.RepairAsync(f.Progress, _ => Task.FromResult(true))).Outcome);
        decision.SetResult(false); Assert.Equal(RepairOutcome.Declined, (await first).Outcome); Assert.Equal(0, f.Starts);
    }
    private static async Task Redaction()
    {
        await using var f = new Fixture(Root, PersistenceMode.Rdb, "secret-test-password"); File.WriteAllText(f.Installation.RdbPath, "broken");
        var executor = new FakeExecutor(_ => new ProcessResult(1, "Unexpected EOF reading RDB file secret-test-password")); var result = await f.RunAsync(false, executor: executor);
        Assert.Equal(RepairOutcome.Declined, result.Outcome); Assert.True(!File.ReadAllText(Path.Combine(result.BackupDirectory!, "operation.log")).Contains(f.Installation.Password, StringComparison.Ordinal));
        Assert.True(f.Progress.Updates.All(update => !update.Message.Contains(f.Installation.Password, StringComparison.Ordinal)));
        Assert.True(File.ReadAllText(Path.Combine(result.BackupDirectory!, "manifest.json")).Contains("Sha256", StringComparison.Ordinal));
    }
    private static Task CrossProcess()
    {
        using var lease = SingleInstanceLease.TryAcquire(); Assert.True(lease is not null);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("--mutex-probe");
        using var child = Process.Start(info)!;
        if (!child.WaitForExit(5000)) { child.Kill(true); throw new Exception("Mutex probe timed out"); }
        Assert.Equal(2, child.ExitCode); return Task.CompletedTask;
    }
    private static async Task AclPreservation()
    {
        await using var f = new Fixture(Root, PersistenceMode.Aof);
        File.WriteAllText(f.Installation.AofPath, Fixture.Resp("SET", "material", "current") + "broken\r\n", new UTF8Encoding(false));
        var file = new FileInfo(f.Installation.AofPath); var acl = file.GetAccessControl(AccessControlSections.Access);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null), FileSystemRights.Read, AccessControlType.Allow)); file.SetAccessControl(acl);
        var before = file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var result = await f.RunAsync(false); Assert.Equal(RepairOutcome.RepairedAof, result.Outcome);
        Assert.Equal(before, file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.True(new DirectoryInfo(result.BackupDirectory!).GetAccessControl().AreAccessRulesProtected);
        Assert.True(new DirectoryInfo(result.BackupDirectory!).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().All(rule => rule.IdentityReference.Value != "S-1-1-0"));
    }
    private static async Task ChangedAppliedAof()
    {
        await using var f = new Fixture(Root, PersistenceMode.Aof);
        File.WriteAllText(f.Installation.AofPath, Fixture.Resp("SET", "material", "current") + "broken\r\n", new UTF8Encoding(false));
        var result = await f.RunAsync(true, platform: new ChangedAfterApply(f));
        Assert.Equal(RepairOutcome.Failed, result.Outcome); Assert.Equal(0, f.Starts); Assert.Equal(0, f.Confirmations);
        Assert.True(result.Message.Contains("发生变化", StringComparison.Ordinal));
        Assert.True(File.ReadAllText(f.Installation.AofPath).Contains("external", StringComparison.Ordinal));
    }
}
