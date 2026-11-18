namespace WirelessCable.Core.Abstractions;

/// <summary>
/// 应用私有目录布局。所有可写数据都必须落在这几个目录内，
/// 业务层不得自行拼接 %APPDATA% / %LOCALAPPDATA% 等路径。
/// </summary>
public interface IAppPaths
{
    /// <summary>应用根目录，例如 %LOCALAPPDATA%\WirelessCable。</summary>
    string RootDirectory { get; }

    /// <summary>数据库与状态文件目录。</summary>
    string DataDirectory { get; }

    /// <summary>日志目录。</summary>
    string LogsDirectory { get; }

    /// <summary>服务端证书目录。</summary>
    string CertificatesDirectory { get; }

    /// <summary>默认接收文件目录。</summary>
    string ReceivedDirectory { get; }

    /// <summary>传输临时文件目录。<b>不得</b>位于共享目录内，否则半成品会出现在用户视野里。</summary>
    string TransferTempDirectory { get; }

    /// <summary>SQLite 数据库文件完整路径。</summary>
    string DatabasePath { get; }

    /// <summary>确保上述目录全部存在。</summary>
    void EnsureCreated();
}
