using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using WirelessCable.Infrastructure.Paths;
using WirelessCable.Infrastructure.Security;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using WirelessCable.Server;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

public sealed class ServerEndpointTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public ServerEndpointTests(ServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Health_Anonymous_ReturnsBasicFieldsOnly()
    {
        var response = await _fixture.Client.GetAsync(ProtocolConstants.HealthPath);
        var body = await response.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<HealthInfo>>(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Ok);
        var health = result.Data!;

        Assert.Equal("running", health.Status);
        Assert.False(health.Authenticated);
        Assert.Equal(ProtocolConstants.CurrentVersion, health.ProtocolVersion);
        Assert.Equal("0.1.0-test", health.AppVersion);

        // 端口与指纹随 mDNS / TLS 握手本就公开
        Assert.Equal(_fixture.Port, health.Port);
        Assert.Equal(_fixture.Server.Fingerprint, health.CertificateFingerprint);

        // 运行态情报不下发给匿名调用方
        Assert.Null(health.UptimeSeconds);
        Assert.Null(health.DatabaseReady);
        Assert.Null(health.ActiveSessions);
        Assert.Null(health.Capabilities);
        Assert.DoesNotContain("uptimeSeconds", body, StringComparison.Ordinal);
        Assert.DoesNotContain("capabilities", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_WithToken_ReturnsOperationalDetail()
    {
        var (_, token) = await _fixture.PairAsync("健康检查手机");

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.HealthPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var health = WcJson.DeserializeRequired<ApiResult<HealthInfo>>(body).Data!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(health.Authenticated);
        Assert.True(health.DatabaseReady);
        Assert.NotNull(health.UptimeSeconds);
        Assert.NotNull(health.ActiveSessions);
        Assert.Contains(Capabilities.Health, health.Capabilities!);
        Assert.Equal(_fixture.Port, health.Port);
    }

    [Fact]
    public async Task Health_WithRevokedToken_IsRejected()
    {
        var (deviceId, token) = await _fixture.PairAsync("被撤销后再探健康");

        using var revokeRequest = new HttpRequestMessage(HttpMethod.Delete, $"{ProtocolConstants.DevicesPath}/{deviceId}");
        revokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await _fixture.Client.SendAsync(revokeRequest)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.HealthPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<object>>(body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.DeviceRevoked, result.Error!.Code);
    }

    [Fact]
    public async Task PausedDevice_IsLockedOut_AndRecoversWithoutRepairing()
    {
        var (deviceId, token) = await _fixture.PairAsync("被断开的手机");

        // 用户在电脑上点「断开」：只写一个标记，配对与令牌都还在
        var repository = new WirelessCable.Storage.Repositories.DeviceRepository(
            new WirelessCable.Storage.Sqlite.SqliteDatabaseFactory(_fixture.Paths.DatabasePath));
        await repository.SetSessionPausedAsync(deviceId, true);

        using var pausedRequest = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.HealthPath);
        pausedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var pausedResponse = await _fixture.Client.SendAsync(pausedRequest);
        var pausedBody = await pausedResponse.Content.ReadAsStringAsync();
        var paused = WcJson.DeserializeRequired<ApiResult<object>>(pausedBody);

        // 423 而不是 401/403：手机据此停在"被断开"，不会引导用户重新配对
        Assert.Equal((HttpStatusCode)423, pausedResponse.StatusCode);
        Assert.Equal(ErrorCodes.SessionPaused, paused.Error!.Code);

        // 点「连接」：同一个令牌立刻恢复，不需要重新配对
        await repository.SetSessionPausedAsync(deviceId, false);

        using var resumedRequest = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.HealthPath);
        resumedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resumedResponse = await _fixture.Client.SendAsync(resumedRequest);

        Assert.Equal(HttpStatusCode.OK, resumedResponse.StatusCode);
    }

    [Fact]
    public async Task DeviceInfo_WithoutToken_IsRejected()
    {
        var response = await _fixture.Client.GetAsync(ProtocolConstants.DeviceInfoPath);
        var body = await response.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<object>>(body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.Unauthorized, result.Error!.Code);
        Assert.DoesNotContain("测试主机", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeviceInfo_ReturnsPersistedIdentity()
    {
        var response = await GetDeviceResponseAsync();
        var body = await response.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<DeviceInfo>>(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Ok);
        var device = result.Data!;

        Assert.Equal(32, device.DeviceId.Length);
        Assert.True(Guid.TryParseExact(device.DeviceId, "N", out _));
        Assert.Equal("测试主机", device.DisplayName);
        Assert.Equal(PlatformKind.Windows, device.Platform);
        Assert.Equal(_fixture.Port, device.Port);
        Assert.Equal(ProtocolConstants.CurrentVersion, device.ProtocolVersion);
        Assert.Equal(_fixture.Server.Fingerprint, device.CertificateFingerprint);
        Assert.NotEmpty(device.CertificateFingerprint);
    }

    [Fact]
    public async Task DeviceId_IsStableAcrossRequests()
    {
        var first = await GetDeviceAsync();
        var second = await GetDeviceAsync();

        Assert.Equal(first.DeviceId, second.DeviceId);
    }

    [Fact]
    public async Task UnknownRoute_Returns404Envelope()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/does-not-exist");
        var body = await response.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<object>>(body);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.NotFound, result.Error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.TraceId));
    }

    [Fact]
    public async Task UnsupportedProtocolVersion_Returns426()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.HealthPath);
        request.Headers.Add(ProtocolConstants.VersionHeader, "99");

        var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<object>>(body);

        Assert.Equal((HttpStatusCode)426, response.StatusCode);
        Assert.Equal(ErrorCodes.ProtocolVersionUnsupported, result.Error!.Code);
        Assert.Contains("supported=", result.Error.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupportedProtocolVersion_Returns200()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.HealthPath);
        request.Headers.Add(ProtocolConstants.VersionHeader, ProtocolConstants.CurrentVersion.ToString());

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TraceIdHeader_MatchesBody()
    {
        var response = await _fixture.Client.GetAsync(ProtocolConstants.HealthPath);
        var body = await response.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<HealthInfo>>(body);

        Assert.True(response.Headers.TryGetValues(ProtocolConstants.TraceIdHeader, out var values));
        Assert.Equal(result.TraceId, values!.Single());
    }

    [Fact]
    public async Task Health_ResponseIsUtf8Json()
    {
        var response = await _fixture.Client.GetAsync(ProtocolConstants.HealthPath);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
    }

    [Fact]
    public async Task DatabaseFile_IsCreated()
    {
        await _fixture.Client.GetAsync(ProtocolConstants.HealthPath);

        Assert.True(File.Exists(_fixture.Paths.DatabasePath));
        Assert.Equal(SchemaInitializer.SchemaVersion, SchemaInitializer.GetSchemaVersion(new SqliteDatabaseFactory(_fixture.Paths.DatabasePath)));
    }

    [Fact]
    public async Task UnknownLongPath_StillReturnsStructuredEnvelope()
    {
        var longPath = "/api/v1/" + new string('a', 2048);
        var response = await _fixture.Client.GetAsync(longPath);
        var body = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("ok", out var ok));
        Assert.False(ok.GetBoolean());
        Assert.True(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task PairingOverHttp_EndToEnd_IssuesWorkingToken()
    {
        using var phone = new PairingSimulator { DeviceName = "HTTP配对手机" };

        var requestJson = WcJson.Serialize(phone.BuildRequest());
        using var requestContent = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
        var challengeResponse = await _fixture.Client.PostAsync(ProtocolConstants.PairRequestPath, requestContent);
        var challengeBody = await challengeResponse.Content.ReadAsStringAsync();
        var challengeResult = WcJson.DeserializeRequired<ApiResult<PairingChallengeDto>>(challengeBody);

        Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
        Assert.True(challengeResult.Ok, challengeResult.Error?.Message);
        var challenge = challengeResult.Data!;

        // 响应里绝不能出现验证码
        var code = _fixture.Server.Pairing.GetSessions().Single(s => s.RequestId == challenge.RequestId).Code;
        Assert.DoesNotContain(code, challengeBody, StringComparison.Ordinal);
        Assert.Equal(ProtocolConstants.PairingSuite, challenge.PairingSuite);

        Assert.True(_fixture.Server.Pairing.TryApprove(challenge.RequestId, out var approveError), approveError);

        var confirmJson = WcJson.Serialize(phone.BuildConfirm(challenge, code));
        using var confirmContent = new StringContent(confirmJson, System.Text.Encoding.UTF8, "application/json");
        var confirmResponse = await _fixture.Client.PostAsync(ProtocolConstants.PairConfirmPath, confirmContent);
        var confirmBody = await confirmResponse.Content.ReadAsStringAsync();
        var confirmResult = WcJson.DeserializeRequired<ApiResult<PairingConfirmResponseDto>>(confirmBody);

        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        Assert.True(confirmResult.Ok, confirmResult.Error?.Message);

        var token = phone.UnsealToken(confirmResult.Data!, challenge.RequestId);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.DevicesPath);
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var listResponse = await _fixture.Client.SendAsync(listRequest);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    }

    [Fact]
    public async Task WrongCodeOverHttp_IsRejected()
    {
        using var phone = new PairingSimulator { DeviceName = "错误码手机" };

        using var requestContent = new StringContent(
            WcJson.Serialize(phone.BuildRequest()), System.Text.Encoding.UTF8, "application/json");
        var challengeBody = await (await _fixture.Client.PostAsync(ProtocolConstants.PairRequestPath, requestContent))
            .Content.ReadAsStringAsync();
        var challenge = WcJson.DeserializeRequired<ApiResult<PairingChallengeDto>>(challengeBody).Data!;

        Assert.True(_fixture.Server.Pairing.TryApprove(challenge.RequestId, out _));

        using var confirmContent = new StringContent(
            WcJson.Serialize(phone.BuildConfirm(challenge, "000000", useWrongCode: true)),
            System.Text.Encoding.UTF8,
            "application/json");
        var confirmResponse = await _fixture.Client.PostAsync(ProtocolConstants.PairConfirmPath, confirmContent);
        var body = await confirmResponse.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<object>>(body);

        Assert.Equal(HttpStatusCode.Unauthorized, confirmResponse.StatusCode);
        Assert.Equal(ErrorCodes.PairingCodeInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task Devices_WithoutToken_IsRejected()
    {
        var response = await _fixture.Client.GetAsync(ProtocolConstants.DevicesPath);
        var body = await response.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<object>>(body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.Unauthorized, result.Error!.Code);
    }

    [Fact]
    public async Task PairedDevice_CanListDevicesAndHeartbeat()
    {
        var (deviceId, token) = await _fixture.PairAsync("心跳手机");

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.DevicesPath);
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var listResponse = await _fixture.Client.SendAsync(listRequest);
        var listBody = await listResponse.Content.ReadAsStringAsync();
        var list = WcJson.DeserializeRequired<ApiResult<List<TrustedDeviceDto>>>(listBody);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.True(list.Ok);
        Assert.Contains(list.Data!, d => d.DeviceId == deviceId);

        using var beatRequest = new HttpRequestMessage(HttpMethod.Post, ProtocolConstants.HeartbeatPath);
        beatRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        beatRequest.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var beatResponse = await _fixture.Client.SendAsync(beatRequest);
        var beatBody = await beatResponse.Content.ReadAsStringAsync();
        var beat = WcJson.DeserializeRequired<ApiResult<HeartbeatResponseDto>>(beatBody);

        Assert.Equal(HttpStatusCode.OK, beatResponse.StatusCode);
        Assert.Equal("active", beat.Data!.SessionState);
        Assert.True(beat.Data.DeviceOnline);
    }

    [Fact]
    public async Task RevokedDevice_LosesAccessImmediately()
    {
        var (deviceId, token) = await _fixture.PairAsync("被撤销手机");

        using var revokeRequest = new HttpRequestMessage(HttpMethod.Delete, $"{ProtocolConstants.DevicesPath}/{deviceId}");
        revokeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var revokeResponse = await _fixture.Client.SendAsync(revokeRequest);
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.DevicesPath);
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var listResponse = await _fixture.Client.SendAsync(listRequest);
        var body = await listResponse.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<object>>(body);

        Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);
        Assert.Equal(ErrorCodes.DeviceRevoked, result.Error!.Code);
    }

    private async Task<DeviceInfo> GetDeviceAsync()
    {
        var body = await (await GetDeviceResponseAsync()).Content.ReadAsStringAsync();
        return WcJson.DeserializeRequired<ApiResult<DeviceInfo>>(body).Data!;
    }

    /// <summary>device/info 需要设备令牌，先配对再取。</summary>
    private async Task<HttpResponseMessage> GetDeviceResponseAsync()
    {
        var (_, token) = await _fixture.PairAsync("设备信息手机");
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.DeviceInfoPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _fixture.Client.SendAsync(request);
    }
}

