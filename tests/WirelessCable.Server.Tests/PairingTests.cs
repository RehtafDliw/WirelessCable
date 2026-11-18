using System.Security.Cryptography;
using WirelessCable.Infrastructure.Security;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Identity;
using WirelessCable.Server.Pairing;
using WirelessCable.Server.Security;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>配对测试台：真实 SQLite + 真实密码学 + 可控时钟。</summary>
internal sealed class PairingHarness : IDisposable
{
    public PairingHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "wc-pairing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Database = new SqliteDatabaseFactory(Path.Combine(Root, "test.db"));
        SchemaInitializer.Initialize(Database);
        Devices = new DeviceRepository(Database);
        Service = new PairingService(Devices, Qr, new FixedIdentityProvider(), Clock);
    }

    public string Root { get; }
    public SqliteDatabaseFactory Database { get; }
    public DeviceRepository Devices { get; }
    public QrPairingService Qr { get; } = new();
    public FakeTimeProvider Clock { get; } = new();
    public PairingService Service { get; }

    public string CodeOf(string requestId)
        => Service.GetSessions().Single(s => s.RequestId == requestId).Code;

    public void Dispose()
    {
        Service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FixedIdentityProvider : ServerRuntimeIdentityProvider
    {
        public DeviceIdentity GetIdentity() => new("serverdeviceid00000000000000000001", "测试电脑");
    }
}

public sealed class PairingFlowTests
{
    [Fact]
    public async Task FullPairingFlow_IssuesTokenThatAuthenticates()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var begin = harness.Service.Begin(client.BuildRequest(), "127.0.0.1");
        Assert.True(begin.Success, begin.Message);
        var challenge = begin.Challenge!;
        Assert.DoesNotContain("code", challenge.State, StringComparison.OrdinalIgnoreCase);

        var code = harness.CodeOf(challenge.RequestId);
        Assert.Equal(ProtocolConstants.PairingCodeLength, code.Length);

        Assert.True(harness.Service.TryApprove(challenge.RequestId, out var approveError), approveError);

        var outcome = await harness.Service.ConfirmAsync(client.BuildConfirm(challenge, code), CancellationToken.None);
        Assert.True(outcome.Success, outcome.Message);

        var token = client.UnsealToken(outcome.Response!, challenge.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(token));

        var authenticator = new DeviceAuthenticator(harness.Devices);
        var auth = await authenticator.AuthenticateAsync($"Bearer {token}");

