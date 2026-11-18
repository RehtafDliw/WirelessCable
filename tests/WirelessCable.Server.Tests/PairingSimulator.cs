using System.Security.Cryptography;
using WirelessCable.Infrastructure.Security;
using WirelessCable.Protocol.Dtos;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 模拟 Android 客户端的配对侧实现：临时 ECDH + 长期 ECDSA + 验证码 HMAC + 解封装令牌。
/// 测试里用它跑真实的密码学流程，不 mock 任何一步。
/// </summary>
internal sealed class PairingSimulator : IDisposable
{
    private readonly ECDiffieHellman _ephemeral = PairingCrypto.CreateEphemeralKey();
    private readonly ECDsa _identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private byte[]? _lastPairingKey;

    public string DeviceId { get; } = Guid.NewGuid().ToString("N");

    public string DeviceName { get; init; } = "测试手机";

    public string ClientNonce { get; } = PairingCrypto.GenerateNonce();

    public string EphemeralPublicKey => PairingCrypto.ExportPublicKey(_ephemeral);

    public string IdentityPublicKey => PairingCrypto.ExportPublicKey(_identity);

    public PairingRequestDto BuildRequest(string? qrNonce = null) => new()
    {
        DeviceId = DeviceId,
        DeviceName = DeviceName,
        Platform = "android",
        IdentityPublicKey = IdentityPublicKey,
        EphemeralPublicKey = EphemeralPublicKey,
        QrNonce = qrNonce,
        ClientNonce = ClientNonce,
    };

    /// <summary>客户端按自己的密钥协商结果派生校验码——与电脑界面显示的那个应该相同。</summary>
    public string DeriveCode(PairingChallengeDto challenge)
    {
        using var serverKey = PairingCrypto.ImportPeerKey(challenge.ServerEphemeralPublicKey);
        var shared = PairingCrypto.DeriveSharedSecret(_ephemeral, serverKey);

        var transcript = PairingCrypto.BuildTranscript(new PairingTranscript(
            DeviceId,
            EphemeralPublicKey,
            challenge.ServerEphemeralPublicKey,
            ClientNonce,
            challenge.ServerNonce));

        var pairingKey = PairingCrypto.DerivePairingKey(shared, transcript);
        CryptographicOperations.ZeroMemory(shared);
        return PairingCrypto.DeriveSasCode(pairingKey);
    }

    public PairingConfirmRequestDto BuildConfirm(PairingChallengeDto challenge, string code, bool useWrongCode = false)
    {
        using var serverKey = PairingCrypto.ImportPeerKey(challenge.ServerEphemeralPublicKey);
        var shared = PairingCrypto.DeriveSharedSecret(_ephemeral, serverKey);

        var transcript = PairingCrypto.BuildTranscript(new PairingTranscript(
            DeviceId,
            EphemeralPublicKey,
            challenge.ServerEphemeralPublicKey,
            ClientNonce,
            challenge.ServerNonce));

        _lastPairingKey = PairingCrypto.DerivePairingKey(shared, transcript);
        CryptographicOperations.ZeroMemory(shared);

        var confirmation = PairingCrypto.ComputeConfirmation(_lastPairingKey, useWrongCode ? "000000" : code);
        var signature = _identity.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return new PairingConfirmRequestDto
        {
            RequestId = challenge.RequestId,
            Confirmation = Convert.ToBase64String(confirmation),
            Signature = Convert.ToBase64String(signature),
        };
    }

    public string UnsealToken(PairingConfirmResponseDto response, string requestId)
    {
        if (_lastPairingKey is null)
        {
            throw new InvalidOperationException("尚未完成密钥协商。");
        }

        var sealedBytes = Convert.FromBase64String(response.SealedToken);
        var tokenBytes = PairingCrypto.UnsealToken(_lastPairingKey, requestId, sealedBytes);
        return System.Text.Encoding.UTF8.GetString(tokenBytes);
    }

    public void Dispose()
    {
        _ephemeral.Dispose();
        _identity.Dispose();
    }
}
