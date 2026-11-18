using System.Text.Json;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using Xunit;

namespace WirelessCable.Protocol.Tests;

/// <summary>
/// 配对报文的"最小暴露"约束。
///
/// 校验码是两端各自派生的比对值，绝不能出现在网络响应里：
/// 一旦下发，任何抓包者都能照着它算出 confirmation，人工确认就形同虚设。
/// 这条测试锁的是 DTO 形状——不是靠调用方自觉。
/// </summary>
public sealed class PairingWireShapeTests
{
    private static PairingChallengeDto Challenge() => new()
    {
        RequestId = "req-1",
        ServerEphemeralPublicKey = "SERVER_EPHEMERAL",
        ServerNonce = "SERVER_NONCE",
        ExpiresAtUnixMs = 1_800_000_000_000,
        State = PairingStates.Pending,
        ServerDeviceId = "serverdevice0001",
        ServerName = "客厅电脑",
        PairingSuite = ProtocolConstants.PairingSuite,
    };

    [Fact]
    public void Challenge_DoesNotCarryCode()
    {
        var json = JsonSerializer.Serialize(Challenge(), WcJson.Options);

        Assert.DoesNotContain("\"code\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sas", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_DoesNotCarryCode()
    {
        var status = new PairingStatusDto
        {
            RequestId = "req-1",
            State = PairingStates.Approved,
            ExpiresAtUnixMs = 1_800_000_000_000,
        };

        var json = JsonSerializer.Serialize(status, WcJson.Options);

        Assert.DoesNotContain("\"code\"", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>本机界面用的视图**必须**带码——它就是给用户比对的那一份。</summary>
    [Fact]
    public void SessionView_CarriesCodeForLocalDisplay()
    {
        var view = new PairingSessionViewDto
        {
            RequestId = "req-1",
            DeviceId = "device-1",
            DeviceName = "手机",
            Platform = "android",
            Code = "640833",
            State = PairingStates.Pending,
            CreatedAtUnixMs = 1,
            ExpiresAtUnixMs = 2,
            Attempts = 0,
        };

        var json = JsonSerializer.Serialize(view, WcJson.Options);

        Assert.Contains("\"code\":\"640833\"", json);
    }
}