        Assert.Equal(DeviceAuthStatus.Ok, auth.Status);
        Assert.Equal(client.DeviceId, auth.Device!.DeviceId);
        Assert.True(auth.Device.IsPaired);
        Assert.False(auth.Device.IsRevoked);
    }

    [Fact]
    public async Task ConfirmWithoutApproval_IsRejected()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var begin = harness.Service.Begin(client.BuildRequest(), "127.0.0.1");
        var challenge = begin.Challenge!;
        var code = harness.CodeOf(challenge.RequestId);

        var outcome = await harness.Service.ConfirmAsync(client.BuildConfirm(challenge, code), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCodes.PairingNotApproved, outcome.ErrorCode);
    }

    [Fact]
    public async Task RejectedRequest_CannotBeConfirmed()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var begin = harness.Service.Begin(client.BuildRequest(), "127.0.0.1");
        var challenge = begin.Challenge!;
        var code = harness.CodeOf(challenge.RequestId);

        Assert.True(harness.Service.TryReject(challenge.RequestId, out _));

        var outcome = await harness.Service.ConfirmAsync(client.BuildConfirm(challenge, code), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCodes.PairingNotApproved, outcome.ErrorCode);
    }

    [Fact]
    public async Task WrongCode_LocksAfterFiveAttempts()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var begin = harness.Service.Begin(client.BuildRequest(), "127.0.0.1");
        var challenge = begin.Challenge!;
        harness.Service.TryApprove(challenge.RequestId, out _);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var failed = await harness.Service.ConfirmAsync(
                client.BuildConfirm(challenge, "000000", useWrongCode: true), CancellationToken.None);

            Assert.False(failed.Success);
            Assert.Equal(ErrorCodes.PairingCodeInvalid, failed.ErrorCode);
        }

        var locked = await harness.Service.ConfirmAsync(
            client.BuildConfirm(challenge, "000000", useWrongCode: true), CancellationToken.None);

        Assert.False(locked.Success);
        Assert.Equal(ErrorCodes.PairingAttemptsExceeded, locked.ErrorCode);

        // 锁定后即便码正确也不再放行
        var code = harness.CodeOf(challenge.RequestId);
        var afterLock = await harness.Service.ConfirmAsync(client.BuildConfirm(challenge, code), CancellationToken.None);
        Assert.False(afterLock.Success);
        Assert.Equal(ErrorCodes.PairingAttemptsExceeded, afterLock.ErrorCode);
    }

    [Fact]
    public async Task ExpiredRequest_IsRejected()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var begin = harness.Service.Begin(client.BuildRequest(), "127.0.0.1");
        var challenge = begin.Challenge!;
        var code = harness.CodeOf(challenge.RequestId);
        harness.Service.TryApprove(challenge.RequestId, out _);

        harness.Clock.Advance(TimeSpan.FromSeconds(ProtocolConstants.PairingTtlSeconds + 1));

        var outcome = await harness.Service.ConfirmAsync(client.BuildConfirm(challenge, code), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCodes.PairingExpired, outcome.ErrorCode);
    }

    [Fact]
    public async Task MultipleDevices_AreHandledIndependently()
    {
        using var harness = new PairingHarness();
        using var phoneA = new PairingSimulator { DeviceName = "手机A" };
        using var phoneB = new PairingSimulator { DeviceName = "手机B" };

        var beginA = harness.Service.Begin(phoneA.BuildRequest(), "10.0.0.2");
        var beginB = harness.Service.Begin(phoneB.BuildRequest(), "10.0.0.3");

        var challengeA = beginA.Challenge!;
        var challengeB = beginB.Challenge!;

        Assert.NotEqual(challengeA.RequestId, challengeB.RequestId);
        Assert.NotEqual(harness.CodeOf(challengeA.RequestId), harness.CodeOf(challengeB.RequestId));
        Assert.Equal(2, harness.Service.PendingCount);

        // 只批准 A，B 必须仍然无法确认
        Assert.True(harness.Service.TryApprove(challengeA.RequestId, out _));
        var bBlocked = await harness.Service.ConfirmAsync(
            phoneB.BuildConfirm(challengeB, harness.CodeOf(challengeB.RequestId)), CancellationToken.None);
        Assert.Equal(ErrorCodes.PairingNotApproved, bBlocked.ErrorCode);

        var codeA = harness.CodeOf(challengeA.RequestId);
        var aOk = await harness.Service.ConfirmAsync(phoneA.BuildConfirm(challengeA, codeA), CancellationToken.None);
        Assert.True(aOk.Success, aOk.Message);

        // A 的完成不影响 B：B 随后仍能正常配对
        Assert.True(harness.Service.TryApprove(challengeB.RequestId, out _));
        var codeB = harness.CodeOf(challengeB.RequestId);
        var bOk = await harness.Service.ConfirmAsync(phoneB.BuildConfirm(challengeB, codeB), CancellationToken.None);
        Assert.True(bOk.Success, bOk.Message);

        Assert.Equal(2, await harness.Devices.CountPairedAsync());
    }

    [Fact]
    public async Task SameDevice_NewRequestReplacesOldOne()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var first = harness.Service.Begin(client.BuildRequest(), "127.0.0.1").Challenge!;
        var second = harness.Service.Begin(client.BuildRequest(), "127.0.0.1").Challenge!;

        Assert.Null(harness.Service.GetState(first.RequestId));
        Assert.Equal(PairingStates.Pending, harness.Service.GetState(second.RequestId));
        Assert.Equal(1, harness.Service.PendingCount);

        harness.Service.TryApprove(second.RequestId, out _);
        var code = harness.CodeOf(second.RequestId);
        var outcome = await harness.Service.ConfirmAsync(client.BuildConfirm(second, code), CancellationToken.None);
        Assert.True(outcome.Success, outcome.Message);
    }

    [Fact]
    public void QrNonce_IsSingleUse()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var payload = harness.Qr.CreatePayload("deviceid0001", "电脑", ["192.168.1.5"], 47110, "abcd");
        Assert.False(string.IsNullOrEmpty(payload.Nonce));

        var first = harness.Service.Begin(client.BuildRequest(payload.Nonce), "127.0.0.1");
        Assert.True(first.Success, first.Message);

        using var otherClient = new PairingSimulator();
        var second = harness.Service.Begin(otherClient.BuildRequest(payload.Nonce), "127.0.0.1");

        Assert.False(second.Success);
        Assert.Equal(ErrorCodes.QrNonceInvalid, second.ErrorCode);
    }

    [Fact]
    public async Task RevokedDevice_CanNoLongerAuthenticate()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var begin = harness.Service.Begin(client.BuildRequest(), "127.0.0.1");
        var challenge = begin.Challenge!;
        harness.Service.TryApprove(challenge.RequestId, out _);
        var code = harness.CodeOf(challenge.RequestId);
        var outcome = await harness.Service.ConfirmAsync(client.BuildConfirm(challenge, code), CancellationToken.None);
        Assert.True(outcome.Success, outcome.Message);

        var token = client.UnsealToken(outcome.Response!, challenge.RequestId);
        var authenticator = new DeviceAuthenticator(harness.Devices);

        Assert.Equal(DeviceAuthStatus.Ok, (await authenticator.AuthenticateAsync($"Bearer {token}")).Status);

        await harness.Devices.SetRevokedAsync(client.DeviceId, true);

        var afterRevoke = await authenticator.AuthenticateAsync($"Bearer {token}");
        Assert.Equal(DeviceAuthStatus.Revoked, afterRevoke.Status);
    }

    [Fact]
    public async Task TamperedSignature_IsRejected()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var begin = harness.Service.Begin(client.BuildRequest(), "127.0.0.1");
        var challenge = begin.Challenge!;
        harness.Service.TryApprove(challenge.RequestId, out _);
        var code = harness.CodeOf(challenge.RequestId);

        var valid = client.BuildConfirm(challenge, code);
        var tamperedBytes = Convert.FromBase64String(valid.Signature);
        tamperedBytes[^1] ^= 0xFF;

        var tampered = new PairingConfirmRequestDto
        {
            RequestId = valid.RequestId,
            Confirmation = valid.Confirmation,
            Signature = Convert.ToBase64String(tamperedBytes),
        };

        var outcome = await harness.Service.ConfirmAsync(tampered, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCodes.SignatureInvalid, outcome.ErrorCode);
    }
}

