using System.Collections.Concurrent;
using QRCoder;
using WirelessCable.Infrastructure.Security;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;

namespace WirelessCable.Server.Pairing;

/// <summary>
/// 二维码配对。二维码只携带"怎么连、连谁、指纹是什么"和一次性随机数，
/// **不含任何长期密钥**；随机数由服务端单次消费，用于防重放。
/// </summary>
public sealed class QrPairingService
{
    /// <summary>Windows 移动热点的默认网段前缀。</summary>
    private const string DefaultHotspotPrefix = "192.168.137.";

    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);

    /// <summary>生成新的二维码负载（内含一次性随机数）。</summary>
    public QrPayloadDto CreatePayload(
        string deviceId,
        string deviceName,
        IReadOnlyList<string> addresses,
        int port,
        string fingerprint,
        QrHotspotDto? hotspot = null,
        bool dropStaleHotspotSubnet = false)
    {
        Purge();

        var nonce = PairingCrypto.GenerateNonce();
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ProtocolConstants.QrNonceTtlSeconds);
        _nonces[nonce] = expiresAt;

        return new QrPayloadDto
        {
            Version = ProtocolConstants.CurrentVersion,
            DeviceId = deviceId,
            DeviceName = deviceName,
            Addresses = ExcludeHotspot(addresses, hotspot?.Address, dropStaleHotspotSubnet),
            Hotspot = hotspot,
            Port = port,
            Fingerprint = fingerprint,
            Nonce = nonce,
            ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>
    /// 把热点地址从"现有网络地址"里剔掉。
    ///
    /// 热点只是兜底：手机扫码后先用 addresses 里的地址连（同一 Wi-Fi/网线的常规路径），
    /// 全部连不上才去连热点、再按 hotspot.address 连一次。
    /// 热点地址混进 addresses 会让手机在没连热点时白等一次连接超时。
    /// </summary>
    /// <param name="dropStaleHotspotSubnet">
    /// 刚刚**真的关掉了**应用开的热点时才为 true：网卡上的 192.168.137.x 会在系统里
    /// 多留几秒（实测仍在 addresses 里），那时它一定不可达，留着只会让手机每试一次等满超时。
    ///
    /// 不能无条件按网段剔——用户的真实局域网可能就在 192.168.137.0/24
    /// （Windows 热点的默认网段，但也能被路由器用），或者他自个儿开的热点还开着。
    /// </param>
    public static IReadOnlyList<string> ExcludeHotspot(
        IReadOnlyList<string> addresses,
        string? hotspotAddress,
        bool dropStaleHotspotSubnet = false)
    {
        var result = new List<string>(addresses.Count);

        foreach (var address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(hotspotAddress)
                && string.Equals(address, hotspotAddress, StringComparison.Ordinal))
            {
                continue;
            }

            if (dropStaleHotspotSubnet
                && address.StartsWith(DefaultHotspotPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!result.Contains(address, StringComparer.Ordinal))
            {
                result.Add(address);
            }
        }

        return result;
    }

    /// <summary>序列化为二维码文本。</summary>
    public static string Serialize(QrPayloadDto payload) => WcJson.Serialize(payload);

    /// <summary>生成 PNG 字节，供界面显示。默认 8 像素/模块，纠错级别 M。</summary>
    public static byte[] CreatePng(string content, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }

    /// <summary>消费一次性随机数。已过期或不存在返回 false。</summary>
    public bool Consume(string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        if (!_nonces.TryRemove(nonce, out var expiresAt))
        {
            return false;
        }

        return expiresAt > DateTimeOffset.UtcNow;
    }

    private void Purge()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _nonces)
        {
            if (pair.Value <= now)
            {
                _nonces.TryRemove(pair.Key, out _);
            }
        }
    }
}