public sealed class ServerLifecycleTests
{
    [Fact]
    public async Task Stop_ReleasesListeningPort()
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();

        var database = new SqliteDatabaseFactory(paths.DatabasePath);
        var port = 48600 + Random.Shared.Next(0, 200);

        var server = new WirelessCableServer(
            new ServerOptions { PreferredPort = port, DisplayName = "释放测试" },
            paths,
            database,
            new AppConfigRepository(database),
            new DeviceRepository(database),
            new Infrastructure.Certificates.ServerCertificateManager(paths, new DpapiSecretProtector()));

        Assert.True(await server.StartAsync());
        var actualPort = server.Port!.Value;

        await server.StopAsync();
        Assert.Null(server.Port);

        using var listener = new TcpListener(IPAddress.Any, actualPort);
        listener.Start();
        listener.Stop();

        await server.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SecondServer_OnOccupiedPort_FallsBackToNextPort()
    {
        var first = await CreateServerAsync("端口占用-A", 48900);
        Assert.True(await first.StartAsync());
        var firstPort = first.Port!.Value;

        var second = await CreateServerAsync("端口占用-B", firstPort);
        Assert.True(await second.StartAsync());

        Assert.NotEqual(firstPort, second.Port);

        await first.DisposeAsync();
        await second.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static Task<WirelessCableServer> CreateServerAsync(string name, int port)
    {
        var root = Path.Combine(Path.GetTempPath(), "wc-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var database = new SqliteDatabaseFactory(paths.DatabasePath);

        return Task.FromResult(new WirelessCableServer(
            new ServerOptions { PreferredPort = port, DisplayName = name },
            paths,
            database,
            new AppConfigRepository(database),
            new DeviceRepository(database),
            new Infrastructure.Certificates.ServerCertificateManager(paths, new DpapiSecretProtector())));
    }
}
