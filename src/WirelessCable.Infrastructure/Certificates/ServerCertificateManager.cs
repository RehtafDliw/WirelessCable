using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WirelessCable.Core.Abstractions;

namespace WirelessCable.Infrastructure.Certificates;

/// <summary>服务端证书与其 SHA-256 指纹。</summary>
/// <param name="Certificate">带私钥的证书。</param>
/// <param name="Fingerprint">SHA-256(DER) 小写十六进制，64 字符。</param>
public sealed record ServerCertificate(X509Certificate2 Certificate, string Fingerprint);

/// <summary>
/// 自签服务端证书的生成与持久化。
///
/// 设计要点：
/// <list type="bullet">
/// <item>ECDSA P-256 + SHA-256，有效期 1 年，剩余不足 30 天自动轮换；</item>
/// <item>PFX 密码随机 32 字节，经 DPAPI(CurrentUser) 加密后单独存放；</item>
/// <item>私钥导入使用 UserKeySet（SChannel 要求可持久化的 CNG 句柄，EphemeralKeySet 会导致握手失败）；</item>
/// <item>指纹只用于"固定信任"，不构成 CA 信任链——正式流程必须由配对双方核对，绝不无条件信任。</item>
/// </list>
/// </summary>
public sealed class ServerCertificateManager
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WirelessCable.ServerCert.v1");
    private static readonly TimeSpan RotationWindow = TimeSpan.FromDays(30);

    private readonly IAppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly Lock _gate = new();

    public ServerCertificateManager(IAppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    private string PfxPath => Path.Combine(_paths.CertificatesDirectory, "server.pfx");
    private string KeyPath => Path.Combine(_paths.CertificatesDirectory, "server.pfx.key");

    /// <summary>取现有证书；不存在或即将过期则重新生成并落盘。</summary>
    public ServerCertificate GetOrCreate(
        IReadOnlyCollection<string> dnsNames,
        IReadOnlyCollection<IPAddress> ipAddresses)
    {
        lock (_gate)
        {
            var existing = TryLoad();
            if (existing is not null && existing.Certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.Add(RotationWindow))
            {
                return existing;
            }

            var created = Create(dnsNames, ipAddresses);
            Save(created);
            return created;
        }
    }

    /// <summary>计算证书指纹：SHA-256 over DER，小写十六进制。</summary>
    public static string ComputeFingerprint(X509Certificate2 certificate)
        => Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();

    /// <summary>把 64 字符指纹格式化为 AB:CD:… 便于人工核对。</summary>
    public static string FormatForDisplay(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint) || fingerprint.Length % 2 != 0)
        {
            return fingerprint;
        }

        var builder = new StringBuilder(fingerprint.Length + fingerprint.Length / 2);
        for (var i = 0; i < fingerprint.Length; i += 2)
        {
            if (builder.Length > 0)
            {
                builder.Append(':');
            }
            builder.Append(fingerprint.AsSpan(i, 2));
        }

        return builder.ToString().ToUpperInvariant();
    }

    private ServerCertificate? TryLoad()
    {
        try
        {
            if (!File.Exists(PfxPath) || !File.Exists(KeyPath))
            {
                return null;
            }

            var protectedKey = File.ReadAllBytes(KeyPath);
            var password = Encoding.UTF8.GetString(_protector.Unprotect(protectedKey, Entropy));
            var pfx = File.ReadAllBytes(PfxPath);

            var certificate = X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                return null;
            }

            return new ServerCertificate(certificate, ComputeFingerprint(certificate));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or FormatException)
        {
            // 损坏或换用户导致 DPAPI 解不开：按需重建，不静默信任旧证书
            return null;
        }
    }

    private ServerCertificate Create(IReadOnlyCollection<string> dnsNames, IReadOnlyCollection<IPAddress> ipAddresses)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=WirelessCable", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);

        foreach (var dns in dnsNames.Where(static d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            san.AddDnsName(dns);
        }

        foreach (var ip in ipAddresses.Where(static ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Distinct())
        {
            san.AddIpAddress(ip);
        }

        request.CertificateExtensions.Add(san.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(365);

        using var selfSigned = request.CreateSelfSigned(notBefore, notAfter);

        // 重新导入一次，确保私钥句柄可用于 SChannel
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pfx = selfSigned.Export(X509ContentType.Pfx, password);
        var certificate = X509CertificateLoader.LoadPkcs12(
            pfx,
            password,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

        return new ServerCertificate(certificate, ComputeFingerprint(certificate));
    }

    private void Save(ServerCertificate certificate)
    {
        Directory.CreateDirectory(_paths.CertificatesDirectory);

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pfx = certificate.Certificate.Export(X509ContentType.Pfx, password);
        var protectedKey = _protector.Protect(Encoding.UTF8.GetBytes(password), Entropy);

        WriteAtomic(PfxPath, pfx);
        WriteAtomic(KeyPath, protectedKey);
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
