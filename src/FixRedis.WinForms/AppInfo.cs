using System.Reflection;

namespace FixRedis.WinForms;

// 版本及项目地址从程序集元数据读取，统一由 Directory.Build.props 维护。
internal static class AppInfo
{
    public static string DisplayVersion { get; } = "V" + typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public static string WindowTitle => $"FixRedis {DisplayVersion} · Redis 修复工具";
    public static string RepositoryUrl { get; } = typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "RepositoryUrl").Value!;
}
