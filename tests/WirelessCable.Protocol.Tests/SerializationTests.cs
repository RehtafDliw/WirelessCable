using System.Text.Json;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using Xunit;

namespace WirelessCable.Protocol.Tests;

public sealed class SerializationTests
{
    /// <summary>
    /// 协议基准样例。Android 端 ProtocolSerializationTest.kt 使用同一份字符串，
    /// 双端必须都能解析，且字段名/类型/取值一致。
    /// </summary>
    private const string DeviceInfoFixture =
        """{"deviceId":"9f2c4b7a8e1d4c6fb0a3d5e7c9b1a2f3","displayName":"客厅电脑","platform":"windows","osVersion":"10.0.22631.0","appVersion":"0.1.0","protocolVersion":1,"certificateFingerprint":"a1b2c3d4","addresses":["192.168.1.10","10.0.0.5"],"port":47110,"capabilities":["health","device_info"]}""";

    private static DeviceInfo SampleDevice() => new()
    {
        DeviceId = "9f2c4b7a8e1d4c6fb0a3d5e7c9b1a2f3",
        DisplayName = "客厅电脑",
        Platform = PlatformKind.Windows,
        OsVersion = "10.0.22631.0",
        AppVersion = "0.1.0",
        ProtocolVersion = ProtocolConstants.CurrentVersion,
        CertificateFingerprint = "a1b2c3d4",
        Addresses = ["192.168.1.10", "10.0.0.5"],
        Port = 47110,
        Capabilities = ["health", "device_info"],
    };

    [Fact]
    public void DeviceInfo_SerializesToCanonicalFixture()
    {
        var json = WcJson.Serialize(SampleDevice());

        Assert.Equal(DeviceInfoFixture, json);
    }

    [Fact]
    public void DeviceInfo_DeserializesFromCanonicalFixture()
    {
        var device = WcJson.DeserializeRequired<DeviceInfo>(DeviceInfoFixture);

        Assert.Equal("9f2c4b7a8e1d4c6fb0a3d5e7c9b1a2f3", device.DeviceId);
        Assert.Equal("客厅电脑", device.DisplayName);
        Assert.Equal(PlatformKind.Windows, device.Platform);
        Assert.Equal(47110, device.Port);
        Assert.Equal(2, device.Addresses.Count);
        Assert.Equal(ProtocolConstants.CurrentVersion, device.ProtocolVersion);
    }

    [Fact]
    public void ChineseText_IsWrittenAsUtf8NotEscaped()
    {
        var json = WcJson.Serialize(SampleDevice());

        Assert.Contains("客厅电脑", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiResult_OmitsNullFields()
    {
        var result = ApiResult<DeviceInfo>.Success(SampleDevice(), "trace-1", DateTimeOffset.UnixEpoch);

        var json = WcJson.Serialize(result);

        Assert.DoesNotContain("\"error\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ok\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"traceId\":\"trace-1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiResult_Failure_CarriesErrorAndNoData()
    {
        var result = ApiResult<DeviceInfo>.Failure(
            ErrorCodes.NotFound, "接口不存在。", "trace-2", DateTimeOffset.UnixEpoch);

        var json = WcJson.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.False(root.TryGetProperty("data", out _));
        Assert.Equal(ErrorCodes.NotFound, root.GetProperty("error").GetProperty("code").GetString());
        Assert.False(root.GetProperty("error").GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public void ApiResult_CarriesProtocolEnvelopeFields()
    {
        var result = ApiResult<int>.Success(1, "trace-3", DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

        var json = WcJson.Serialize(result);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(ProtocolConstants.CurrentVersion, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(1_700_000_000_000, root.GetProperty("serverTimeUnixMs").GetInt64());
        Assert.Equal("trace-3", root.GetProperty("traceId").GetString());
    }

    [Fact]
    public void Platform_IsLowercaseString()
    {
        var json = WcJson.Serialize(SampleDevice());

        Assert.Contains("\"platform\":\"windows\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownProperty_IsIgnored()
    {
        const string json = """{"deviceId":"x","displayName":"y","platform":"android","osVersion":"14","appVersion":"1","protocolVersion":1,"certificateFingerprint":"z","addresses":[],"port":1,"capabilities":[],"futureField":"ignored"}""";

        var device = WcJson.DeserializeRequired<DeviceInfo>(json);

        Assert.Equal("x", device.DeviceId);
        Assert.Equal(PlatformKind.Android, device.Platform);
    }

    [Fact]
    public void HealthInfo_RoundTrips()
    {
        var health = new HealthInfo
        {
            Status = "running",
            Authenticated = true,
            ProtocolVersion = 1,
            UptimeSeconds = 42,
            Port = 47110,
            CertificateFingerprint = "abc",
            DatabaseReady = true,
            ActiveSessions = 0,
            AppVersion = "0.1.0",
            Capabilities = ["health"],
        };

        var json = WcJson.Serialize(health);
        var parsed = WcJson.DeserializeRequired<HealthInfo>(json);

        Assert.Equal(health.Status, parsed.Status);
        Assert.True(parsed.Authenticated);
        Assert.Equal(health.ProtocolVersion, parsed.ProtocolVersion);
        Assert.Equal(health.UptimeSeconds, parsed.UptimeSeconds);
        Assert.True(parsed.DatabaseReady!.Value);
        Assert.Single(parsed.Capabilities!);
    }

    [Fact]
    public void HealthInfo_AnonymousOmitsOperationalDetail()
    {
        var health = new HealthInfo
        {
            Status = "running",
            Authenticated = false,
            ProtocolVersion = 1,
            Port = 47110,
            CertificateFingerprint = "abc",
            AppVersion = "0.1.0",
        };

        var json = WcJson.Serialize(health);

        Assert.DoesNotContain("\"uptimeSeconds\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"databaseReady\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"activeSessions\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"capabilities\"", json, StringComparison.Ordinal);
        Assert.Contains("\"port\":47110", json, StringComparison.Ordinal);

        var parsed = WcJson.DeserializeRequired<HealthInfo>(json);
        Assert.Null(parsed.UptimeSeconds);
        Assert.Null(parsed.DatabaseReady);
    }

    [Fact]
    public void ErrorResponse_OptionalDetailsAreOmittedWhenNull()
    {
        var error = new ErrorResponse { Code = "invalid_request", Message = "bad" };

        var json = WcJson.Serialize(error);

        Assert.DoesNotContain("\"details\"", json, StringComparison.Ordinal);
    }
}
