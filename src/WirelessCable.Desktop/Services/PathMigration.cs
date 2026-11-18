using System.IO;
using Serilog;

namespace WirelessCable.Desktop.Services;

/// <summary>
/// 改数据目录后的首次启动迁移。
///
/// 只搬"不给就起不来"的东西：数据库（含配对关系）与证书（换了指纹手机就得重新配对）。
/// 接收目录里的文件不搬——那里可能几十 GB，启动时拷一遍既慢又容易失败，
/// 旧文件留在原处用户看得见，比悄悄搬走更安全。
/// </summary>
public static class PathMigration
{
    private static readonly string[] CopiedDirectories = ["data", "certs"];

    /// <summary>
    /// 便携版首次启动：把安装版（%LOCALAPPDATA%\WirelessCable）的库与证书搬进来。
    ///
    /// 为什么需要：交付形态改成便携文件夹后，用户第一次跑便携版时数据目录是空的，
    /// 表现为"设备全没了、手机还得重新配对"。这里做一次性导入，
    /// 让"换成便携版"不等于"从头再来"。目标已有库时不覆盖。
    /// </summary>
    public static int? CopyInstalledDataToPortable()
    {
        var portableRoot = WirelessCable.Infrastructure.Paths.PortableLayout.Root;
        var targetData = Path.Combine(portableRoot, "Data");
        var targetDatabase = Path.Combine(targetData, "wirelesscable.db");
        if (File.Exists(targetDatabase))
        {
            return null;
        }

        var sourceRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WirelessCable");
        var sourceDatabase = Path.Combine(sourceRoot, "data", "wirelesscable.db");
        if (!File.Exists(sourceDatabase))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(targetData);
            var copied = 0;

            // WAL 与 shm 一起拷：只拷主库会丢掉最近提交、还没 checkpoint 的那部分
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var source = sourceDatabase + suffix;
                if (File.Exists(source))
                {
                    File.Copy(source, targetDatabase + suffix, overwrite: true);
                    copied++;
                }
            }

            var sourceCerts = Path.Combine(sourceRoot, "certs");
            if (Directory.Exists(sourceCerts))
            {
                var targetCerts = Path.Combine(targetData, "certs");
                Directory.CreateDirectory(targetCerts);
                foreach (var file in Directory.EnumerateFiles(sourceCerts))
                {
                    File.Copy(file, Path.Combine(targetCerts, Path.GetFileName(file)), overwrite: true);
                    copied++;
                }
            }

            Log.Information("已把安装版数据导入便携目录：{Target}（{Count} 个文件）", targetData, copied);
            return copied;
        }
        catch (Exception ex)
        {
            // 导入失败不影响启动：便携版就当新装用，用户重新配对即可
            Log.Warning(ex, "导入安装版数据失败，便携版按空库启动");
            return null;
        }
    }

    /// <summary>返回搬过去的文件数；没有搬家时返回 null。</summary>
    public static int? CopyDefaultDataIfNeeded(string dataRoot)
    {
        var defaultRoot = DesktopSettingsStore.DefaultRoot;
        if (string.Equals(
                Path.GetFullPath(defaultRoot).TrimEnd('\\'),
                Path.GetFullPath(dataRoot).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var targetDatabase = Path.Combine(dataRoot, "data", "wirelesscable.db");
        if (File.Exists(targetDatabase))
        {
            // 目标已有库：用户是在指回一份旧数据，别再覆盖
            return null;
        }

        // 目标落在要搬的源目录里（例如把数据根指到 ...\WirelessCable\data\x）：
        // 边读边往自己里面拷会无限递归，直接放弃搬迁
        if (IsInside(dataRoot, Path.Combine(defaultRoot, "data")) ||
            IsInside(dataRoot, Path.Combine(defaultRoot, "certs")))
        {
            return null;
        }

        if (!File.Exists(Path.Combine(defaultRoot, "data", "wirelesscable.db")))
        {
            // 默认位置本来就没有数据，没有可搬的
            return null;
        }

        try
        {
            var copied = 0;
            foreach (var name in CopiedDirectories)
            {
                var source = Path.Combine(defaultRoot, name);
                if (!Directory.Exists(source))
                {
                    continue;
                }

                copied += CopyDirectory(source, Path.Combine(dataRoot, name));
            }

            // 旧库留着不动：迁移出问题时用户还能把设置改回去
            Directory.CreateDirectory(Path.Combine(dataRoot, "received"));
            return copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "迁移数据目录失败，继续使用空目录 {@Target}", dataRoot);
            return null;
        }
    }

    private static int CopyDirectory(string source, string destination)    {
        Directory.CreateDirectory(destination);
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target))
            {
                continue;
            }

            File.Copy(file, target);
            count++;
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            count += CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        return count;
    }

    /// <summary>candidate 是否位于 parent 之内（含相等）。</summary>
    private static bool IsInside(string candidate, string parent)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }
}
