using System.Net.Http.Headers;
using WirelessCable.Protocol;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// "设备是否在线"的判据。
///
/// 以前只有 <c>/api/v1/heartbeat</c> 刷新 last_seen：手机跑长作业（保存整个文件夹、
/// 传一个大文件）时主循环卡在作业里，根本轮不到发心跳，桌面端就会把一台**正在干活**的
/// 手机判成离线。1.5.59 起任何带令牌的请求都算"还活着"，这里锁住这个行为。
/// </summary>
public sealed class DevicePresenceTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public DevicePresenceTests(ServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Authenticated_request_refreshes_last_seen_without_heartbeat()
    {
        var (deviceId, token) = await _fixture.PairAsync("保活手机");

        // 模拟"五分钟没心跳"
        await _fixture.Server.Devices.UpdateLastSeenAsync(
            deviceId,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            CancellationToken.None);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ProtocolConstants.FileSystemPath + "/roots");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var device = await _fixture.Server.Devices.GetAsync(deviceId, CancellationToken.None);
        Assert.NotNull(device);
        Assert.NotNull(device!.LastSeenUtc);
        Assert.True(
            DateTimeOffset.UtcNow - device.LastSeenUtc!.Value < TimeSpan.FromSeconds(10),
            $"业务请求没有刷新 last_seen：{device.LastSeenUtc:O}");
    }

    [Fact]
    public async Task Anonymous_request_does_not_touch_last_seen()
    {
        var (deviceId, _) = await _fixture.PairAsync("匿名检验手机");

        await _fixture.Server.Devices.UpdateLastSeenAsync(
            deviceId,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            CancellationToken.None);

        // 不带令牌：中间件在鉴权那一步就拒了，不应该刷新任何设备的 last_seen
        using var response = await _fixture.Client.GetAsync(ProtocolConstants.FileSystemPath + "/roots");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);

        var device = await _fixture.Server.Devices.GetAsync(deviceId, CancellationToken.None);
        Assert.NotNull(device?.LastSeenUtc);
        Assert.True(
            DateTimeOffset.UtcNow - device!.LastSeenUtc!.Value > TimeSpan.FromMinutes(4),
            "匿名请求居然刷新了 last_seen。");
    }
}
