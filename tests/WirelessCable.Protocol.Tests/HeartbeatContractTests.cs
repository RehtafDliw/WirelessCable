using WirelessCable.Protocol;
using Xunit;

namespace WirelessCable.Protocol.Tests;

/// <summary>
/// 心跳与"多久没心跳算离线"的关系。
///
/// 这两个数必须一起改：只调心跳不调窗口，一次丢包就误判离线；只调窗口不调心跳，
/// 手机早就没了桌面端还显示在线（1.5.59 现场）。用测试把这条约束钉住。
/// </summary>
public sealed class HeartbeatContractTests
{
    [Fact]
    public void OfflineWindow_covers_at_least_three_missed_heartbeats()
    {
        Assert.True(ProtocolConstants.HeartbeatIntervalSeconds > 0);
        Assert.True(
            ProtocolConstants.DeviceOfflineAfterSeconds >= ProtocolConstants.HeartbeatIntervalSeconds * 3,
            "离线窗口短于 3 个心跳周期：一次网络抖动就会让设备在线/离线来回跳。");
    }

    [Fact]
    public void Offline_is_detected_within_a_minute()
    {
        // 用户的验收口径：手机没了，桌面端一分钟内必须翻成离线。
        // 最坏情况 = 窗口 + 最后一拍心跳的间隔。
        var worstCase =
            ProtocolConstants.DeviceOfflineAfterSeconds + ProtocolConstants.HeartbeatIntervalSeconds;

        Assert.True(worstCase <= 60, $"最坏情况要 {worstCase} 秒才判离线，超过一分钟。");
    }
}
