using System.Security.Cryptography;
using System.Text;

namespace WirelessCable.Infrastructure.Security;

/// <summary>参与会话转录的字段。顺序与拼装规则必须与 Android 端逐字节一致。</summary>
public sealed record PairingTranscript(
    string DeviceId,
    string ClientEphemeralPublicKey,
    string ServerEphemeralPublicKey,
    string ClientNonce,
    string ServerNonce);

/// <summary>
/// 配对密码学。全部使用 BCL 原语，不引入第三方加密库。
///
/// 依据：
/// <list type="bullet">
/// <item>NIST SP 800-56A Rev.3 —— ECDH（P-256），取 X 坐标作为共享秘密（raw secret agreement）；</item>
/// <item>RFC 5869 —— HKDF-SHA256 派生配对密钥；</item>
/// <item>RFC 2104 —— HMAC-SHA256：派生两端一致的六位校验码，并做密钥确认；</item>
/// <item>FIPS 186-5 —— ECDSA P-256 + SHA-256 证明长期身份私钥持有；</item>
/// <item>NIST SP 800-38D —— AES-256-GCM 封装设备令牌。</item>
/// </list>
///
/// 转录哈希的字节布局（两端一致，禁止改动顺序或分隔符）：
/// <code>
/// UTF8( "WC-PAIR-v1\n" + deviceId + "\n" + clientEphemeralPublicKey + "\n"
///       + serverEphemeralPublicKey + "\n" + clientNonce + "\n" + serverNonce )
/// 取 SHA-256。
/// </code>
/// 其中两个公钥都是 Base64(SPKI)，nonce 也是 Base64，避免二进制长度歧义。
/// </summary>
public static class PairingCrypto
{
    public const string TranscriptPrefix = "WC-PAIR-v1";
    public const string HkdfInfo = "WirelessCable/pair/v1";
    public const string ConfirmLabel = "confirm:";
    public const string TokenHkdfInfo = "WirelessCable/token/v1";

    /// <summary>校验码派生标签。两端必须一致。</summary>
    public const string SasLabel = "sas:v1";

