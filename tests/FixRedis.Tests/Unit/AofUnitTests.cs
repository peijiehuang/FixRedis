using System.Text;
using System.Text.Json;
using FixRedis.Core;

namespace FixRedis.Tests;

// 不启动 Redis，通过工具输出和依赖替身验证修复失败时的数据保护边界。
internal static class AofUnitTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        (string Name, ProcessResult Output, CheckStatus Expected)[] classifications =
        [
            ("有效输出", new(0, "AOF is valid"), CheckStatus.Valid),
            ("存在有效前缀", new(1, "AOF analyzed: size=100, ok_up_to=80, diff=20"), CheckStatus.Corrupt),
            ("没有有效前缀", new(1, "AOF analyzed: size=100, ok_up_to=0, diff=100"), CheckStatus.Corrupt),
            ("前导区校验损坏", new(1, "RDB CRC error\nRDB preamble of AOF file is not sane"), CheckStatus.Corrupt),
            ("未知前导区错误", new(1, "Can't handle RDB format version 99\nRDB preamble of AOF file is not sane"), CheckStatus.OperationalFailure),
            ("退出码与有效输出冲突", new(1, "AOF is valid"), CheckStatus.OperationalFailure),
            ("有效位置超过文件大小", new(1, "AOF analyzed: size=10, ok_up_to=80, diff=20"), CheckStatus.OperationalFailure),
            ("统计差值不一致", new(1, "AOF analyzed: size=100, ok_up_to=80, diff=30"), CheckStatus.OperationalFailure),
            ("数字溢出", new(1, "AOF analyzed: size=99999999999999999999999, ok_up_to=0, diff=100"), CheckStatus.OperationalFailure),
            ("有效与损坏输出冲突", new(0, "AOF analyzed: size=100, ok_up_to=80, diff=20\nAOF is valid"), CheckStatus.OperationalFailure),
            ("不能打开文件", new(1, "Cannot open file: Access is denied"), CheckStatus.OperationalFailure)
        ];
        foreach (var row in classifications)
            yield return ("[单元] AOF 输出分类：" + row.Name, () => { Assert.Equal(row.Expected, PersistenceInspector.Classify(row.Output, true).Status); return Task.CompletedTask; });
        (string Name, string Log, bool Corrupt)[] startupLogs =
        [
            ("格式损坏", "Bad file format reading the append only file", true),
            ("意外结束", "Unexpected end of file reading the append only file", true),
            ("未知命令", "Unknown command 'damaged' reading the append only file", true),
            ("混合前导区损坏", "Error reading the RDB preamble of the AOF file", true),
            ("权限错误优先", "Permission denied\nBad file format reading the append only file", false),
            ("内存错误优先", "OOM loading RDB; Wrong RDB checksum", false),
            ("磁盘错误优先", "No space left\nBad file format reading the append only file", false),
            ("端口错误", "bind: Address already in use", false),
            ("认证错误", "NOAUTH Authentication required", false),
            ("无关联的跨行关键词", "Unknown command in module\nRDB loaded successfully", false),
            ("无损坏信息", "Ready to accept connections", false)
        ];
        foreach (var row in startupLogs)
            yield return ("[单元] AOF 启动诊断：" + row.Name, () => { Assert.Equal(row.Corrupt, PersistenceInspector.StartupProvesCorruption(row.Log)); return Task.CompletedTask; });
        foreach (var fault in new[] { "fix-timeout", "fix-permission", "recheck-unknown", "fix-corrupt", "fix-empty-deny", "fix-empty-accept", "recheck-corrupt" })
            yield return ("[单元] AOF 修复流程：" + fault, () => FixFailureAsync(root, fault));
        yield return ("[单元] AOF 备份失败：检查器与服务均不得调用", () => BackupFailureAsync(root));
        yield return ("[单元] AOF 空库启动失败：仅启动一次且不循环确认", () => EmptyStartFailureAsync(root));
        yield return ("[单元] AOF 启动后仍运行但验证失败：禁止清空", () => RunningAfterFailureAsync(root));
        yield return ("[单元] AOF 确认回调失败：不应用文件", () => ConfirmationFailureAsync(root));
        yield return ("[单元] AOF 空文件：无需启动外部检查器", () => EmptyFileAsync(root));
    }

    private static async Task FixFailureAsync(string root, string fault)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof);
        var bytes = Encoding.UTF8.GetBytes("prefix-broken-tail"); File.WriteAllBytes(f.Installation.AofPath, bytes);
        var platform = new StubPlatform(f.Installation);
        int calls = 0;
        var executor = new ScriptExecutor((arguments, input) =>
        {
            calls++;
            var candidate = arguments[^1];
            Assert.True(candidate.Contains(Path.DirectorySeparatorChar + "work" + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Tool received live data or backup instead of work copy");
            Assert.True(File.ReadAllBytes(f.Installation.AofPath).SequenceEqual(bytes), "Live file changed before recheck/confirmation");
            if (calls == 1) return Broken(bytes.Length, 6);
            if (calls == 2)
            {
                Assert.Equal("--fix", arguments[0]); Assert.Equal("y\n", input);
                if (fault == "fix-timeout") throw new RepairException("检查工具执行超时，未应用修复结果。");
                if (fault == "fix-permission") throw new UnauthorizedAccessException("Cannot open file");
                if (fault == "fix-corrupt") return Broken(bytes.Length, 6);
                File.WriteAllBytes(candidate, fault.StartsWith("fix-empty", StringComparison.Ordinal) ? [] : bytes[..6]);
                return new ProcessResult(0, "Successfully truncated AOF");
            }
            Assert.Equal(3, calls);
            return fault == "recheck-corrupt" ? Broken(6, 2) : new ProcessResult(1, "unrecognized check output");
        });
        bool accepts = fault == "fix-empty-accept";
        var result = await f.RunAsync(accepts, executor: executor, platform: platform);
        var expected = fault is "fix-corrupt" or "recheck-corrupt" or "fix-empty-deny" ? RepairOutcome.Declined : accepts ? RepairOutcome.StartedEmpty : RepairOutcome.Failed;
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expected is RepairOutcome.Declined or RepairOutcome.StartedEmpty ? 1 : 0, f.Confirmations);
        Assert.Equal(accepts ? 1 : 0, platform.Starts);
        if (!accepts) Assert.True(bytes.SequenceEqual(File.ReadAllBytes(f.Installation.AofPath)), "Failure applied candidate to live file");
        else Assert.Equal(0L, new FileInfo(f.Installation.AofPath).Length);
        AssertBackup(f, result, bytes);
    }

    private static async Task BackupFailureAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof); File.WriteAllText(f.Installation.AofPath, "broken");
        var platform = new StubPlatform(f.Installation);
        var executor = new ScriptExecutor((_, _) => throw new Exception("Must not call tool after failed backup"));
        var result = await f.RunAsync(true, new FailingBackup(), executor, platform);
        Assert.Equal(RepairOutcome.Failed, result.Outcome); Assert.Equal(0, platform.Starts); Assert.Equal(0, executor.Calls); Assert.Equal(0, f.Confirmations);
        Assert.Equal("broken", File.ReadAllText(f.Installation.AofPath));
    }
    private static async Task EmptyStartFailureAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof); File.WriteAllText(f.Installation.AofPath, "broken");
        var platform = new StubPlatform(f.Installation) { Start = () => throw new RepairException("启动超时") };
        var result = await f.RunAsync(true, executor: new ScriptExecutor((_, _) => Broken(6, 0)), platform: platform);
        Assert.Equal(RepairOutcome.Failed, result.Outcome); Assert.Equal(1, platform.Starts); Assert.Equal(1, f.Confirmations);
        AssertBackup(f, result, Encoding.UTF8.GetBytes("broken"));
        Assert.True(Directory.GetFiles(f.DirectoryPath, "*.fixredis-quarantine-*").Length == 1);
    }
    private static async Task RunningAfterFailureAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof); File.WriteAllText(f.Installation.AofPath, "valid");
        var platform = new StubPlatform(f.Installation);
        platform.Start = async () => { platform.State = ServiceState.Running; await File.AppendAllTextAsync(f.Installation.LogPath!, "Unknown command 'broken' reading the append only file\n"); throw new RepairException("验证失败"); };
        var result = await f.RunAsync(true, executor: new ScriptExecutor((_, _) => new(0, "AOF is valid")), platform: platform);
        Assert.Equal(RepairOutcome.Failed, result.Outcome); Assert.Equal(0, f.Confirmations); Assert.Equal(1, platform.Starts); Assert.Equal("valid", File.ReadAllText(f.Installation.AofPath));
    }
    private static async Task ConfirmationFailureAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof); File.WriteAllText(f.Installation.AofPath, "broken");
        var platform = new StubPlatform(f.Installation);
        var service = f.Repair(executor: new ScriptExecutor((_, _) => Broken(6, 0)), platform: platform);
        var result = await service.RepairAsync(f.Progress, _ => throw new InvalidOperationException("Dialog failed"));
        Assert.Equal(RepairOutcome.Failed, result.Outcome); Assert.Equal(0, platform.Starts); Assert.Equal("broken", File.ReadAllText(f.Installation.AofPath));
    }
    private static async Task EmptyFileAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof); File.WriteAllBytes(f.Installation.AofPath, []);
        var executor = new ScriptExecutor((_, _) => throw new Exception("Empty AOF must not need a checker"));
        var result = await f.RunAsync(false, executor: executor, platform: new StubPlatform(f.Installation));
        Assert.Equal(RepairOutcome.StartedExisting, result.Outcome); Assert.Equal(0, executor.Calls); Assert.Equal(0, f.Confirmations);
    }

    internal static void AssertBackup(Fixture f, RepairResult result, byte[] original)
    {
        var manifest = JsonSerializer.Deserialize<BackupSnapshot>(File.ReadAllText(Path.Combine(result.BackupDirectory!, "manifest.json")))!;
        var entry = manifest.Files.Single(file => file.OriginalPath == f.Installation.AofPath);
        Assert.True(!entry.Missing && original.SequenceEqual(File.ReadAllBytes(entry.CopyPath!)), "Original AOF backup bytes were changed");
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), entry.Sha256);
        Assert.Equal((long)original.Length, entry.Size);
    }
    private static ProcessResult Broken(int size, int valid) => new(1, $"AOF analyzed: size={size}, ok_up_to={valid}, diff={size - valid}");
    private sealed class ScriptExecutor(Func<IReadOnlyList<string>, string?, ProcessResult> run) : IProcessExecutor
    {
        public int Calls { get; private set; }
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? input = null, Action<string>? output = null, CancellationToken cancellationToken = default)
        { Calls++; var result = run(arguments, input); output?.Invoke(result.Output); return Task.FromResult(result); }
    }
    private sealed class StubPlatform(RedisInstallation installation) : IRedisPlatform
    {
        public int Starts { get; private set; }
        public ServiceState State { get; set; } = ServiceState.Stopped;
        public Func<Task>? Start { get; set; }
        public Task<RedisInstallation> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(installation);
        public Task<ServiceSnapshot> GetServiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ServiceSnapshot(State, State == ServiceState.Running ? 123 : 0));
        public Task EnsureStoppedAsync(CancellationToken cancellationToken = default) { Assert.Equal(ServiceState.Stopped, State); return Task.CompletedTask; }
        public Task VerifyHealthyAsync(RedisInstallation installation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task StartAndVerifyAsync(RedisInstallation installation, CancellationToken cancellationToken = default)
        { Starts++; if (Start is not null) await Start(); State = ServiceState.Running; }
    }
}
