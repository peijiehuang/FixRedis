using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FixRedis.Core;

/// <summary>解析支持范围内的 Redis 配置；无法可靠解释的指令直接拒绝，避免猜错数据路径。</summary>
public static class ConfigurationReader
{
    public static RedisInstallation Read(string serverPath, string configPath, string version)
    {
        serverPath = Path.GetFullPath(serverPath);
        configPath = Path.GetFullPath(configPath);
        var bytes = File.ReadAllBytes(configPath);
        var config = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        string dir = ".", rdb = "dump.rdb", aof = "appendonly.aof", log = "", password = "", host = "127.0.0.1";
        bool appendOnly = false, saveEnabled = true;
        int port = 6379;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var args = Tokenize(line);
            var directive = args[0].ToLowerInvariant();
            if (directive is "include" or "slaveof" or "replicaof" or "loadmodule" or "sentinel")
                throw new RepairException($"第一版不支持配置指令 {directive}，停止操作。");
            if (directive == "cluster-enabled" && !Value(args).Equals("no", StringComparison.OrdinalIgnoreCase))
                throw new RepairException("不支持 Redis 集群实例。");
            if (directive == "rename-command")
                throw new RepairException("第一版不支持命令重命名配置，无法可靠验证实例。");
            switch (directive)
            {
                case "dir": dir = Value(args); break;
                case "dbfilename": rdb = Value(args); break;
                case "appendfilename": aof = Value(args); break;
                case "logfile": log = Value(args); break;
                case "requirepass": password = Value(args); break;
                case "appendonly": appendOnly = Boolean(args); break;
                case "save":
                    if (args.Count == 2 && args[1] == "") saveEnabled = false;
                    else if (args.Count == 3 && int.TryParse(args[1], out var seconds) && seconds > 0 && int.TryParse(args[2], out var changes) && changes >= 0) saveEnabled = true;
                    else throw new RepairException("无法解析 save 配置。");
                    break;
                case "port":
                    if (!int.TryParse(Value(args), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
                        throw new RepairException("仅支持有效 TCP 端口的 Redis 实例。");
                    break;
                case "bind":
                    if (!args.Skip(1).Any(value => value == "127.0.0.1"))
                        throw new RepairException("第一版要求 Redis 绑定 127.0.0.1。");
                    host = "127.0.0.1";
                    break;
            }
        }
        ValidateFileName(rdb); ValidateFileName(aof);
        // 此 Windows Redis 版本将服务工作目录切换到程序目录，相对路径必须以它为基准。
        var workingDirectory = Path.GetDirectoryName(serverPath)!;
        var dataDirectory = Path.GetFullPath(dir, workingDirectory);
        var rdbPath = Path.Combine(dataDirectory, rdb);
        var aofPath = Path.Combine(dataDirectory, aof);
        if (string.Equals(rdbPath, aofPath, StringComparison.OrdinalIgnoreCase))
            throw new RepairException("RDB 与 AOF 文件路径不能相同。");
        var logPath = log.Length == 0 ? null : Path.GetFullPath(log, workingDirectory);
        if (new[] { rdbPath, aofPath }.Any(path => string.Equals(path, configPath, StringComparison.OrdinalIgnoreCase) || string.Equals(path, serverPath, StringComparison.OrdinalIgnoreCase) || string.Equals(path, logPath, StringComparison.OrdinalIgnoreCase)))
            throw new RepairException("数据文件与配置、程序或日志路径重叠，停止操作。");
        return new RedisInstallation
        {
            ServerPath = serverPath, ConfigPath = configPath, ConfigHash = Convert.ToHexString(SHA256.HashData(bytes)),
            DataDirectory = dataDirectory, RdbPath = rdbPath, AofPath = aofPath, LogPath = logPath,
            Password = password, Host = host, Port = port, Version = version,
            Mode = appendOnly ? saveEnabled ? PersistenceMode.RdbAndAof : PersistenceMode.Aof : saveEnabled ? PersistenceMode.Rdb : PersistenceMode.None
        };
    }

    private static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(name) != name)
            throw new RepairException("持久化文件名必须是数据目录内的普通文件名。");
    }
    private static string Value(IReadOnlyList<string> args) => args.Count == 2 ? args[1] : throw new RepairException($"无法解析 {args[0]} 配置。");
    private static bool Boolean(IReadOnlyList<string> args) => Value(args).ToLowerInvariant() switch
    { "yes" => true, "no" => false, _ => throw new RepairException($"无法解析 {args[0]} 配置值。") };

    public static IReadOnlyList<string> Tokenize(string line)
    {
        var result = new List<string>();
        int index = 0;
        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index])) index++;
            if (index == line.Length) break;
            var token = new StringBuilder();
            char quote = '\0';
            bool closed = false;
            while (index < line.Length)
            {
                var ch = line[index++];
                if (quote == '\0' && char.IsWhiteSpace(ch)) break;
                if (quote == '\0' && (ch == '"' || ch == '\'')) { quote = ch; continue; }
                if (quote != '\0' && ch == quote)
                {
                    if (index < line.Length && !char.IsWhiteSpace(line[index])) throw new RepairException("配置中的引号后有非法字符。");
                    closed = true; quote = '\0'; break;
                }
                if (quote == '"' && ch == '\\' && index < line.Length)
                {
                    var next = line[index++];
                    if (next == 'x' && index + 1 < line.Length && byte.TryParse(line.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                    { token.Append((char)hex); index += 2; }
                    else token.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'a' => '\a', _ => next });
                }
                else if (quote == '\'' && ch == '\\' && index < line.Length && line[index] == '\'') { token.Append('\''); index++; }
                else token.Append(ch);
            }
            if (quote != '\0' && !closed) throw new RepairException("配置中存在未闭合的引号。");
            result.Add(token.ToString());
        }
        return result;
    }
}
