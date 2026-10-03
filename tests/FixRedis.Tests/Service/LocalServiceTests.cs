using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FixRedis.Core;

namespace FixRedis.Tests;

// 仅显式命令启用：直接操作本机已安装的 Redis 服务，不使用替代进程。
internal sealed class LocalServiceTests
{
    private readonly ProcessExecutor _executor = new();
    private readonly WindowsRedisPlatform _platform;
    private readonly string _root;
    private readonly List<object> _results = [];
    private RedisInstallation _original = null!;
    private RedisInstallation _test = null!;
    private List<SavedFile> _files = [];
    private byte[] _config = [];
    private bool _modified;
    private bool _stoppedOriginal;
    private string _password = "";
    private static readonly byte[] Prefix = Encoding.UTF8.GetBytes(Fixture.Resp("SET", "fixredis:service-test", "有效数据"));
    private sealed record SavedFile(string OriginalPath, string CopyPath, bool Missing, long Size, string? Sha256);
    private sealed record Scenario(string Name, byte[]? Aof, byte[] Rdb, bool MustFailStart, bool Accept, RepairOutcome Outcome, string? Value);

    private LocalServiceTests(string root)
    {
        _root = Path.GetFullPath(root);
        _platform = new WindowsRedisPlatform(_executor);
    }

    public static int Run(string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { Console.Error.WriteLine("真实服务测试需要管理员权限；未修改 Redis。"); return 2; }
        using var lease = SingleInstanceLease.TryAcquire();
        if (lease is null) { Console.Error.WriteLine("另一个修复工具正在运行；未修改 Redis。"); return 2; }
        return new LocalServiceTests(root).RunAsync().GetAwaiter().GetResult();
    }

