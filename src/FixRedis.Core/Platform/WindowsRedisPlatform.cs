using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FixRedis.Core;

/// <summary>发现并控制已安装的 Windows 服务 Redis，不安装服务、不强杀 Redis 进程。</summary>
public sealed class WindowsRedisPlatform(IProcessExecutor executor) : IRedisPlatform
{
    public async Task<RedisInstallation> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Redis")
            ?? throw new RepairException("未找到 Windows 服务 Redis。工具不会安装或修改服务。");
        var image = key.GetValue("ImagePath") as string ?? throw new RepairException("Redis 服务没有有效启动参数。");
        var args = SplitCommandLine(Environment.ExpandEnvironmentVariables(image));
        if (args.Length != 3 || !args[1].Equals("--service-run", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[2]))
            throw new RepairException("仅支持 redis-server.exe --service-run 配置文件 的明确绝对路径启动方式。");
        if (!Path.GetFileName(args[0]).Equals("redis-server.exe", StringComparison.OrdinalIgnoreCase)) throw new RepairException("Redis 服务程序名不符合支持范围。");
        var versionOutput = await executor.RunAsync(args[0], ["--version"], cancellationToken: cancellationToken);
        var version = Regex.Match(versionOutput.Output, @"\bv=(5\.0\.\d+(?:\.\d+)?)\b");
        if (versionOutput.ExitCode != 0 || !version.Success) throw new RepairException("仅支持 Windows Redis 5.0，无法识别当前版本。");
        return ConfigurationReader.Read(args[0], args[2], version.Groups[1].Value);
    }

    public Task<ServiceSnapshot> GetServiceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var manager = Native.OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法查询 Windows 服务管理器。");
        using var service = Native.OpenService(manager, "Redis", 4);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法查询 Redis 服务。");
        if (!Native.QueryServiceStatusEx(service, 0, out var state, Marshal.SizeOf<Native.ServiceStatus>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Redis 服务状态。");
        return Task.FromResult(new ServiceSnapshot(state.CurrentState switch
        { 1 => ServiceState.Stopped, 2 => ServiceState.StartPending, 4 => ServiceState.Running, _ => ServiceState.Other }, checked((int)state.ProcessId)));
    }

    public async Task EnsureStoppedAsync(CancellationToken cancellationToken = default)
    {
        if ((await GetServiceAsync(cancellationToken)).State != ServiceState.Stopped) throw new RepairException("Redis 服务尚未完全停止，拒绝操作数据文件。");
        var processes = Process.GetProcessesByName("redis-server");
        try
        {
            if (processes.Any(process => !process.HasExited))
                throw new RepairException("检测到其他 redis-server 进程，无法排除文件占用；请先处理该进程。工具不会强杀进程。");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public async Task VerifyHealthyAsync(RedisInstallation installation, CancellationToken cancellationToken = default)
    {
        var service = await GetServiceAsync(cancellationToken);
        if (service.State != ServiceState.Running) throw new RepairException("目标服务没有处于 Running 状态。");
        await RedisConnection.VerifyAsync(installation, service.ProcessId, cancellationToken);
    }

    public async Task StartAndVerifyAsync(RedisInstallation installation, CancellationToken cancellationToken = default)
    {
        await EnsureStoppedAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            // SCM 的启动调用可能阻塞，必须放到后台以保持界面响应。
            await Task.Run(() =>
            {
                using var manager = Native.OpenSCManager(null, null, 1);
                if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接服务管理器。");
                using var service = Native.OpenService(manager, "Redis", 4 | 16);
                if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开 Redis 服务，请确认管理员权限。");
                if (!Native.StartService(service, 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Redis 服务启动请求失败。");
            }, deadline.Token).WaitAsync(deadline.Token);
            while (true)
            {
                var state = await GetServiceAsync(deadline.Token);
                if (state.State == ServiceState.Stopped) throw new RepairException("Redis 服务启动后退出，请查看本次启动日志。");
                if (state.State == ServiceState.Running)
                {
                    try { await RedisConnection.VerifyAsync(installation, state.ProcessId, deadline.Token); return; }
                    catch (Exception error) when (error is SocketException or IOException or RedisLoadingException || error is OperationCanceledException && !deadline.IsCancellationRequested) { }
                }
                await Task.Delay(300, deadline.Token);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new RepairException("60 秒内未完成服务启动及健康验证；不会清空数据或继续重试。", error); }
    }

    private static string[] SplitCommandLine(string command)
    {
        var pointer = Native.CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))!).ToArray(); }
        finally { Native.LocalFree(pointer); }
    }

    private sealed class ServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => Native.CloseServiceHandle(handle);
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct ServiceStatus
        {
            public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, int size, out int needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool StartService(ServiceHandle service, int count, IntPtr arguments);
        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CommandLineToArgvW(string command, out int count);
        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr handle);
    }
}
