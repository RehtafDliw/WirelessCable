using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using WirelessCable.Infrastructure.Certificates;
using WirelessCable.Infrastructure.Paths;
using WirelessCable.Infrastructure.Security;
using WirelessCable.Protocol;
using WirelessCable.Server.Security;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 请求频率限制。
///
/// 中间件对回环地址免单（界面轮询与集成测试都从 127.0.0.1 来），
/// 所以这里分两层证明：限流器本身的计数语义（确定性单测），
/// 以及"非回环来源真的会拿到 429"（用本机局域网地址发请求）。
/// </summary>
public sealed class RateLimitTests
{
    [Fact]
    public void Limiter_RejectsAfterLimit()
    {
        var limiter = new FixedWindowRateLimiter(limitPerMinute: 3);

        Assert.True(limiter.TryAcquire("10.0.0.5", out _));
        Assert.True(limiter.TryAcquire("10.0.0.5", out _));
        Assert.True(limiter.TryAcquire("10.0.0.5", out _));
        Assert.False(limiter.TryAcquire("10.0.0.5", out var retryAfter));
        Assert.True(retryAfter >= 1);

        // 另一个来源不受影响：限流按 key 隔离，不能一台设备把所有人挡住
        Assert.True(limiter.TryAcquire("10.0.0.6", out _));
    }

    [Fact]
    public void Limiter_ResetsAfterWindow()
    {
        var limiter = new FixedWindowRateLimiter(limitPerMinute: 2, window: TimeSpan.FromMilliseconds(250));

        Assert.True(limiter.TryAcquire("10.0.0.7", out _));
        Assert.True(limiter.TryAcquire("10.0.0.7", out _));
        Assert.False(limiter.TryAcquire("10.0.0.7", out _));

        Thread.Sleep(300);

        Assert.True(limiter.TryAcquire("10.0.0.7", out _));
    }

    [Fact]
    public async Task NonLoopbackClient_Gets429()
    {
        var lanAddress = FindLanAddress();
        if (lanAddress is null)
        {
            // 本机没有非回环 IPv4（例如离线机器）：跳过，并在测试输出里留痕
            Console.WriteLine("[skip] 无局域网地址，无法验证 429");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "wc-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();

        var database = new SqliteDatabaseFactory(paths.DatabasePath);
        var port = 48700 + Random.Shared.Next(0, 200);

        await using var server = new WirelessCableServer(
            new ServerOptions
            {
                PreferredPort = port,
                DisplayName = "限流测试",
                RateLimitPerMinute = 3,
            },
            paths,
            database,
            new AppConfigRepository(database),
            new DeviceRepository(database),
            new ServerCertificateManager(paths, new DpapiSecretProtector()));

        Assert.True(await server.StartAsync());
        var actualPort = server.Port!.Value;

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var url = $"https://{lanAddress}:{actualPort}/api/v1/health";
        var codes = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            using var response = await client.GetAsync(url);
            codes.Add(response.StatusCode);
        }

        await server.StopAsync();
        SqliteConnection.ClearAllPools();

        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.OK, codes[0]);
    }

    private static string? FindLanAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address.Address))
                {
                    return address.Address.ToString();
                }
            }
        }

        return null;
    }
}