public sealed class PairingCryptoVectorTests
{
    /// <summary>转录哈希的字节布局必须与 Android 端逐字节一致（文档 docs/protocol.md §10）。</summary>
    [Fact]
    public void TranscriptHash_MatchesCrossLanguageConstant()
    {
        var transcript = PairingCrypto.BuildTranscript(new PairingTranscript(
            "9f2c4b7a8e1d4c6fb0a3d5e7c9b1a2f3",
            "CLIENT_EPHEMERAL_KEY_BASE64",
            "SERVER_EPHEMERAL_KEY_BASE64",
            "CLIENT_NONCE_BASE64",
            "SERVER_NONCE_BASE64"));

        var hex = Convert.ToHexString(transcript).ToLowerInvariant();

        Assert.Equal("e70d3db3b61f466f1af5568a486d4362bdfc4d20af758d60049594ef40d4a6e3", hex);
    }

    /// <summary>RFC 5869 Test Case 1（SHA-256），确认我们使用的 HKDF 参数语义正确。</summary>
    [Fact]
    public void Hkdf_MatchesRfc5869TestCase1()
    {
        var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");

        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 42, salt, info);

        Assert.Equal(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865",
            Convert.ToHexString(okm).ToLowerInvariant());
    }

    [Fact]
    public void Confirmation_UsesDocumentedLabel()
    {
        var key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");

        var actual = PairingCrypto.ComputeConfirmation(key, "123456");
        var expected = HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("confirm:123456"));

        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
    }

    /// <summary>
    /// 校验码由配对密钥派生，两端必须算出同一个数字。
    /// Android 端 PairingCryptoTest.sasCodeMatchesCrossLanguageVector 用同一组向量。
    /// </summary>
    [Fact]
    public void SasCode_MatchesCrossLanguageVector()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        Assert.Equal("640833", PairingCrypto.DeriveSasCode(key));
    }

    [Fact]
    public void SasCode_IsAlwaysSixDigits()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var key = Enumerable.Range(0, 32).Select(i => (byte)(i * seed + 7)).ToArray();
            var code = PairingCrypto.DeriveSasCode(key);

            Assert.Equal(6, code.Length);
            Assert.All(code, static c => Assert.True(char.IsAsciiDigit(c)));
        }
    }

    /// <summary>
    /// 配对流程端到端必须仍然成立：手机拿到的码与电脑界面显示的码是同一个，
    /// 用它算出的确认值服务端才认。这条测试替代了"用户手输码"这一步。
    /// </summary>
    [Fact]
    public async Task DerivedCode_IsWhatDesktopShowsAndClientUses()
    {
        using var harness = new PairingHarness();
        using var client = new PairingSimulator();

        var begin = harness.Service.Begin(client.BuildRequest(), "127.0.0.1");
        var challenge = begin.Challenge!;
        var code = harness.CodeOf(challenge.RequestId);

        // 客户端从自己协商出的密钥派生，与服务端展示的码一致
        Assert.Equal(code, client.DeriveCode(challenge));

        Assert.True(harness.Service.TryApprove(challenge.RequestId, out var error), error);
        var outcome = await harness.Service.ConfirmAsync(client.BuildConfirm(challenge, code), CancellationToken.None);

        Assert.True(outcome.Success, outcome.Message);
    }
}
