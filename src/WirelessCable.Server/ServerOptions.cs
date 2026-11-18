using WirelessCable.Protocol;

namespace WirelessCable.Server;

/// <summary>服务端启动参数。</summary>
public sealed class ServerOptions
{
    /// <summary>首选 HTTPS 端口，被占用时按 <see cref="PortProbeCount"/> 向后探测。</summary>
    public int PreferredPort { get; init; } = ProtocolConstants.DefaultHttpsPort;

    /// <summary>端口探测次数。</summary>
    public int PortProbeCount { get; init; } = ProtocolConstants.PortProbeCount;

    /// <summary>显示名；为空时取机器名。</summary>
    public string? DisplayName { get; init; }

    /// <summary>应用版本，原样出现在健康检查与设备信息里。默认取程序集版本。</summary>
    public string AppVersion { get; init; } = AppVersionInfo.Current;

    /// <summary>每 IP 每分钟请求上限。</summary>
    /// <summary>
    /// 每台设备的每分钟请求上限。
    /// <para>
    /// 1.5.6 从 120 提到 600：手机端现在有三条轮询（心跳/索引、作业长轮询、传输任务），
    /// 传输页开着时还要再叠一条——120 会被正常轮询打满，回 429 后手机把心跳失败
    /// 显示成"连接中断"，看起来像掉线（1.5.6 真机踩到）。
    /// </para>
    /// <para>
    /// 放宽不降低安全性：所有非回环请求都要令牌，分块路径本来就不计入配额，
    /// 600/分钟（10/秒）对局域网轮询足够宽裕，仍能挡住失控的客户端。
    /// </para>
    /// </summary>
    public int RateLimitPerMinute { get; init; } = 600;

    /// <summary>允许的客户端协议版本集合。</summary>
    public IReadOnlyList<int> SupportedProtocolVersions { get; init; } = [ProtocolConstants.CurrentVersion];

    /// <summary>共享目录（手机上传的落点）。为空时使用应用目录下的 received。</summary>
    public string? SharedDirectory { get; init; }
}
