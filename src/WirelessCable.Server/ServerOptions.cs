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
    /// 对局域网轮询足够宽裕，仍能挡住失控的客户端。
    /// </para>
    /// <para>
    /// 2.10.3 从 600 提到 1800：**整文件夹传输本身就是一个正常的请求尖峰**。
    /// 实测一次 326 个文件的上传：`/transfer/uploads` 330 次 + `/tasks/{id}/complete` 327 次，
    /// 再加作业/索引/心跳/列表轮询，两分钟内就冲到 600，之后连续 387 次 429——
    /// 手机端弹"请求过于频繁"，传输卡在最后几个文件。
    /// 每一次请求都是配对的设备发的，阈值该按"单设备批量传输的合法峰值"定，而不是按轮询定。
    /// </para>
    /// </summary>
    public int RateLimitPerMinute { get; init; } = 1800;

    /// <summary>允许的客户端协议版本集合。</summary>
    public IReadOnlyList<int> SupportedProtocolVersions { get; init; } = [ProtocolConstants.CurrentVersion];

    /// <summary>共享目录（手机上传的落点）。为空时使用应用目录下的 received。</summary>
    public string? SharedDirectory { get; init; }
}
