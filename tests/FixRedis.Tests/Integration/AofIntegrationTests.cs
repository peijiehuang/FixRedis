using System.Text;
using FixRedis.Core;

namespace FixRedis.Tests;

// 使用本机真实 Redis 程序和临时数据，验证加载失败、修复及再次重启后的键值。
internal static class AofIntegrationTests
{
    private static readonly byte[] Prefix = Encoding.UTF8.GetBytes(Fixture.Resp("SET", "material", "有效前缀\r\n含中文"));
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        (string Name, byte[] Tail)[] tails =
        [
            ("尾部命令写入一半", Encoding.UTF8.GetBytes("*3\r\n$3\r\nSET\r\n$4\r\nlost\r\n")),
            ("中间 RESP 损坏", Encoding.UTF8.GetBytes("broken\r\n" + Fixture.Resp("SET", "later", "lost"))),
            ("最后 CRLF 不完整", Encoding.UTF8.GetBytes(Fixture.Resp("SET", "lost", "value"))[..^1]),
            ("UTF8 多字节内容截断", Encoding.UTF8.GetBytes(Fixture.Resp("SET", "lost", "尾部中文"))[..^4]),
            ("事务缺少 EXEC", Encoding.UTF8.GetBytes(Fixture.Resp("MULTI") + Fixture.Resp("SET", "lost", "transaction")))
        ];
        foreach (var row in tails) yield return ("[集成] AOF 启动失败→修复→再次重启：" + row.Name, () => RepairPrefixAsync(root, row.Tail));
        yield return ("[集成] AOF 多余 EXEC 仍能启动：健康实例不修复", () => ExtraExecAsync(root));
        yield return ("[集成] AOF 有效 RDB 前导区 + 损坏命令尾部", () => HybridTailAsync(root));
        foreach (bool accept in new[] { false, true })
        {
            yield return ($"[集成] AOF 前导区 CRC 损坏：{(accept ? "接受" : "拒绝")}", () => HybridCrcAsync(root, accept));
            yield return ($"[集成] AOF 未知命令：{(accept ? "接受" : "拒绝")}", () => SemanticAsync(root, accept, false));
        }
        yield return ("[集成] AOF 参数错误导致进程崩溃：证据不足不清空", () => SemanticAsync(root, true, true));
        yield return ("[集成] AOF 已截断但仍有未知命令：拒绝二次清空", () => SemanticAfterFixAsync(root));
        yield return ("[集成] AOF 无有效前缀：确认后空库", () => TotalLossAsync(root));
        yield return ("[集成] AOF 正常但旧 RDB 损坏：忽略旧快照", () => IgnoresRdbAsync(root));
        foreach (bool missing in new[] { false, true })
            yield return ($"[集成] AOF {(missing ? "缺失" : "为空")}：双持久化不加载旧 RDB", () => EmptyWithOldRdbAsync(root, missing));
    }

    private static async Task RepairPrefixAsync(string root, byte[] tail)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof, strictAof: true);
        var original = Prefix.Concat(tail).ToArray(); await File.WriteAllBytesAsync(f.Installation.AofPath, original);
        await ProveStartupFailureAsync(f, original);
        var config = await File.ReadAllBytesAsync(f.Installation.ConfigPath);
        var result = await f.RunAsync(false);
        Assert.Equal(RepairOutcome.RepairedAof, result.Outcome); Assert.Equal(0, f.Confirmations);
        Assert.True(config.SequenceEqual(await File.ReadAllBytesAsync(f.Installation.ConfigPath)), "Repair changed persistence configuration");
        AofUnitTests.AssertBackup(f, result, original);
        Assert.True(f.Progress.Updates.Any(update => update.Message.Contains($"截断 {tail.Length} 字节", StringComparison.Ordinal)));
        await VerifyPrefixAndRestartAsync(f, "有效前缀\r\n含中文", Prefix);
    }
    private static async Task HybridTailAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.RdbAndAof, strictAof: true); await f.SeedRdbAsync();
        var rdb = await File.ReadAllBytesAsync(f.Installation.RdbPath);
        var prefix = rdb.Concat(Encoding.UTF8.GetBytes(Fixture.Resp("SET", "material", "newer-than-rdb"))).ToArray();
        var original = prefix.Concat(Encoding.UTF8.GetBytes("broken\r\n")).ToArray(); await File.WriteAllBytesAsync(f.Installation.AofPath, original);
        await ProveStartupFailureAsync(f, original);
        var result = await f.RunAsync(false); Assert.Equal(RepairOutcome.RepairedAof, result.Outcome);
        Assert.True(rdb.SequenceEqual(await File.ReadAllBytesAsync(f.Installation.RdbPath)), "Old RDB was altered");
        AofUnitTests.AssertBackup(f, result, original);
        await VerifyPrefixAndRestartAsync(f, "newer-than-rdb", prefix);
    }
    private static async Task HybridCrcAsync(string root, bool accept)
    {
        await using var f = new Fixture(root, PersistenceMode.RdbAndAof, strictAof: true); await f.SeedRdbAsync();
        var original = await File.ReadAllBytesAsync(f.Installation.RdbPath); original[^1] ^= 0x80;
        await File.WriteAllBytesAsync(f.Installation.AofPath, original);
        await ProveStartupFailureAsync(f, original);
        var starts = f.Starts; var result = await f.RunAsync(accept);
        Assert.Equal(accept ? RepairOutcome.StartedEmpty : RepairOutcome.Declined, result.Outcome); Assert.Equal(1, f.Confirmations);
        Assert.Equal(starts + (accept ? 1 : 0), f.Starts); AofUnitTests.AssertBackup(f, result, original);
        if (accept) { Assert.Equal("0", await f.CommandAsync("DBSIZE")); Assert.True(!File.Exists(f.Installation.RdbPath)); }
        else Assert.True(original.SequenceEqual(await File.ReadAllBytesAsync(f.Installation.AofPath)));
    }
    private static async Task SemanticAsync(string root, bool accept, bool wrongArity)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof, strictAof: true);
        var original = Encoding.UTF8.GetBytes(wrongArity ? Fixture.Resp("SET", "missing-value") : Fixture.Resp("not-a-redis-command"));
        await File.WriteAllBytesAsync(f.Installation.AofPath, original);
        var check = await new PersistenceInspector(f.Executor).CheckAsync(f.Installation, f.Installation.AofPath, _ => { });
        Assert.Equal(CheckStatus.Valid, check.Status); // 格式检查器不能验证所有命令语义。
        await ProveStartupFailureAsync(f, original);
        var starts = f.Starts; var result = await f.RunAsync(accept);
        if (wrongArity)
        {
            // 该输入使 Windows Redis 5 崩溃，但没有明确的 AOF 加载错误，不能据此清空。
            Assert.Equal(RepairOutcome.Failed, result.Outcome); Assert.Equal(0, f.Confirmations);
            Assert.Equal(starts + 1, f.Starts); AofUnitTests.AssertBackup(f, result, original);
            Assert.Equal(ServiceState.Stopped, (await f.GetServiceAsync()).State);
            Assert.True(original.SequenceEqual(await File.ReadAllBytesAsync(f.Installation.AofPath)));
            return;
        }
        Assert.Equal(accept ? RepairOutcome.StartedEmpty : RepairOutcome.Declined, result.Outcome); Assert.Equal(1, f.Confirmations);
        Assert.Equal(starts + (accept ? 2 : 1), f.Starts); AofUnitTests.AssertBackup(f, result, original);
        if (accept) Assert.Equal("0", await f.CommandAsync("DBSIZE"));
        else Assert.True(original.SequenceEqual(await File.ReadAllBytesAsync(f.Installation.AofPath)));
    }
    private static async Task SemanticAfterFixAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof, strictAof: true);
        var prefix = Prefix.Concat(Encoding.UTF8.GetBytes(Fixture.Resp("not-a-redis-command"))).ToArray();
        var original = prefix.Concat(Encoding.UTF8.GetBytes("broken\r\n")).ToArray(); await File.WriteAllBytesAsync(f.Installation.AofPath, original);
        var result = await f.RunAsync(false);
        Assert.Equal(RepairOutcome.Declined, result.Outcome); Assert.Equal(1, f.Starts); Assert.Equal(1, f.Confirmations);
        Assert.Equal(ServiceState.Stopped, (await f.GetServiceAsync()).State);
        Assert.True(prefix.SequenceEqual(await File.ReadAllBytesAsync(f.Installation.AofPath)), "Refusal should preserve already-applied truncation");
        Assert.True(result.Message.Contains("已保留 AOF 修复产物", StringComparison.Ordinal));
        AofUnitTests.AssertBackup(f, result, original);
    }
    private static async Task TotalLossAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof, strictAof: true);
        var original = Encoding.UTF8.GetBytes("broken\r\n"); await File.WriteAllBytesAsync(f.Installation.AofPath, original);
        await ProveStartupFailureAsync(f, original);
        var result = await f.RunAsync(true); Assert.Equal(RepairOutcome.StartedEmpty, result.Outcome); Assert.Equal(1, f.Confirmations);
        AofUnitTests.AssertBackup(f, result, original); Assert.Equal("0", await f.CommandAsync("DBSIZE"));
        await f.StopAsync(); await f.StartAndVerifyAsync(f.Installation); Assert.Equal("0", await f.CommandAsync("DBSIZE"));
    }
    private static async Task IgnoresRdbAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.RdbAndAof, strictAof: true);
        await File.WriteAllTextAsync(f.Installation.RdbPath, "corrupt old RDB"); await File.WriteAllBytesAsync(f.Installation.AofPath, Prefix);
        var result = await f.RunAsync(false); Assert.Equal(RepairOutcome.StartedExisting, result.Outcome); Assert.Equal(0, f.Confirmations);
        Assert.Equal("corrupt old RDB", await File.ReadAllTextAsync(f.Installation.RdbPath));
        await VerifyPrefixAndRestartAsync(f, "有效前缀\r\n含中文");
    }
    private static async Task EmptyWithOldRdbAsync(string root, bool missing)
    {
        await using var f = new Fixture(root, PersistenceMode.RdbAndAof, strictAof: true); await f.SeedRdbAsync();
        if (missing) File.Delete(f.Installation.AofPath); else await File.WriteAllBytesAsync(f.Installation.AofPath, []);
        var result = await f.RunAsync(false); Assert.Equal(RepairOutcome.StartedExisting, result.Outcome); Assert.Equal(0, f.Confirmations);
        Assert.Equal("0", await f.CommandAsync("DBSIZE")); Assert.True(File.Exists(f.Installation.RdbPath));
    }
    private static async Task ProveStartupFailureAsync(Fixture f, byte[] original)
    {
        var error = await Assert.ThrowsAsync(() => f.StartAndVerifyAsync(f.Installation));
        Assert.True(error is RepairException && error.Message.Contains("启动退出", StringComparison.Ordinal), "Failure was not a real Redis load failure: " + error.Message);
        Assert.Equal(ServiceState.Stopped, (await f.GetServiceAsync()).State);
        Assert.True(original.SequenceEqual(await File.ReadAllBytesAsync(f.Installation.AofPath)), "Startup probe unexpectedly changed damaged AOF");
    }
    private static async Task ExtraExecAsync(string root)
    {
        await using var f = new Fixture(root, PersistenceMode.Aof, strictAof: true);
        var original = Prefix.Concat(Encoding.UTF8.GetBytes(Fixture.Resp("EXEC"))).ToArray();
        await File.WriteAllBytesAsync(f.Installation.AofPath, original);
        var check = await new PersistenceInspector(f.Executor).CheckAsync(f.Installation, f.Installation.AofPath, _ => { });
        Assert.Equal(CheckStatus.Corrupt, check.Status);
        await f.StartAndVerifyAsync(f.Installation);
        var starts = f.Starts; var result = await f.RunAsync(false);
        Assert.Equal(RepairOutcome.Healthy, result.Outcome); Assert.Equal(starts, f.Starts); Assert.Equal(0, f.Confirmations);
        await VerifyPrefixAndRestartAsync(f, "有效前缀\r\n含中文", original);
    }
    private static async Task VerifyPrefixAndRestartAsync(Fixture f, string value, byte[]? expectedBytes = null)
    {
        Assert.Equal(value, await f.CommandAsync("GET", "material")); Assert.Equal("1", await f.CommandAsync("DBSIZE"));
        await f.StopAsync();
        if (expectedBytes is not null)
            Assert.True(expectedBytes.SequenceEqual(await File.ReadAllBytesAsync(f.Installation.AofPath)), "AOF bytes did not preserve the expected prefix");
        await f.StartAndVerifyAsync(f.Installation);
        Assert.Equal(value, await f.CommandAsync("GET", "material")); Assert.Equal("1", await f.CommandAsync("DBSIZE"));
    }
}
