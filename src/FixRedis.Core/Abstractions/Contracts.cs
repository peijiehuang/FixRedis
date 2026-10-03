namespace FixRedis.Core;

// 持久化模式决定活动文件；双持久化启动时仍以 AOF 为准。
public enum PersistenceMode { None, Rdb, Aof, RdbAndAof }
public enum ServiceState { Stopped, StartPending, Running, Other }
public enum RepairOutcome { Healthy, StartedExisting, RepairedAof, StartedEmpty, Declined, Failed }
public enum CheckStatus { Valid, Corrupt, OperationalFailure }
public enum StageState { Waiting, Active, Done, Failed }

/// <summary>从真实服务配置解析的实例信息；密码仅用于认证，不应输出到日志。</summary>
public sealed class RedisInstallation
{
    public required string ServerPath { get; init; }
    public required string ConfigPath { get; init; }
    public required string ConfigHash { get; init; }
    public required string DataDirectory { get; init; }
    public required string RdbPath { get; init; }
    public required string AofPath { get; init; }
    public string? LogPath { get; init; }
    public required PersistenceMode Mode { get; init; }
    public string Version { get; init; } = "5.0";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 6379;
    public string Password { get; init; } = "";
    public string RdbChecker => Path.Combine(Path.GetDirectoryName(ServerPath)!, "redis-check-rdb.exe");
    public string AofChecker => Path.Combine(Path.GetDirectoryName(ServerPath)!, "redis-check-aof.exe");
    public bool UsesAof => Mode is PersistenceMode.Aof or PersistenceMode.RdbAndAof;
    public string ModeLabel => Mode switch
    {
        PersistenceMode.Rdb => "RDB 快照", PersistenceMode.Aof => "AOF 日志",
        PersistenceMode.RdbAndAof => "RDB + AOF（启动加载 AOF）", _ => "未启用持久化"
    };
    public override string ToString() => ConfigPath;
}

public sealed record ServiceSnapshot(ServiceState State, int ProcessId = 0);
public sealed record ProcessResult(int ExitCode, string Output);
public sealed record FileCheck(CheckStatus Status, string Reason, long? ValidBytes = null);
public sealed record DataLossRequest(string Reason, string BackupDirectory);
public sealed record RepairResult(RepairOutcome Outcome, string Message, string? BackupDirectory = null);
public sealed record RepairUpdate(string Level, string Message, int? Stage = null,
    StageState? State = null, RedisInstallation? Installation = null, string? BackupDirectory = null,
    ServiceSnapshot? Service = null);

/// <summary>界面依赖的异步入口；数据丢失确认由调用方提供，核心不直接操作窗体。</summary>
public interface IRepairService
{
    Task InspectAsync(IProgress<RepairUpdate> progress);
    Task<RepairResult> RepairAsync(IProgress<RepairUpdate> progress, Func<DataLossRequest, Task<bool>> confirm);
}

public interface IRedisPlatform
{
    Task<RedisInstallation> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<ServiceSnapshot> GetServiceAsync(CancellationToken cancellationToken = default);
    Task EnsureStoppedAsync(CancellationToken cancellationToken = default);
    Task VerifyHealthyAsync(RedisInstallation installation, CancellationToken cancellationToken = default);
    Task StartAndVerifyAsync(RedisInstallation installation, CancellationToken cancellationToken = default);
}

public interface IProcessExecutor
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string? input = null, Action<string>? output = null, CancellationToken cancellationToken = default);
}

public interface IBackupStore
{
    Task<BackupSnapshot> CreateAsync(RedisInstallation installation, string operationDirectory);
    Task VerifyUnchangedAsync(BackupSnapshot snapshot, IEnumerable<string> paths);
}

public sealed record BackupEntry(string OriginalPath, string? CopyPath, bool Missing, long Size, string? Sha256);
public sealed record BackupSnapshot(string Directory, IReadOnlyList<BackupEntry> Files);

public interface IOperationStore
{
    string CreateDirectory();
    void AppendLog(string directory, string line);
}

public sealed class RepairException(string message, Exception? inner = null) : Exception(message, inner);
