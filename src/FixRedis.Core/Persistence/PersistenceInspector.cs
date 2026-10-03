using System.Globalization;
using System.Text.RegularExpressions;

namespace FixRedis.Core;

/// <summary>解释官方 RDB/AOF 检查结果，只将明确的数据损坏与环境故障区分开。</summary>
public sealed class PersistenceInspector(IProcessExecutor executor)
{
    public async Task<FileCheck> CheckAsync(RedisInstallation installation, string path, Action<string> output)
    {
        if (installation.UsesAof && new FileInfo(path).Length == 0)
            return new FileCheck(CheckStatus.Valid, "空 AOF 是 Redis 5 支持的有效空库文件。", 0);
        var tool = installation.UsesAof ? installation.AofChecker : installation.RdbChecker;
        output("执行 " + Path.GetFileName(tool) + " 检查工作副本。");
        var result = await executor.RunAsync(tool, [path], output: output);
        output("检查工具退出码：" + result.ExitCode);
        return Classify(result, installation.UsesAof);
    }

    public async Task<FileCheck> FixAofAsync(RedisInstallation installation, string path, Action<string> output)
    {
        var result = await executor.RunAsync(installation.AofChecker, ["--fix", path], "y\n", output);
        output("AOF 修复工具退出码：" + result.ExitCode);
        if (result.ExitCode != 0) return Classify(result, true);
        return await CheckAsync(installation, path, output);
    }

    public static FileCheck Classify(ProcessResult result, bool aof)
    {
        var text = result.Output;
        if (aof)
        {
            var validMessage = text.Split('\n').Any(line => line.TrimEnd('\r') == "AOF is valid");
            // 截断位置、文件大小和差值必须一致，不能应用矛盾或溢出的检查结果。
            var matches = Regex.Matches(text, @"(?m)^AOF analyzed: size=(\d+), ok_up_to=(\d+), diff=(\d+)\r?$");
            if (matches.Count > 1 || matches.Count == 0 && text.Contains("AOF analyzed:", StringComparison.Ordinal)) return Unreliable();
            if (matches.Count == 1)
            {
                var match = matches[0];
                if (!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
                    !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var valid) ||
                    !long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var diff) ||
                    valid > size || diff != size - valid) return Unreliable();
                if (diff > 0)
                    return result.ExitCode == 0 || validMessage ? Unreliable() : new FileCheck(CheckStatus.Corrupt, $"AOF 有 {diff} 字节位于最后有效位置之后。", valid);
            }
            if (result.ExitCode == 0 && validMessage) return new FileCheck(CheckStatus.Valid, "检查通过。");
            if (text.Contains("RDB preamble of AOF file is not sane", StringComparison.Ordinal) && IsRdbCorruption(text))
                return new FileCheck(CheckStatus.Corrupt, "AOF 的 RDB 前导区损坏，无法通过截断修复。");
        }
        else
        {
            if (result.ExitCode == 0 && text.Contains("RDB looks OK", StringComparison.Ordinal)) return new FileCheck(CheckStatus.Valid, "检查通过。");
            if (IsRdbCorruption(text)) return new FileCheck(CheckStatus.Corrupt, "RDB 文件损坏；官方检查工具没有 RDB 修复功能。");
        }
        return Unreliable();
    }

    private static FileCheck Unreliable() => new(CheckStatus.OperationalFailure, "检查工具未能可靠判断文件损坏，停止操作（检查退出码、权限、工具版本或输出）。");

    private static bool IsRdbCorruption(string text) => new[]
    {
        "Wrong signature trying to load DB from file", "Unexpected EOF reading RDB file", "RDB CRC error", "Invalid object type:"
    }.Any(marker => text.Contains(marker, StringComparison.Ordinal));

    public static bool StartupProvesCorruption(string newLog)
    {
        // 环境错误优先；损坏标记必须位于同一行，不能拼接无关日志触发清空。
        if (new[] { "Permission denied", "Access is denied", "No space left", "Out Of Memory", "OOM", "bind:" }
            .Any(marker => newLog.Contains(marker, StringComparison.OrdinalIgnoreCase))) return false;
        return newLog.Split('\n').Any(line =>
            new[] { "Wrong signature", "Wrong RDB checksum", "RDB CRC error", "Unexpected EOF reading RDB", "Bad file format reading the append only file", "Unexpected end of file reading the append only file", "Error reading the RDB preamble of the AOF file", "Unknown command" }
                .Any(marker => line.Contains(marker, StringComparison.Ordinal)) &&
            (line.Contains("RDB", StringComparison.OrdinalIgnoreCase) || line.Contains("append only", StringComparison.OrdinalIgnoreCase)));
    }
}
