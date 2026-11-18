using WirelessCable.Core.Abstractions;

namespace WirelessCable.Infrastructure.Paths;

/// <summary>
/// Windows 目录布局。
///
/// 两种模式：
/// - **安装版**（默认）：根是 %LOCALAPPDATA%\WirelessCable，用户可在设置里改数据根与日志目录；
/// - **绿色版**：程序目录下有 Data/ 时启用，数据在 Data/、日志在 Logs/、临时文件在 TransferTemp/，
///   不写用户目录，整包可迁移。
/// </summary>
public sealed class AppPaths : IAppPaths
{
    /// <param name="portable">
    /// 绿色版判定。默认按程序目录里的标记推断；测试可显式传入，
    /// 免得为了测布局去改进程级环境变量（会污染并行跑的其他用例）。
    /// </param>
    public AppPaths(
        string? rootOverride = null,
        string? dataRootOverride = null,
        string? logsOverride = null,
        bool? portable = null)
    {
        // 绿色版：所有可变数据都留在程序目录里，整个文件夹拷走即完成迁移。
        // 用户显式指定了目录（安装版设置项）时不走这条路，避免两套规则打架。
        if (rootOverride is null && dataRootOverride is null && (portable ?? PortableLayout.IsPortable))
        {
            RootDirectory = PortableLayout.Root;
            DataDirectory = Path.Combine(RootDirectory, "Data");
            CertificatesDirectory = Path.Combine(DataDirectory, "certs");
            ReceivedDirectory = Path.Combine(DataDirectory, "received");
            TransferTempDirectory = Path.Combine(RootDirectory, "TransferTemp");
            DatabasePath = Path.Combine(DataDirectory, "wirelesscable.db");
            LogsDirectory = string.IsNullOrWhiteSpace(logsOverride)
                ? Path.Combine(RootDirectory, "Logs")
                : logsOverride!;
            return;
        }

        RootDirectory = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WirelessCable");

        // 数据根可以单独换位置：库、证书、接收文件、临时文件都跟着走，
        // 这样"把数据放到 D 盘"是一次迁移而不是四个目录各配一遍。
        var dataRoot = string.IsNullOrWhiteSpace(dataRootOverride) ? RootDirectory : dataRootOverride!;

        DataDirectory = Path.Combine(dataRoot, "data");
        CertificatesDirectory = Path.Combine(dataRoot, "certs");
        ReceivedDirectory = Path.Combine(dataRoot, "received");
        TransferTempDirectory = Path.Combine(dataRoot, "temp");
        DatabasePath = Path.Combine(DataDirectory, "wirelesscable.db");

        LogsDirectory = string.IsNullOrWhiteSpace(logsOverride)
            ? Path.Combine(RootDirectory, "logs")
            : logsOverride!;
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string LogsDirectory { get; }
    public string CertificatesDirectory { get; }
    public string ReceivedDirectory { get; }
    public string TransferTempDirectory { get; }
    public string DatabasePath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CertificatesDirectory);
        Directory.CreateDirectory(ReceivedDirectory);
        Directory.CreateDirectory(TransferTempDirectory);
    }
}
