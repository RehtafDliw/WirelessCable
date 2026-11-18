using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using WirelessCable.Server.Pairing;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 二维码里的电脑热点字段。
///
/// 两条规则必须在协议层锁死：
/// 1. 热点地址**不能**混进 addresses——手机扫码后先用现有网络连，
///    混进去会让它白等一次连接超时；
/// 2. 字段名与 Android 端 QrHotspot 一致（hotspot/ssid/passphrase/address），
///    否则手机解析出来是 null，兜底通道悄悄失效。
/// </summary>
public sealed class QrPayloadTests
{
    private const string Fingerprint = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private static QrHotspotDto Hotspot(string address = "192.168.137.1") => new()
    {
        Ssid = "WirelessCable-7F2A",
        Passphrase = "12345678",
        Address = address,
    };

    [Fact]
    public void CreatePayload_KeepsHotspotAddressOutOfLanAddresses()
    {
        var service = new QrPairingService();

        var payload = service.CreatePayload(
            "device-1",
            "客厅电脑",
            ["172.18.15.63", "192.168.137.1", "172.17.13.108"],
            47110,
            Fingerprint,
            Hotspot());

        Assert.Equal(["172.18.15.63", "172.17.13.108"], payload.Addresses);
        Assert.NotNull(payload.Hotspot);
        Assert.Equal("192.168.137.1", payload.Hotspot!.Address);
    }

    [Fact]
    public void CreatePayload_WithoutHotspot_KeepsAllAddresses()
    {
        var service = new QrPairingService();

        var payload = service.CreatePayload(
            "device-1",
            "客厅电脑",
            ["172.18.15.63", "192.168.137.1"],
            47110,
            Fingerprint);

        Assert.Null(payload.Hotspot);
        Assert.Equal(["172.18.15.63", "192.168.137.1"], payload.Addresses);
    }

    [Fact]
    public void ExcludeHotspot_RemovesDuplicatesAndBlank()
    {
        var result = QrPairingService.ExcludeHotspot(
            ["172.18.15.63", "", "172.18.15.63", "192.168.137.1"],
            "192.168.137.1");

        Assert.Equal(["172.18.15.63"], result);
    }

    [Fact]
    public void SerializedPayload_UsesCamelCaseKeysAndroidExpects()
    {
        var service = new QrPairingService();
        var payload = service.CreatePayload(
            "device-1",
            "客厅电脑",
            ["172.18.15.63"],
            47110,
            Fingerprint,
            Hotspot());

        var json = QrPairingService.Serialize(payload);

        Assert.Contains("\"hotspot\":", json);
        Assert.Contains("\"ssid\":\"WirelessCable-7F2A\"", json);
        Assert.Contains("\"passphrase\":\"12345678\"", json);
        Assert.Contains("\"address\":\"192.168.137.1\"", json);

        // 往返之后字段仍在（防止序列化配置把 null 之外的字段也吞掉）
        var roundTrip = WcJson.DeserializeRequired<QrPayloadDto>(json);
        Assert.Equal("WirelessCable-7F2A", roundTrip.Hotspot!.Ssid);
        Assert.True(roundTrip.Version == ProtocolConstants.CurrentVersion);
    }
}
