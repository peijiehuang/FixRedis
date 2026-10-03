using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FixRedis.Core;

namespace FixRedis.Tests;

internal sealed class Capture : IProgress<RepairUpdate>
{
    public List<RepairUpdate> Updates { get; } = [];
    public void Report(RepairUpdate value) { lock (Updates) Updates.Add(value); }
}
internal static class Assert
{
    public static void True(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual) => True(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, actual {actual}");
    public static async Task<Exception> ThrowsAsync(Func<Task> action)
    { try { await action(); } catch (Exception error) { return error; } throw new Exception("Expected failure did not occur"); }
}
internal sealed class Fixture : IRedisPlatform, IAsyncDisposable
{
    public const string RedisDirectory = @"C:\Program Files\Redis";
    public string DirectoryPath { get; }
    public RedisInstallation Installation { get; }
    public int Starts { get; private set; }
    public int Confirmations { get; private set; }
    public Capture Progress { get; } = new();
    public ProcessExecutor Executor { get; } = new();
    private Process? _process;
    private readonly int _port;
    private readonly string _password;
    public Fixture(string root, PersistenceMode mode, string password = "", bool strictAof = false)
    {
        DirectoryPath = Path.Combine(root, "case with spaces-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(DirectoryPath);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _password = password;
        var config = Path.Combine(DirectoryPath, "redis.conf"); var dir = DirectoryPath.Replace('\\', '/');
        File.WriteAllText(config, $"bind 127.0.0.1\nport {_port}\ndir \"{dir}\"\nlogfile \"{dir}/redis.log\"\ndbfilename dump.rdb\nappendfilename appendonly.aof\nappendonly {(mode is PersistenceMode.Aof or PersistenceMode.RdbAndAof ? "yes" : "no")}\nsave {(mode is PersistenceMode.Rdb or PersistenceMode.RdbAndAof ? "900 1" : "\"\"")}\nappendfsync always\naof-use-rdb-preamble yes\nrequirepass \"{password}\"\n", new UTF8Encoding(false));
        if (strictAof) File.AppendAllText(config, "aof-load-truncated no\n");
        Installation = ConfigurationReader.Read(Path.Combine(RedisDirectory, "redis-server.exe"), config, "5.0.14.1");
    }
    public RepairService Repair(IBackupStore? backup = null, IProcessExecutor? executor = null, IRedisPlatform? platform = null) => new(platform ?? this, executor ?? Executor, backup ?? new BackupStore(), new OperationStore(Path.Combine(DirectoryPath, "backups")));
    public async Task<RepairResult> RunAsync(bool accept, IBackupStore? backup = null, IProcessExecutor? executor = null, IRedisPlatform? platform = null)
    {
        return await Repair(backup, executor, platform).RepairAsync(Progress, request =>
        {
            Confirmations++; Assert.True(File.Exists(Path.Combine(request.BackupDirectory, "manifest.json")), "Prompt before verified backup");
            return Task.FromResult(accept);
        });
    }
    public Task<RedisInstallation> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(Installation);
    public Task<ServiceSnapshot> GetServiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(_process is not null && !_process.HasExited ? new ServiceSnapshot(ServiceState.Running, _process.Id) : new ServiceSnapshot(ServiceState.Stopped));
    public async Task EnsureStoppedAsync(CancellationToken cancellationToken = default) => Assert.Equal(ServiceState.Stopped, (await GetServiceAsync(cancellationToken)).State);
    public async Task VerifyHealthyAsync(RedisInstallation installation, CancellationToken cancellationToken = default) => await RedisConnection.VerifyAsync(installation, (await GetServiceAsync()).ProcessId, cancellationToken);
    public async Task StartAndVerifyAsync(RedisInstallation installation, CancellationToken cancellationToken = default)
    {
        await EnsureStoppedAsync(cancellationToken); Starts++; _process?.Dispose();
        var info = new ProcessStartInfo(installation.ServerPath) { WorkingDirectory = DirectoryPath, UseShellExecute = false, CreateNoWindow = true }; info.ArgumentList.Add(installation.ConfigPath);
        _process = Process.Start(info) ?? throw new Exception("Test server did not start");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            if (_process.HasExited) throw new RepairException("隔离 Redis 启动退出。");
            try { await VerifyHealthyAsync(installation, deadline.Token); return; }
            catch (Exception error) when (error is SocketException or IOException or RedisLoadingException || error is OperationCanceledException && !deadline.IsCancellationRequested) { }
            await Task.Delay(100, deadline.Token);
        }
    }
    public async Task<string> CommandAsync(params string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); using var client = new TcpClient(); await client.ConnectAsync("127.0.0.1", _port, deadline.Token);
        await using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
        if (_password.Length > 0) await RedisConnection.CommandAsync(stream, reader, ["AUTH", _password], deadline.Token);
        return await RedisConnection.CommandAsync(stream, reader, args, deadline.Token);
    }
    public async Task SeedRdbAsync()
    {
        await StartAndVerifyAsync(Installation); Assert.Equal("OK", await CommandAsync("SET", "material", "当前料")); Assert.Equal("OK", await CommandAsync("SAVE"));
        await StopAsync(); Starts = 0;
    }
    public async Task StopAsync()
    {
        if (_process is null || _process.HasExited) return;
        using var quiesce = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var info = await CommandAsync("INFO", "persistence");
            if (info.Contains("rdb_bgsave_in_progress:0", StringComparison.Ordinal) && info.Contains("aof_rewrite_in_progress:0", StringComparison.Ordinal) && info.Contains("aof_rewrite_scheduled:0", StringComparison.Ordinal)) break;
            await Task.Delay(100, quiesce.Token);
        }
        try { await CommandAsync("SHUTDOWN", "NOSAVE"); }
        catch (IOException) { /* Redis 执行 SHUTDOWN 时会主动关闭连接。 */ }
        catch (RepairException error) when (error.Message.Contains("提前关闭", StringComparison.Ordinal)) { }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            // 清理时仅允许终止本用例创建的子进程，不能操作已有 Redis 服务。
            _process.Kill(true); await _process.WaitForExitAsync();
        }
        using var release = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var path in new[] { Installation.RdbPath, Installation.AofPath, Installation.LogPath! })
        {
            while (File.Exists(path))
            {
                try { using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); break; }
                catch (IOException) { await Task.Delay(100, release.Token); }
            }
        }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); _process?.Dispose(); }
    public static string Resp(params string[] args)
    {
        var builder = new StringBuilder("*" + args.Length + "\r\n");
        foreach (var arg in args) builder.Append('$').Append(Encoding.UTF8.GetByteCount(arg)).Append("\r\n").Append(arg).Append("\r\n");
        return builder.ToString();
    }
}
internal sealed class FailingBackup : IBackupStore
{
    public Task<BackupSnapshot> CreateAsync(RedisInstallation installation, string operationDirectory) => throw new IOException("模拟磁盘空间不足");
    public Task VerifyUnchangedAsync(BackupSnapshot snapshot, IEnumerable<string> paths) => throw new Exception("Must not run");
}
internal sealed class ChangingBackup : IBackupStore
{
    private readonly BackupStore _inner = new();
    public async Task<BackupSnapshot> CreateAsync(RedisInstallation installation, string operationDirectory)
    { var result = await _inner.CreateAsync(installation, operationDirectory); await File.AppendAllTextAsync(installation.RdbPath, "changed"); return result; }
    public Task VerifyUnchangedAsync(BackupSnapshot snapshot, IEnumerable<string> paths) => _inner.VerifyUnchangedAsync(snapshot, paths);
}
internal sealed class FakeExecutor(Func<IReadOnlyList<string>, ProcessResult> result) : IProcessExecutor
{
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? input = null, Action<string>? output = null, CancellationToken cancellationToken = default)
    { var value = result(arguments); output?.Invoke(value.Output); return Task.FromResult(value); }
}
internal sealed class FailedPlatform(RedisInstallation installation, string newLog, bool running = false) : IRedisPlatform
{
    public int Starts { get; private set; }
    public Task<RedisInstallation> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(installation);
    public Task<ServiceSnapshot> GetServiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ServiceSnapshot(running ? ServiceState.Running : ServiceState.Stopped, 99999));
    public Task EnsureStoppedAsync(CancellationToken cancellationToken = default) => running ? throw new RepairException("服务仍在运行") : Task.CompletedTask;
    public Task VerifyHealthyAsync(RedisInstallation installation, CancellationToken cancellationToken = default) => throw new RepairException("身份或认证验证失败");
    public async Task StartAndVerifyAsync(RedisInstallation installation, CancellationToken cancellationToken = default)
    { Starts++; if (installation.LogPath is not null) await File.AppendAllTextAsync(installation.LogPath, newLog); throw new RepairException("启动失败"); }
}
internal sealed class ChangedAfterApply(Fixture fixture) : IRedisPlatform
{
    public Task<RedisInstallation> DiscoverAsync(CancellationToken cancellationToken = default) => fixture.DiscoverAsync(cancellationToken);
    public Task<ServiceSnapshot> GetServiceAsync(CancellationToken cancellationToken = default) => fixture.GetServiceAsync(cancellationToken);
    public Task VerifyHealthyAsync(RedisInstallation installation, CancellationToken cancellationToken = default) => fixture.VerifyHealthyAsync(installation, cancellationToken);
    public Task StartAndVerifyAsync(RedisInstallation installation, CancellationToken cancellationToken = default) => fixture.StartAndVerifyAsync(installation, cancellationToken);
    public async Task EnsureStoppedAsync(CancellationToken cancellationToken = default)
    {
        await fixture.EnsureStoppedAsync(cancellationToken);
        if (Directory.GetFiles(fixture.DirectoryPath, "appendonly.aof.fixredis-quarantine-*").Length > 0)
            await File.AppendAllTextAsync(fixture.Installation.AofPath, Fixture.Resp("SET", "external", "new"));
    }
}
