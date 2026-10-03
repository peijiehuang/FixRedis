using System.Net.Sockets;
using System.Text;

namespace FixRedis.Core;

/// <summary>通过 RESP 执行认证与只读健康核验，避免将其他端口响应误判为目标服务。</summary>
public static class RedisConnection
{
    public static async Task VerifyAsync(RedisInstallation installation, int expectedProcessId, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync(installation.Host, installation.Port, deadline.Token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, true);
        if (installation.Password.Length > 0 && await CommandAsync(stream, reader, ["AUTH", installation.Password], deadline.Token) != "OK")
            throw new RepairException("Redis 认证未通过。");
        if (await CommandAsync(stream, reader, ["PING"], deadline.Token) != "PONG") throw new RepairException("Redis PING 验证失败。");
        var server = ParseInfo(await CommandAsync(stream, reader, ["INFO", "server"], deadline.Token));
        var persistence = ParseInfo(await CommandAsync(stream, reader, ["INFO", "persistence"], deadline.Token));
        var replication = ParseInfo(await CommandAsync(stream, reader, ["INFO", "replication"], deadline.Token));
        if (!server.TryGetValue("redis_mode", out var mode) || mode != "standalone" || !replication.TryGetValue("role", out var role) || role != "master")
            throw new RepairException("运行时实例不是独立主节点，第一版不支持集群、Sentinel 或副本。");
        if (expectedProcessId <= 0 || !server.TryGetValue("process_id", out var pid) || pid != expectedProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new RepairException("端口响应不属于目标 Redis 服务进程。");
        if (!server.TryGetValue("config_file", out var config) || !Path.GetFullPath(config).Equals(installation.ConfigPath, StringComparison.OrdinalIgnoreCase))
            throw new RepairException("响应实例加载的配置文件不匹配。");
        if (!server.TryGetValue("redis_version", out var version) || !version.StartsWith("5.0.", StringComparison.Ordinal))
            throw new RepairException("响应实例不是支持的 Redis 5.0。");
        if (!persistence.TryGetValue("loading", out var loading) || loading != "0") throw new RepairException("Redis 数据仍在加载。");
        if (!persistence.TryGetValue("aof_enabled", out var aof) || aof != (installation.UsesAof ? "1" : "0"))
            throw new RepairException("实例运行时持久化方式与配置不一致。");
        var actualDirectory = await CommandAsync(stream, reader, ["CONFIG", "GET", "dir"], deadline.Token);
        var actualRdb = await CommandAsync(stream, reader, ["CONFIG", "GET", "dbfilename"], deadline.Token);
        if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualDirectory)).Equals(Path.TrimEndingDirectorySeparator(installation.DataDirectory), StringComparison.OrdinalIgnoreCase) ||
            actualRdb != Path.GetFileName(installation.RdbPath))
            throw new RepairException("实例实际数据目录或文件名与解析配置不一致。");
    }

    private static Dictionary<string, string> ParseInfo(string info) => info.Split('\n')
        .Select(line => line.TrimEnd('\r')).Where(line => line.Contains(':'))
        .Select(line => line.Split(':', 2)).ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

    internal static async Task<string> CommandAsync(Stream stream, StreamReader reader, IReadOnlyList<string> args, CancellationToken token)
    {
        var builder = new StringBuilder("*" + args.Count + "\r\n");
        foreach (var arg in args) builder.Append('$').Append(Encoding.UTF8.GetByteCount(arg)).Append("\r\n").Append(arg).Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(builder.ToString()), token);
        var reply = await ReadReplyAsync(reader, token);
        return reply;
    }

    private static async Task<string> ReadReplyAsync(StreamReader reader, CancellationToken token)
    {
        var line = await reader.ReadLineAsync(token) ?? throw new RepairException("Redis 连接提前关闭。");
        if (line.StartsWith('-'))
        {
            // 服务端错误可能带有认证参数，不将原始错误文本直接输出到日志。
            if (line.Contains("LOADING", StringComparison.Ordinal)) throw new RedisLoadingException();
            if (line.Contains("NOAUTH", StringComparison.Ordinal) || line.Contains("WRONGPASS", StringComparison.Ordinal) || line.Contains("invalid password", StringComparison.OrdinalIgnoreCase))
                throw new RepairException("Redis 认证失败，请检查配置中的 requirepass。");
            throw new RepairException("Redis 命令被拒绝，无法完成健康检查。");
        }
        if (line.StartsWith('+') || line.StartsWith(':')) return line[1..];
        if (line == "*2") { await ReadReplyAsync(reader, token); return await ReadReplyAsync(reader, token); }
        if (!line.StartsWith('$') || !int.TryParse(line.AsSpan(1), out var bytes) || bytes is < 0 or > 1024 * 1024)
            throw new RepairException("Redis 返回了不支持的协议响应。");
        // RESP 长度按 UTF-8 字节计算，不能把中文或代理项当成单字节字符。
        var builder = new StringBuilder();
        int remaining = bytes;
        char[] character = new char[1];
        while (remaining > 0)
        {
            if (await reader.ReadAsync(character.AsMemory(), token) == 0) throw new RepairException("Redis 响应不完整。");
            builder.Append(character[0]);
            if (!char.IsHighSurrogate(character[0])) remaining -= Encoding.UTF8.GetByteCount(builder.ToString(builder.Length - (char.IsLowSurrogate(character[0]) ? 2 : 1), char.IsLowSurrogate(character[0]) ? 2 : 1));
        }
        if (remaining != 0 || await reader.ReadLineAsync(token) != "") throw new RepairException("Redis 协议长度校验失败。");
        return builder.ToString();
    }
}

internal sealed class RedisLoadingException() : Exception("Redis 数据仍在加载。");
