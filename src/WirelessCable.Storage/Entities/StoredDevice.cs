namespace WirelessCable.Storage.Entities;

/// <summary>Devices 表的一行：已配对设备及其长期凭据。</summary>
public sealed class StoredDevice
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required string Platform { get; init; }

    /// <summary>对端 TLS 证书指纹（展示与审计用）。</summary>
    public string CertificateFingerprint { get; init; } = string.Empty;

    /// <summary>对端长期身份公钥（ECDSA P-256，Base64 SPKI）。</summary>
    public string IdentityPublicKey { get; init; } = string.Empty;

    /// <summary>设备令牌的 SHA-256（十六进制）。明文令牌不落库。</summary>
    public string TokenHash { get; init; } = string.Empty;

    public bool IsPaired { get; init; }
    public bool IsRevoked { get; init; }

    /// <summary>
    /// 用户主动"断开"了这台设备。
    ///
    /// 与撤销/删除不同：配对、令牌、授权全部保留，只是服务端拒绝它的请求（423），
    /// 直到在电脑上点「连接」。手机侧收到 423 会停下来并提示原因。
    /// </summary>
    public bool SessionPaused { get; init; }

    /// <summary>
    /// 该设备对"此电脑"（电脑整机）的访问档位，见 <see cref="WirelessCable.Protocol.MachineAccessLevels"/>。
    /// 默认 2（可读写）：新配对设备直接能看、能改全部文件；
    /// 用户可在「共享目录」页降为只读或关闭。
    /// </summary>
    public int MachineAccess { get; init; } = WirelessCable.Core.Domain.MachineAccessLevels.Default;
    public DateTimeOffset? LastSeenUtc { get; init; }
    public DateTimeOffset? PairedAtUtc { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
}
