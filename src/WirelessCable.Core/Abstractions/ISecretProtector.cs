namespace WirelessCable.Core.Abstractions;

/// <summary>
/// 本机密钥保护。Windows 实现为 DPAPI（CurrentUser），Android 对应 Keystore。
/// 明文密钥不得落盘，也不得进入日志。
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密明文，返回值可直接写入磁盘。</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy);

    /// <summary>解密 <see cref="Protect"/> 的输出。失败抛 <see cref="System.Security.Cryptography.CryptographicException"/>。</summary>
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy);
}
