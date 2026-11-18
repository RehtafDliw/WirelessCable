using System.Security.Cryptography;
using WirelessCable.Core.Abstractions;

namespace WirelessCable.Infrastructure.Security;

/// <summary>
/// DPAPI 实现（CurrentUser 作用域）。只有当前 Windows 用户能解密，换用户/换机器均失败。
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
    {
        if (plaintext.IsEmpty)
        {
            throw new ArgumentException("明文不能为空。", nameof(plaintext));
        }

        return ProtectedData.Protect(plaintext.ToArray(), entropy.ToArray(), DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> entropy)
    {
        if (ciphertext.IsEmpty)
        {
            throw new ArgumentException("密文不能为空。", nameof(ciphertext));
        }

        return ProtectedData.Unprotect(ciphertext.ToArray(), entropy.ToArray(), DataProtectionScope.CurrentUser);
    }
}