    private async Task<int> RunAsync()
    {
        // 实机测试目录采用与正式修复备份相同的受限 ACL。
        var runDirectory = new OperationStore(_root).CreateDirectory();
        _runDirectory = runDirectory;
        bool passed = false, restored = false;
        try
        {
            _original = await _platform.DiscoverAsync(); _password = _original.Password;
            await _platform.VerifyHealthyAsync(_original);
            if (_original.Mode == PersistenceMode.None) throw new RepairException("未启用持久化，不能保证恢复测试前内存数据，停止实机测试。");
            _config = await File.ReadAllBytesAsync(_original.ConfigPath);
            Log($"实际服务 Redis；PID={(await _platform.GetServiceAsync()).ProcessId}；程序={_original.ServerPath}；配置={_original.ConfigPath}");
            var healthy = await RepairAsync(false);
            Assert.Equal(RepairOutcome.Healthy, healthy.Result.Outcome);
            Record("原服务健康时不修复", true);
            // 停服前先持久化当前内存数据，确保完整备份包含维护前的数据状态。
            Assert.Equal("OK", await CommandAsync(_original, "SAVE"));
            _stoppedOriginal = true;
            await StopAsync();
            await BackupDirectoryAsync();
            await VerifyBackupAsync();
            Log("完整目录和活动文件备份 SHA-256 校验通过，开始实机故障测试。");
            _modified = true;
            var extra = "\n# FixRedis temporary real-service test\nappendonly yes\nsave \"\"\nappendfsync always\naof-load-truncated no\nauto-aof-rewrite-percentage 0\n";
            if (_original.LogPath is null) throw new RepairException("实机测试要求已有 Redis 文件日志，以保存服务启动失败证据。");
            await File.WriteAllBytesAsync(_original.ConfigPath, _config.Concat(Encoding.UTF8.GetBytes(extra)).ToArray());
            _test = await _platform.DiscoverAsync();
            await File.WriteAllBytesAsync(_test.AofPath, Prefix);
            await _platform.StartAndVerifyAsync(_test);
            Assert.Equal("OK", await CommandAsync(_test, "SAVE"));
            await StopAsync();
            var rdb = await File.ReadAllBytesAsync(_test.RdbPath);
            foreach (var scenario in Scenarios(rdb))
            {
                try { await TestAsync(scenario); Record(scenario.Name, true); }
                catch (Exception error) { Record(scenario.Name, false, error.Message); throw; }
            }
            passed = true;
        }
        catch (Exception error) { Log("测试停止：" + error); }
        finally
        {
            try
            {
                if (_modified)
                {
                    await StopAsync();
                    await VerifyBackupAsync();
                    foreach (var path in new[] { _original.ConfigPath, _original.RdbPath, _original.AofPath })
                    {
                        var file = _files.Single(entry => entry.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase));
                        if (file.Missing) { if (File.Exists(path)) File.Delete(path); }
                        else
                        {
                            File.Copy(file.CopyPath, path, true);
                            Assert.Equal(file.Sha256, await HashAsync(path));
                        }
                    }
                    Log("测试前配置及数据文件已按校验后的备份恢复；测试日志和隔离文件保留。");
                }
                if (_stoppedOriginal)
                {
                    var state = await _platform.GetServiceAsync();
                    if (state.State != ServiceState.Running) await _platform.StartAndVerifyAsync(_original);
                    await _platform.VerifyHealthyAsync(_original);
                    Log($"原服务已恢复并通过身份、认证及持久化验证；PID={(await _platform.GetServiceAsync()).ProcessId}。");
                }
                restored = true;
            }
            catch (Exception error) { Log("恢复原服务失败，请保留此备份并处理：" + error); }
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "results.json"), JsonSerializer.Serialize(new
            { Passed = passed, OriginalServiceRestored = restored, Results = _results }, new JsonSerializerOptions { WriteIndented = true }));
            Log($"结束：测试通过={passed}；原服务恢复={restored}；报告={runDirectory}");
        }
        return passed && restored ? 0 : 1;
    }

    private string _runDirectory = "";
    private void Log(string text)
    {
        if (_password.Length > 0) text = text.Replace(_password, "[已隐藏]", StringComparison.Ordinal);
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        Console.WriteLine(line);
        File.AppendAllText(Path.Combine(_runDirectory, "service-test.log"), line + Environment.NewLine, new UTF8Encoding(false));
    }
    private void Record(string name, bool passed, string? error = null)
    {
        _results.Add(new { Name = name, Passed = passed, Error = error is null ? null : _password.Length == 0 ? error : error.Replace(_password, "[已隐藏]", StringComparison.Ordinal) });
        Log($"{(passed ? "PASS" : "FAIL")} {name}" + (error is null ? "" : "：" + error));
    }

    private async Task BackupDirectoryAsync()
    {
        var install = Path.GetDirectoryName(_original.ServerPath)!;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string directory)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new RepairException("备份目录包含重解析点，停止：" + entry.FullName);
                if (entry is DirectoryInfo child) Collect(child.FullName); else files.Add(entry.FullName);
            }
        }
        Collect(install);
        if (!_original.DataDirectory.Equals(install, StringComparison.OrdinalIgnoreCase)) Collect(_original.DataDirectory);
        files.UnionWith(new[] { _original.ConfigPath, _original.RdbPath, _original.AofPath });
        if (_original.LogPath is not null) files.Add(_original.LogPath);
        foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(install, path);
            var copy = Path.Combine(_runDirectory, "full-backup", relative.StartsWith("..", StringComparison.Ordinal) ? "external-" + _files.Count + "-" + Path.GetFileName(path) : relative);
            if (!File.Exists(path)) { _files.Add(new(path, copy, true, 0, null)); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(path, copy, false);
            _files.Add(new(path, copy, false, new FileInfo(path).Length, await HashAsync(path)));
        }
        await File.WriteAllTextAsync(Path.Combine(_runDirectory, "full-backup-manifest.json"), JsonSerializer.Serialize(_files, new JsonSerializerOptions { WriteIndented = true }));
        Log($"完整备份 {_files.Count} 个文件/缺失记录：{Path.Combine(_runDirectory, "full-backup")}");
    }
    private async Task VerifyBackupAsync()
    {
        foreach (var file in _files.Where(file => !file.Missing))
        {
            Assert.Equal(file.Size, new FileInfo(file.CopyPath).Length);
            Assert.Equal(file.Sha256, await HashAsync(file.CopyPath));
        }
    }
    private async Task StopAsync()
    {
        var state = await _platform.GetServiceAsync();
        if (state.State == ServiceState.Stopped) { await _platform.EnsureStoppedAsync(); return; }
        var result = await _executor.RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), ["stop", "Redis"]);
        if (result.ExitCode != 0) throw new RepairException("停止 Redis 服务失败：" + result.Output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while ((await _platform.GetServiceAsync()).State != ServiceState.Stopped) await Task.Delay(200, deadline.Token);
        await _platform.EnsureStoppedAsync();
    }
    private async Task<(RepairResult Result, int Confirmations)> RepairAsync(bool accept)
    {
        int confirmations = 0;
        var repair = new RepairService(_platform, _executor, new BackupStore(), new OperationStore(Path.Combine(_runDirectory, "repairs")));
        var result = await repair.RepairAsync(new InlineProgress(Log), request =>
        {
            confirmations++;
            Assert.True(File.Exists(Path.Combine(request.BackupDirectory, "manifest.json")));
            Log($"真实服务修复请求全部数据丢失确认：接受={accept}；原因={request.Reason}");
            return Task.FromResult(accept);
        });
        return (result, confirmations);
    }
    private sealed class InlineProgress(Action<string> log) : IProgress<RepairUpdate>
    { public void Report(RepairUpdate value) => log($"[{value.Level}] {value.Message}"); }

    private async Task TestAsync(Scenario scenario)
    {
        Log("开始真实服务场景：" + scenario.Name);
        await StopAsync();
        await File.WriteAllBytesAsync(_test.RdbPath, scenario.Rdb);
        if (scenario.Aof is null) { if (File.Exists(_test.AofPath)) File.Delete(_test.AofPath); }
        else await File.WriteAllBytesAsync(_test.AofPath, scenario.Aof);
        var configHash = await HashAsync(_test.ConfigPath);
        if (scenario.MustFailStart)
        {
            var log = StartupLogCursor.Capture(_test.LogPath);
            var error = await Assert.ThrowsAsync(() => _platform.StartAndVerifyAsync(_test));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await _platform.GetServiceAsync()).State != ServiceState.Stopped) await Task.Delay(200, deadline.Token);
            await _platform.EnsureStoppedAsync();
            var newLog = await log.ReadAsync();
            Assert.True(PersistenceInspector.StartupProvesCorruption(newLog), "服务未启动，但日志不能证明 AOF 加载损坏：" + error.Message);
            Log("已验证实际 Redis 服务因数据加载失败退出：" + newLog);
            Assert.True(scenario.Aof!.SequenceEqual(await File.ReadAllBytesAsync(_test.AofPath)));
        }
        var repair = await RepairAsync(scenario.Accept);
        Assert.Equal(scenario.Outcome, repair.Result.Outcome);
        Assert.Equal(scenario.Outcome is RepairOutcome.Declined or RepairOutcome.StartedEmpty ? 1 : 0, repair.Confirmations);
        var snapshot = JsonSerializer.Deserialize<BackupSnapshot>(await File.ReadAllTextAsync(Path.Combine(repair.Result.BackupDirectory!, "manifest.json")))!;
        var entry = snapshot.Files.Single(file => file.OriginalPath.Equals(_test.AofPath, StringComparison.OrdinalIgnoreCase));
        if (scenario.Aof is null) Assert.True(entry.Missing);
        else
        {
            Assert.True(scenario.Aof.SequenceEqual(await File.ReadAllBytesAsync(entry.CopyPath!)));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(scenario.Aof)), entry.Sha256);
        }
        Assert.Equal(configHash, await HashAsync(_test.ConfigPath));
        if (scenario.Outcome == RepairOutcome.Declined)
        {
            Assert.Equal(ServiceState.Stopped, (await _platform.GetServiceAsync()).State);
            Assert.True(scenario.Aof!.SequenceEqual(await File.ReadAllBytesAsync(_test.AofPath)));
            return;
        }
        await ValidateDataAsync(scenario.Value);
        await StopAsync();
        await _platform.StartAndVerifyAsync(_test);
        await ValidateDataAsync(scenario.Value);
        Log($"真实服务恢复及第二次重启均成功；PID={(await _platform.GetServiceAsync()).ProcessId}");
    }
    private async Task ValidateDataAsync(string? value)
    {
        await _platform.VerifyHealthyAsync(_test);
        Assert.Equal(value is null ? "0" : "1", await CommandAsync(_test, "DBSIZE"));
        if (value is not null) Assert.Equal(value, await CommandAsync(_test, "GET", "fixredis:service-test"));
    }
    private static async Task<string> CommandAsync(RedisInstallation installation, params string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = new TcpClient(); await client.ConnectAsync(installation.Host, installation.Port, deadline.Token);
        await using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
        if (installation.Password.Length > 0) await RedisConnection.CommandAsync(stream, reader, ["AUTH", installation.Password], deadline.Token);
        return await RedisConnection.CommandAsync(stream, reader, args, deadline.Token);
    }
    private static async Task<string> HashAsync(string path)
    { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream)); }

    private static IEnumerable<Scenario> Scenarios(byte[] rdb)
    {
        yield return new("正常 AOF", Prefix, rdb, false, false, RepairOutcome.StartedExisting, "有效数据");
        foreach (var tail in new[]
        {
            ("半条命令", Encoding.UTF8.GetBytes("*3\r\n$3\r\nSET\r\n$4\r\nlost\r\n")),
            ("中间格式损坏", Encoding.UTF8.GetBytes("broken\r\n" + Fixture.Resp("SET", "lost", "lost"))),
            ("CRLF 截断", Encoding.UTF8.GetBytes(Fixture.Resp("SET", "lost", "value"))[..^1]),
            ("UTF8 值截断", Encoding.UTF8.GetBytes(Fixture.Resp("SET", "lost", "尾部中文"))[..^4]),
            ("未完成事务", Encoding.UTF8.GetBytes(Fixture.Resp("MULTI") + Fixture.Resp("SET", "lost", "value")))
        }) yield return new(tail.Item1, Prefix.Concat(tail.Item2).ToArray(), rdb, true, false, RepairOutcome.RepairedAof, "有效数据");
        var mixed = rdb.Concat(Encoding.UTF8.GetBytes(Fixture.Resp("SET", "fixredis:service-test", "更新的数据"))).Concat(Encoding.UTF8.GetBytes("broken\r\n")).ToArray();
        yield return new("混合 RDB 前导区 + 损坏尾部", mixed, rdb, true, false, RepairOutcome.RepairedAof, "更新的数据");
        var crc = rdb.ToArray(); crc[^1] ^= 0x80;
        foreach (var damaged in new[] { ("前导区 CRC", crc), ("全部损坏", Encoding.UTF8.GetBytes("broken\r\n")), ("未知命令", Encoding.UTF8.GetBytes(Fixture.Resp("unknown-command"))) })
            foreach (bool accept in new[] { false, true })
                yield return new(damaged.Item1 + (accept ? "：接受空库" : "：拒绝空库"), damaged.Item2, rdb, true, accept, accept ? RepairOutcome.StartedEmpty : RepairOutcome.Declined, null);
        yield return new("空 AOF 不加载旧 RDB", [], rdb, false, false, RepairOutcome.StartedExisting, null);
        yield return new("缺失 AOF 不加载旧 RDB", null, rdb, false, false, RepairOutcome.StartedExisting, null);
        yield return new("有效 AOF 忽略损坏 RDB", Prefix, Encoding.UTF8.GetBytes("broken RDB"), false, false, RepairOutcome.StartedExisting, "有效数据");
    }
}
