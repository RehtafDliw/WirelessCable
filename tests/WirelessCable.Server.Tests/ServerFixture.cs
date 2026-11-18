using Microsoft.Data.Sqlite;
using Serilog;
using WirelessCable.Infrastructure.Certificates;
using WirelessCable.Infrastructure.Paths;
using WirelessCable.Infrastructure.Security;
using WirelessCable.Server;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 集成测试夹具：真实启动 Kestrel（HTTPS），用 HttpClient 打真实请求。
/// 测试内接受自签证书（ServerCertificateCustomValidationCallback），
/// 这只用于测试，正式连接必须做指纹固定校验。
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private WirelessCableServer _server = null!;
    private HttpClientHandler _handler = null!;

    public string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), "wc-tests", Guid.NewGuid().ToString("N"));

    public AppPaths Paths { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public WirelessCableServer Server => _server;

    public int Port => _server.Port ?? throw new InvalidOperationException("服务未启动，端口不可用。");

    public async Task InitializeAsync()
    {
        Log.Logger = new LoggerConfiguration().MinimumLevel.Fatal().CreateLogger();

        Paths = new AppPaths(RootDirectory);
        Paths.EnsureCreated();

        var database = new SqliteDatabaseFactory(Paths.DatabasePath);
        var appConfig = new AppConfigRepository(database);
        var devices = new DeviceRepository(database);
        var certificates = new ServerCertificateManager(Paths, new DpapiSecretProtector());

        var preferredPort = 48100 + Random.Shared.Next(0, 400);

        _server = new WirelessCableServer(
            new ServerOptions
            {
                PreferredPort = preferredPort,
                DisplayName = "测试主机",
                AppVersion = "0.1.0-test",
                RateLimitPerMinute = 600,
            },
            Paths,
            database,
            appConfig,
            devices,
            certificates);

        if (!await _server.StartAsync())
        {
            throw new InvalidOperationException($"测试服务启动失败：{_server.Snapshot().LastError}");
        }

        _handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };

        Client = new HttpClient(_handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{Port}"),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    /// <summary>在进程内跑一次完整配对，返回可用令牌（等价于手机端完成配对）。</summary>
    public async Task<(string DeviceId, string Token)> PairAsync(string deviceName = "测试手机")
    {
        using var client = new PairingSimulator { DeviceName = deviceName };
        var begin = Server.Pairing.Begin(client.BuildRequest(), "127.0.0.1");
        if (!begin.Success || begin.Challenge is null)
        {
            throw new InvalidOperationException($"配对发起失败：{begin.ErrorCode} {begin.Message}");
        }

        var challenge = begin.Challenge;
        var code = Server.Pairing.GetSessions().Single(s => s.RequestId == challenge.RequestId).Code;
        if (!Server.Pairing.TryApprove(challenge.RequestId, out var approveError))
        {
            throw new InvalidOperationException($"批准失败：{approveError}");
        }

        var outcome = await Server.Pairing.ConfirmAsync(client.BuildConfirm(challenge, code), CancellationToken.None);
        if (!outcome.Success || outcome.Response is null)
        {
            throw new InvalidOperationException($"配对确认失败：{outcome.ErrorCode} {outcome.Message}");
        }

        return (client.DeviceId, client.UnsealToken(outcome.Response, challenge.RequestId));
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        _handler?.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);

        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试目录残留不影响结论
        }
    }
}