    public const int KeySizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    /// <summary>创建 P-256 临时密钥。</summary>
    public static ECDiffieHellman CreateEphemeralKey()
        => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>导出公钥为 Base64(SPKI)。</summary>
    public static string ExportPublicKey(ECDiffieHellman key)
        => Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo());

    public static string ExportPublicKey(ECDsa key)
        => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>导入对端 ECDH 公钥；只接受 P-256，防止曲线混淆。</summary>
    public static ECDiffieHellman ImportPeerKey(string base64Spki)
    {
        var bytes = Convert.FromBase64String(base64Spki);
        var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(bytes, out var read);

        if (read != bytes.Length || peer.KeySize != 256)
        {
            peer.Dispose();
            throw new CryptographicException("仅支持 P-256 临时公钥。");
        }

        return peer;
    }

    /// <summary>导入长期身份公钥（ECDSA P-256）。</summary>
    public static ECDsa ImportIdentityKey(string base64Spki)
    {
        var bytes = Convert.FromBase64String(base64Spki);
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(bytes, out var read);

        if (read != bytes.Length || key.KeySize != 256)
        {
            key.Dispose();
            throw new CryptographicException("仅支持 P-256 身份公钥。");
        }

        return key;
    }

    /// <summary>计算共享秘密（ECDH，取 X 坐标，32 字节）。</summary>
    public static byte[] DeriveSharedSecret(ECDiffieHellman own, ECDiffieHellman peer)
        => own.DeriveRawSecretAgreement(peer.PublicKey);

    /// <summary>构造转录哈希。</summary>
    public static byte[] BuildTranscript(PairingTranscript transcript)
    {
        var text = string.Join('\n',
            TranscriptPrefix,
            transcript.DeviceId,
            transcript.ClientEphemeralPublicKey,
            transcript.ServerEphemeralPublicKey,
            transcript.ClientNonce,
            transcript.ServerNonce);

        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>HKDF-SHA256 派生 32 字节配对密钥。</summary>
    public static byte[] DerivePairingKey(byte[] sharedSecret, byte[] transcript)
        => HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: sharedSecret,
            outputLength: KeySizeBytes,
            salt: transcript,
            info: Encoding.UTF8.GetBytes(HkdfInfo));

    /// <summary>用配对密钥 + 六位验证码计算确认值。</summary>
    public static byte[] ComputeConfirmation(byte[] pairingKey, string code)
        => HMACSHA256.HashData(pairingKey, Encoding.UTF8.GetBytes(ConfirmLabel + code));

    /// <summary>常量时间比较，避免时序侧信道。</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        => CryptographicOperations.FixedTimeEquals(left, right);

    /// <summary>
    /// 用配对密钥封装设备令牌：AES-256-GCM，AAD 为 requestId。
    /// 输出布局：nonce(12) || tag(16) || ciphertext。
    /// </summary>
    public static byte[] SealToken(byte[] pairingKey, string requestId, ReadOnlySpan<byte> token)
    {
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: pairingKey,
            outputLength: KeySizeBytes,
            salt: null,
            info: Encoding.UTF8.GetBytes(TokenHkdfInfo));

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[token.Length];
        var tag = new byte[TagSizeBytes];

        using var gcm = new AesGcm(key, TagSizeBytes);
        gcm.Encrypt(nonce, token, ciphertext, tag, Encoding.UTF8.GetBytes(requestId));

        var result = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSizeBytes);
        ciphertext.CopyTo(result, NonceSizeBytes + TagSizeBytes);
        return result;
    }

    /// <summary>生成六位数字配对码（前导零保留）。保留给旧版客户端的随机码路径。</summary>
    public static string GenerateCode()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>
    /// 从配对密钥派生六位校验码（SAS）。
    ///
    /// 两端各自算出同一个码显示在屏幕上，用户比对而不是手输：
    /// 中间人各自协商出不同密钥，两屏数字必然不同；真正的通行凭证仍是
    /// <see cref="ComputeConfirmation"/>（HMAC 需要密钥，中间人算不出）。
    /// 字节布局与 Android 端 PairingCrypto.deriveSasCode 一致。
    /// </summary>
    public static string DeriveSasCode(ReadOnlySpan<byte> pairingKey)
    {
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(pairingKey, Encoding.UTF8.GetBytes(SasLabel), digest);

        var value = ((uint)digest[0] << 24 | (uint)digest[1] << 16 | (uint)digest[2] << 8 | digest[3])
            % 1_000_000u;

        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 解开 <see cref="SealToken"/> 的输出。客户端（Android）需要等价实现，
    /// 因此这里同时作为测试与文档的参考实现。
    /// </summary>
    public static byte[] UnsealToken(byte[] pairingKey, string requestId, ReadOnlySpan<byte> sealedToken)
    {
        if (sealedToken.Length <= NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("封装令牌长度非法。");
        }

        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: pairingKey,
            outputLength: KeySizeBytes,
            salt: null,
            info: Encoding.UTF8.GetBytes(TokenHkdfInfo));

        var nonce = sealedToken[..NonceSizeBytes];
        var tag = sealedToken.Slice(NonceSizeBytes, TagSizeBytes);
        var ciphertext = sealedToken[(NonceSizeBytes + TagSizeBytes)..];
        var plaintext = new byte[ciphertext.Length];

        using var gcm = new AesGcm(key, TagSizeBytes);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(requestId));
        return plaintext;
    }

    /// <summary>生成 32 字节随机令牌并返回 Base64。</summary>
    public static string GenerateToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>令牌只以 SHA-256 形式落库。</summary>
    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>生成一次性随机数（二维码 nonce / 会话 nonce）。</summary>
    public static string GenerateNonce(int bytes = 16)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
}
