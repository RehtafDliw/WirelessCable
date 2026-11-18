using WirelessCable.Infrastructure.Paths;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 目录布局。设置页允许把数据根与日志目录改到别处，
/// 这里锁死"换数据根时哪些目录跟着走、哪些不跟"。
/// </summary>
public sealed class AppPathsTests
{
    [Fact]
    public void DefaultLayout_KeepsEverythingUnderRoot()
    {
        var paths = new AppPaths(rootOverride: @"C:\root");

        Assert.Equal(@"C:\root", paths.RootDirectory);
        Assert.Equal(@"C:\root\data", paths.DataDirectory);
        Assert.Equal(@"C:\root\logs", paths.LogsDirectory);
        Assert.Equal(@"C:\root\certs", paths.CertificatesDirectory);
        Assert.Equal(@"C:\root\received", paths.ReceivedDirectory);
        Assert.Equal(@"C:\root\temp", paths.TransferTempDirectory);
        Assert.Equal(@"C:\root\data\wirelesscable.db", paths.DatabasePath);
    }

    [Fact]
    public void DataRootOverride_MovesDataButKeepsLogsAtRoot()
    {
        var paths = new AppPaths(rootOverride: @"C:\root", dataRootOverride: @"D:\wcdata");

        Assert.Equal(@"C:\root", paths.RootDirectory);
        Assert.Equal(@"D:\wcdata\data", paths.DataDirectory);
        Assert.Equal(@"D:\wcdata\certs", paths.CertificatesDirectory);
        Assert.Equal(@"D:\wcdata\received", paths.ReceivedDirectory);
        Assert.Equal(@"D:\wcdata\temp", paths.TransferTempDirectory);
        Assert.Equal(@"D:\wcdata\data\wirelesscable.db", paths.DatabasePath);

        // 日志不跟着数据根走：它可能被指定到别处集中收集
        Assert.Equal(@"C:\root\logs", paths.LogsDirectory);
    }

    [Fact]
    public void LogOverride_OnlyMovesLogs()
    {
        var paths = new AppPaths(rootOverride: @"C:\root", logsOverride: @"E:\logs\wc");

        Assert.Equal(@"E:\logs\wc", paths.LogsDirectory);
        Assert.Equal(@"C:\root\data", paths.DataDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOverrides_FallBackToDefaults(string blank)
    {
        var paths = new AppPaths(rootOverride: @"C:\root", dataRootOverride: blank, logsOverride: blank);

        Assert.Equal(@"C:\root\data", paths.DataDirectory);
        Assert.Equal(@"C:\root\logs", paths.LogsDirectory);
    }

    [Fact]
    public void EnsureCreated_CreatesEveryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-paths", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(rootOverride: root);
            paths.EnsureCreated();

            foreach (var directory in new[]
                     {
                         paths.DataDirectory, paths.LogsDirectory, paths.CertificatesDirectory,
                         paths.ReceivedDirectory, paths.TransferTempDirectory,
                     })
            {
                Assert.True(Directory.Exists(directory), directory);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

/// <summary>
/// 绿色版布局。发布包里的 Portable 目录就靠这套规则：数据/日志/临时文件全在程序目录，
/// 用户把文件夹拷到另一台机器后不需要重新配置。
/// </summary>
public sealed class PortableLayoutTests
{
    [Fact]
    public void Portable_LayoutStaysInsideProgramDirectory()
    {
        var paths = new AppPaths(portable: true);
        var baseDir = AppContext.BaseDirectory;

        Assert.Equal(baseDir, paths.RootDirectory);
        Assert.Equal(Path.Combine(baseDir, "Data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(baseDir, "Logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(baseDir, "Data", "certs"), paths.CertificatesDirectory);
        Assert.Equal(Path.Combine(baseDir, "Data", "received"), paths.ReceivedDirectory);
        Assert.Equal(Path.Combine(baseDir, "TransferTemp"), paths.TransferTempDirectory);
        Assert.Equal(Path.Combine(baseDir, "Data", "wirelesscable.db"), paths.DatabasePath);
    }

    [Fact]
    public void Portable_NeverWritesUnderLocalAppData()
    {
        var paths = new AppPaths(portable: true);
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        foreach (var dir in new[]
                 {
                     paths.RootDirectory, paths.DataDirectory, paths.LogsDirectory,
                     paths.CertificatesDirectory, paths.ReceivedDirectory, paths.TransferTempDirectory,
                 })
        {
            Assert.False(
                dir.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase),
                $"绿色版不应写到用户目录：{dir}");
        }

        Assert.False(paths.DatabasePath.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplicitDataRoot_WinsOverPortableDetection()
    {
        // 安装版设置里指定过数据目录时，即使程序目录恰好有 Data/ 也不该被便携规则接管
        var dataRoot = Path.Combine(Path.GetTempPath(), "wcdata");
        var paths = new AppPaths(dataRootOverride: dataRoot, portable: true);

        Assert.Equal(Path.Combine(dataRoot, "data"), paths.DataDirectory);
    }
}
