using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FixRedis.Tests;

internal static class PrivateDesktop
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        // 独立 Win32 桌面只承载测试进程，不调用 SwitchDesktop 干扰当前桌面。
        var desktopName = "FixRedisTests-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(desktopName, null, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建独立测试桌面。");
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "FixRedis.Tests.exe");
            var command = new StringBuilder("\"" + executable + "\" " + string.Join(" ", arguments.Select(Quote)));
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\" + desktopName };
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, Environment.CurrentDirectory, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在独立测试桌面启动界面烟测。");
            try
            {
                using var child = Process.GetProcessById(process.ProcessId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try { await child.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { child.Kill(true); await child.WaitForExitAsync(); throw new TimeoutException("独立测试桌面的验收超时。"); }
                Console.WriteLine($"独立测试桌面验收结束，退出码 {child.ExitCode}；当前桌面未切换。截图在 artifacts/tests 最新目录。");
                return child.ExitCode;
            }
            finally { CloseHandle(process.Process); CloseHandle(process.Thread); }
        }
        finally { CloseDesktop(desktop); }
    }
    private static string Quote(string argument)
    {
        if (argument.Contains('"') || argument.EndsWith('\\')) throw new ArgumentException("测试参数包含不支持的引号或尾随反斜杠。");
        return "\"" + argument + "\"";
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedBytes; public IntPtr ReservedBuffer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string desktop, string? device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string executable, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
