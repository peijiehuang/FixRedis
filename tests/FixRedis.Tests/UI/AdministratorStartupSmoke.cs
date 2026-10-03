using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace FixRedis.Tests;

internal static class AdministratorStartupSmoke
{
    public static async Task RunAsync(string outputDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        // 使用 DLL 宿主绕过 EXE 清单，验证真实进程令牌是否被启动入口正确拦截。
        var start = new ProcessStartInfo("dotnet.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FixRedis.WinForms.dll"));
        using var process = Process.Start(start) ?? throw new Exception("无法启动实际修复程序。");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var version = typeof(FixRedis.WinForms.MainForm).Assembly.GetName().Version!.ToString(3);
            string mainTitle = $"FixRedis V{version} · Redis 修复工具";
            string expectedTitle = elevated ? mainTitle : "FixRedis — 需要管理员权限";
            IntPtr window = IntPtr.Zero;
            while (window == IntPtr.Zero)
            {
                Assert.True(!process.HasExited, "程序在显示预期窗口前退出。");
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out var pid);
                    if (pid != process.Id) return true;
                    var title = Text(handle);
                    if (!elevated) Assert.True(title != mainTitle, "非管理员进程打开了主窗口。");
                    if (title == expectedTitle) window = handle;
                    return true;
                }, IntPtr.Zero);
                if (window == IntPtr.Zero) await Task.Delay(100, timeout.Token);
            }
            if (!elevated)
            {
                var contents = new StringBuilder();
                EnumChildWindows(window, (handle, _) => { contents.Append(Text(handle)); return true; }, IntPtr.Zero);
                Assert.True(contents.ToString().Contains("以管理员身份运行", StringComparison.Ordinal));
                Assert.True(contents.ToString().Contains("程序将退出", StringComparison.Ordinal));
            }
            else
            {
                // 初始检测期间主窗口禁止关闭，等待只读检测完成后再退出。
                while (true)
                {
                    var contents = new StringBuilder();
                    EnumChildWindows(window, (handle, _) => { contents.Append(Text(handle)); return true; }, IntPtr.Zero);
                    Assert.True(!contents.ToString().Contains("无法完成检测", StringComparison.Ordinal), "管理员启动后的只读检测失败。");
                    if (contents.ToString().Contains("等待修复。原始数据", StringComparison.Ordinal)) break;
                    await Task.Delay(100, timeout.Token);
                }
                Assert.True(GetWindowRect(window, out var bounds));
                using var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var dc = graphics.GetHdc();
                    try { Assert.True(PrintWindow(window, dc, 2), "实际程序窗口截图失败。"); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                bitmap.Save(Path.Combine(outputDirectory, "actual-application.png"));
            }
            Assert.True(PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero)); // WM_CLOSE：只关闭窗口，不点击修复。
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(elevated ? 0 : 1, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    private static string Text(IntPtr window)
    {
        var text = new StringBuilder(2048); GetWindowText(window, text, text.Capacity); return text.ToString();
    }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect bounds);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
