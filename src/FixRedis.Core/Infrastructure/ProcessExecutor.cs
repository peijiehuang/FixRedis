using System.Diagnostics;
using System.Text;

namespace FixRedis.Core;

/// <summary>以参数列表调用官方检查工具，同时读取输出，处理确认输入和超时。</summary>
public sealed class ProcessExecutor(TimeSpan? timeout = null) : IProcessExecutor
{
    private const int MaximumCapturedCharacters = 4 * 1024 * 1024;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(5);
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string? input = null, Action<string>? output = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new RepairException("无法启动检查工具。");
        var captured = new StringBuilder();
        var gate = new object();
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                lock (gate)
                {
                    if (captured.Length + line.Length > MaximumCapturedCharacters) throw new RepairException("检查工具输出超过限制，停止操作。");
                    captured.AppendLine(line);
                    output?.Invoke(line);
                }
            }
        }
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);
        var reads = Task.WhenAll(stdout, stderr);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), deadline.Token);
            process.StandardInput.Close();
            // 及时处理输出读取失败，避免子进程因管道写满而一直阻塞。
            var exit = process.WaitForExitAsync(deadline.Token);
            var pending = new List<Task> { stdout, stderr, exit };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            return new ProcessResult(process.ExitCode, captured.ToString());
        }
        catch (Exception error)
        {
            deadline.Cancel();
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await reads; } catch (Exception readError) when (readError is OperationCanceledException or IOException or RepairException) { /* 等待管道任务结束，但保留最初的失败原因。 */ }
            if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new RepairException("检查工具执行超时，未应用修复结果。", error);
            throw;
        }
    }
}
