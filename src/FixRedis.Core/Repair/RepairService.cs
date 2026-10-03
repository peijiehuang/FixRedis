using System.Security.Cryptography;
using System.Text;

namespace FixRedis.Core;

/// <summary>异步修复入口，使用会话隔离每次状态，并阻止同一实例的重复调用。</summary>
public sealed class RepairService(IRedisPlatform platform, IProcessExecutor executor, IBackupStore backups, IOperationStore operations) : IRepairService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task InspectAsync(IProgress<RepairUpdate> progress)
    {
        var installation = await platform.DiscoverAsync();
        progress.Report(new RepairUpdate("检测", "已读取实际服务配置。", Installation: installation, Service: await platform.GetServiceAsync()));
    }
    public async Task<RepairResult> RepairAsync(IProgress<RepairUpdate> progress, Func<DataLossRequest, Task<bool>> confirm)
    {
        if (!await _gate.WaitAsync(0)) return new RepairResult(RepairOutcome.Failed, "已有修复操作正在执行。");
        try { return await Task.Run(() => new RepairSession(platform, executor, backups, operations, progress, confirm).RunAsync()); }
        finally { _gate.Release(); }
    }
}

// 单次操作按检测、备份、修复、启动顺序执行；任何保护条件失败都终止后续修改。
internal sealed class RepairSession(IRedisPlatform platform, IProcessExecutor executor, IBackupStore backups,
    IOperationStore operations, IProgress<RepairUpdate> progress, Func<DataLossRequest, Task<bool>> confirm)
{
    private RedisInstallation? _installation;
    private BackupSnapshot? _snapshot;
    private string? _operation;
    private string? _appliedAofHash;
    private int _stage;
    private RedisInstallation Installation => _installation ?? throw new InvalidOperationException("Installation not discovered");
    private BackupSnapshot Snapshot => _snapshot ?? throw new InvalidOperationException("Backup not completed");
    private string Operation => _operation ?? throw new InvalidOperationException("Operation not created");

    public async Task<RepairResult> RunAsync()
    {
        try
        {
            _operation = operations.CreateDirectory();
            if (await PreflightAsync()) return Complete(RepairOutcome.Healthy, "Redis 服务正常，无需修复；未重启或修改数据。");
            await BackupAsync();
            var lossReason = await InspectAndRepairAsync();
            if (lossReason is not null) return await EmptyRecoveryAsync(lossReason);
            Emit("检查", "检查与修复完成。", StageState.Done);
            await GuardAsync();
            var error = await TryStartAsync();
            if (error is not null)
            {
                if (Installation.Mode != PersistenceMode.None && PersistenceInspector.StartupProvesCorruption(error.Value.Log) && (await platform.GetServiceAsync()).State == ServiceState.Stopped)
                    return await EmptyRecoveryAsync("本次启动日志明确报告持久化数据加载损坏；服务已退出。");
                throw new RepairException("启动或健康验证失败：" + error.Value.Error.Message, error.Value.Error);
            }
            return Complete(_appliedAofHash is null ? RepairOutcome.StartedExisting : RepairOutcome.RepairedAof,
                _appliedAofHash is null ? "Redis 服务已启动并通过验证；未修改持久化数据文件。" : "AOF 已截断修复，Redis 服务已恢复；原文件备份已保留。");
        }
        catch (Exception error) { return Fail(error); }
    }

    private async Task<bool> PreflightAsync()
    {
        _stage = 0; Emit("检测", "正在读取 Windows 服务 Redis 的实际启动配置。", StageState.Active);
        _installation = await platform.DiscoverAsync();
        var service = await platform.GetServiceAsync();
        Emit("检测", $"版本 {Installation.Version}；{Installation.ModeLabel}；配置：{Installation.ConfigPath}", service: service);
        if (service.State == ServiceState.Running)
        {
            await platform.VerifyHealthyAsync(Installation);
            Emit("验证", "服务 Running，PING=PONG，实例身份、配置和持久化状态匹配。", StageState.Done);
            return true;
        }
        await platform.EnsureStoppedAsync();
        Emit("检测", "服务已停止，没有其他 Redis 进程；不会强杀进程。", StageState.Done);
        return false;
    }

    private async Task BackupAsync()
    {
        _stage = 1; Emit("备份", "备份原始 RDB/AOF、配置及日志，并校验 SHA-256。", StageState.Active);
        _snapshot = await backups.CreateAsync(Installation, Operation);
        foreach (var entry in Snapshot.Files) Emit("备份", entry.Missing ? "原文件不存在，已记录：" + entry.OriginalPath : "复制及校验通过：" + entry.OriginalPath);
        Emit("备份", "原始文件完整备份；不恢复历史生产数据。", StageState.Done, Operation);
    }

    private async Task<string?> InspectAndRepairAsync()
    {
        // 只检查活动持久化文件，AOF 模式不能回退到可能过期的 RDB。
        _stage = 2; Emit("检查", "只在独立副本上检查和修复。", StageState.Active);
        await GuardAsync();
        var activePath = Installation.UsesAof ? Installation.AofPath : Installation.RdbPath;
        var active = Snapshot.Files.Single(entry => entry.OriginalPath.Equals(activePath, StringComparison.OrdinalIgnoreCase));
        if (Installation.Mode == PersistenceMode.None || active.Missing)
        { Emit("检查", "没有可检查的活动持久化文件；不恢复旧快照，只尝试启动和诊断。"); return null; }
        var work = Path.Combine(Operation, "work"); Directory.CreateDirectory(work);
        var candidate = Path.Combine(work, Path.GetFileName(activePath)); File.Copy(active.CopyPath!, candidate, false);
        var inspector = new PersistenceInspector(executor);
        var check = await inspector.CheckAsync(Installation, candidate, ToolOutput);
        if (check.Status == CheckStatus.OperationalFailure) throw new RepairException(check.Reason);
        if (check.Status == CheckStatus.Valid) { Emit("检查", check.Reason); return null; }
        if (!Installation.UsesAof || check.ValidBytes is not > 0) return check.Reason;
        return await RepairAofAsync(inspector, candidate);
    }

    private async Task<string?> RepairAofAsync(PersistenceInspector inspector, string candidate)
    {
        // 官方工具只接触工作副本；复检、数据丢失判断和源文件校验通过后才替换。
        var before = new FileInfo(candidate).Length;
        Emit("修复", "在 AOF 工作副本上执行 --fix，不修改原始备份。");
        var check = await inspector.FixAofAsync(Installation, candidate, ToolOutput);
        if (check.Status == CheckStatus.OperationalFailure) throw new RepairException(check.Reason);
        if (check.Status == CheckStatus.Corrupt) return check.Reason;
        var after = new FileInfo(candidate).Length;
        if (after == 0) return "AOF 修复将丢弃全部有效数据。";
        await GuardAsync();
        var hash = await HashAsync(candidate);
        ReplaceFile(candidate, Installation.AofPath); _appliedAofHash = hash;
        Emit("修复", $"复检通过，已保留 {after} 字节，截断 {before - after} 字节；不推算数据条数。");
        return null;
    }

    private async Task<RepairResult> EmptyRecoveryAsync(string reason)
    {
        // 备份成功不代表允许丢弃数据，必须由确认回调明确返回接受。
        _stage = 2; Emit("等待", reason + " 需要确认是否丢失此实例全部数据。", StageState.Failed);
        if (!await confirm(new DataLossRequest(reason, Operation)))
            return Complete(RepairOutcome.Declined, _appliedAofHash is null ? "用户拒绝空库恢复；原始数据及备份均已保留，服务未恢复。" : "用户拒绝空库恢复；已保留 AOF 修复产物及原始备份，未进一步修改文件。");
        Emit("选择", "用户明确接受丢失此实例全部数据；不恢复历史生产数据。");
        await GuardAsync();
        ResetPersistence();
        Emit("隔离", "当前 RDB/AOF 已移出加载路径，保持原持久化配置。", StageState.Done);
        var error = await TryStartAsync();
        if (error is not null) throw new RepairException("空库启动或健康验证失败：" + error.Value.Error.Message, error.Value.Error);
        return Complete(RepairOutcome.StartedEmpty, "已按确认丢弃当前数据并恢复 Redis 服务；原始备份已保留，未恢复历史数据。");
    }

    private void ResetPersistence()
    {
        if (File.Exists(Installation.RdbPath)) Emit("隔离", IsolateFile(Installation.RdbPath));
        if (!Installation.UsesAof)
        {
            if (File.Exists(Installation.AofPath)) Emit("隔离", IsolateFile(Installation.AofPath));
            return;
        }
        var empty = Path.Combine(Operation, "empty.aof");
        using (var stream = new FileStream(empty, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Flush(true);
        ReplaceFile(empty, Installation.AofPath);
        Emit("准备", "建立有效空 AOF，保持 appendonly 配置。");
    }

    private async Task<(Exception Error, string Log)?> TryStartAsync()
    {
        _stage = 3; Emit("启动", "启动原服务并验证 PING、进程、配置和持久化状态。", StageState.Active);
        var cursor = StartupLogCursor.Capture(Installation.LogPath);
        try
        {
            await platform.StartAndVerifyAsync(Installation);
            Emit("验证", "服务 Running，PING=PONG，实例身份匹配且 loading=0；业务数据完整性未作保证。", StageState.Done, service: await platform.GetServiceAsync());
            return null;
        }
        catch (Exception error)
        {
            var newLog = await cursor.ReadAsync();
            foreach (var line in newLog.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Emit("Redis", line.TrimEnd('\r'));
            return (error, newLog);
        }
    }

    private async Task GuardAsync()
    {
        // 用户确认可能耗时较长，应用文件或启动前都要重新检查占用及外部修改。
        await platform.EnsureStoppedAsync();
        await backups.VerifyUnchangedAsync(Snapshot, _appliedAofHash is null ? [Installation.ConfigPath, Installation.RdbPath, Installation.AofPath] : [Installation.ConfigPath, Installation.RdbPath]);
        if (_appliedAofHash is not null && await HashAsync(Installation.AofPath) != _appliedAofHash)
            throw new RepairException("已应用的 AOF 在验证后发生变化，停止启动或进一步修改。");
    }

    private static async Task<string> HashAsync(string path)
    { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true); return Convert.ToHexString(await SHA256.HashDataAsync(stream)); }
    private string Redact(string text) => _installation?.Password is { Length: > 0 } password ? text.Replace(password, "[已隐藏]", StringComparison.Ordinal) : text;
    private void ToolOutput(string line) => Emit("工具", line);
    private void Emit(string level, string message, StageState? state = null, string? backup = null, ServiceSnapshot? service = null)
    {
        message = Redact(message);
        if (_operation is not null) operations.AppendLog(_operation, $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        progress.Report(new RepairUpdate(level, message, _stage, state, _installation, backup, service));
    }
    private RepairResult Complete(RepairOutcome outcome, string message)
    { Emit("完成", message); return new RepairResult(outcome, message, _operation); }
    private RepairResult Fail(Exception error)
    {
        var message = Redact("操作失败：" + error.Message);
        try { Emit("错误", message, StageState.Failed); }
        catch (Exception logError) { progress.Report(new RepairUpdate("错误", message + "；操作日志写入失败：" + Redact(logError.Message), _stage, StageState.Failed)); }
        return new RepairResult(RepairOutcome.Failed, message, _operation);
    }
    private static string IsolateFile(string path)
    { var target = path + ".fixredis-quarantine-" + Guid.NewGuid().ToString("N"); File.Move(path, target); return target; }
    private static void ReplaceFile(string candidate, string target)
    {
        // 原子替换要求临时文件位于目标卷；File.Replace 同时保留目标文件的 ACL。
        var staging = Path.Combine(Path.GetDirectoryName(target)!, ".fixredis-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var input = File.OpenRead(candidate))
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
            if (File.Exists(target)) File.Replace(staging, target, target + ".fixredis-quarantine-" + Guid.NewGuid().ToString("N"), false);
            else File.Move(staging, target);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}

// 仅读取本次启动新增内容，防止旧的损坏日志触发当前操作的数据清空确认。
internal sealed class StartupLogCursor(string? path, long offset)
{
    public static StartupLogCursor Capture(string? path)
    {
        if (path is null) return new StartupLogCursor(null, 0);
        try { return new StartupLogCursor(path, new FileInfo(path).Length); }
        catch (FileNotFoundException) { return new StartupLogCursor(path, 0); }
        catch (DirectoryNotFoundException) { return new StartupLogCursor(path, 0); }
    }
    public async Task<string> ReadAsync()
    {
        if (path is null) return "";
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Position = stream.Length >= offset ? offset : 0;
            if (stream.Length - stream.Position > 1024 * 1024) throw new RepairException("本次启动日志超过限制，请人工查看 Redis 日志。");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
        catch (FileNotFoundException) { return ""; }
        catch (DirectoryNotFoundException) { return ""; }
    }
}
