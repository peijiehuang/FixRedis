using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace FixRedis.Core;

/// <summary>创建独立操作目录并限制访问权限，保存修复日志；不会自动清理历史备份。</summary>
public sealed class OperationStore(string? root = null) : IOperationStore
{
    public static string DefaultRootPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FixRedis", "Backups");
    private readonly string _root = root ?? DefaultRootPath;
    public string CreateDirectory()
    {
        Directory.CreateDirectory(_root);
        Secure(_root);
        var directory = Path.Combine(_root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Secure(directory);
        return directory;
    }
    public void AppendLog(string directory, string line) => File.AppendAllText(Path.Combine(directory, "operation.log"), line + Environment.NewLine, new UTF8Encoding(false));
    private static void Secure(string path)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var rights = FileSystemRights.FullControl;
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using var identity = WindowsIdentity.GetCurrent();
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), identity.User! })
            acl.AddAccessRule(new FileSystemAccessRule(sid, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
}

/// <summary>保存原始文件和校验清单；修复前后都通过哈希确认源文件未被外部改动。</summary>
public sealed class BackupStore : IBackupStore
{
    public async Task<BackupSnapshot> CreateAsync(RedisInstallation installation, string operationDirectory)
    {
        var directory = Path.Combine(operationDirectory, "originals");
        Directory.CreateDirectory(directory);
        var paths = new List<string> { installation.ConfigPath, installation.RdbPath, installation.AofPath };
        if (installation.LogPath is not null) paths.Add(installation.LogPath);
        var entries = new List<BackupEntry>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            FileStream source;
            try { source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true); }
            catch (FileNotFoundException) { entries.Add(new BackupEntry(path, null, true, 0, null)); continue; }
            catch (DirectoryNotFoundException) { entries.Add(new BackupEntry(path, null, true, 0, null)); continue; }
            await using (source)
            {
                var copy = Path.Combine(directory, $"{entries.Count:D2}-" + Path.GetFileName(path));
                await using (var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                { await source.CopyToAsync(target); target.Flush(true); }
                // 对原文件和副本分别计算哈希，复制完成不等于备份已验证。
                source.Position = 0;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
                await using var verification = File.OpenRead(copy);
                var copiedHash = Convert.ToHexString(await SHA256.HashDataAsync(verification));
                if (verification.Length != source.Length || hash != copiedHash) throw new RepairException("备份副本校验不一致，停止修复。");
                entries.Add(new BackupEntry(path, copy, false, source.Length, hash));
            }
        }
        var config = entries.Single(entry => entry.OriginalPath.Equals(installation.ConfigPath, StringComparison.OrdinalIgnoreCase));
        if (config.Missing || config.Sha256 != installation.ConfigHash) throw new RepairException("配置文件在检测后发生变化或已丢失，停止修复。");
        var snapshot = new BackupSnapshot(operationDirectory, entries);
        await File.WriteAllTextAsync(Path.Combine(operationDirectory, "manifest.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return snapshot;
    }

    public async Task VerifyUnchangedAsync(BackupSnapshot snapshot, IEnumerable<string> paths)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = snapshot.Files.Single(item => item.OriginalPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            try
            {
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                if (entry.Missing || source.Length != entry.Size || Convert.ToHexString(await SHA256.HashDataAsync(source)) != entry.Sha256)
                    throw new RepairException("原文件在备份后发生变化，停止操作：" + path);
            }
            catch (FileNotFoundException) { if (!entry.Missing) throw new RepairException("原文件在备份后丢失：" + path); }
            catch (DirectoryNotFoundException) { if (!entry.Missing) throw new RepairException("原文件目录在备份后丢失：" + path); }
        }
    }
}
