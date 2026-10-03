using FixRedis.Core;
using System.Security;
using System.Security.Principal;

namespace FixRedis.WinForms;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // 即使通过 DLL 宿主绕过 EXE 提权清单，也不能进入无管理员权限的修复流程。
        if (!CheckAdministrator())
        {
            Environment.ExitCode = 1;
            return;
        }
        using var lease = SingleInstanceLease.TryAcquire();
        if (lease is null)
        {
            MessageBox.Show("Redis 修复工具已在运行，请使用已打开的窗口。", "FixRedis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var executor = new ProcessExecutor();
        var service = new RepairService(new WindowsRedisPlatform(executor), executor, new BackupStore(), new OperationStore());
        Application.Run(new MainForm(service));
    }

    private static bool CheckAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
            MessageBox.Show("当前程序未以管理员身份运行。\n\n请右键程序，选择“以管理员身份运行”，并允许 Windows 提权提示。\n\n程序将退出，尚未执行 Redis 检测或修复。",
                "FixRedis — 需要管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException)
        {
            MessageBox.Show("无法确认当前程序的管理员权限，请以管理员身份重新打开。\n\n程序将退出，尚未执行 Redis 检测或修复。",
                "FixRedis — 权限检测失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return false;
    }
}
