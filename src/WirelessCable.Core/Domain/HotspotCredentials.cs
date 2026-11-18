using System.Security.Cryptography;
using System.Text;

namespace WirelessCable.Core.Domain;

/// <summary>
/// 电脑热点的名称与口令规则。
///
/// 为什么由应用决定 SSID 和密码：Windows 移动热点开启之后**读不回密码**
/// （<c>GetCurrentAccessPointConfiguration</c> 只给 SSID 与频段），
/// 应用没法把"系统里现开的热点"的密码交给手机。要么让用户自己去系统设置里抄，
/// 要么由应用配一套自己知道的凭据。这里选后者。
///
/// SSID 带机器后缀，是为了两台电脑同时开热点时手机不会连错（同名热点会认密码）。
/// </summary>
public static class HotspotCredentials
{
    /// <summary>SSID 前缀。</summary>
    public const string SsidPrefix = "WirelessCable";

    /// <summary>密码长度上限：WPA2 口令 8–63 个字符，这里固定 8 位数字，方便手输。</summary>
    public const int PassphraseLength = 8;

    /// <summary>由机器名派生稳定 SSID，形如 <c>WirelessCable-7F2A</c>。</summary>
    public static string DeriveSsid(string? machineName)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(machineName ?? string.Empty), hash);
        return $"{SsidPrefix}-{hash[0]:X2}{hash[1]:X2}";
    }

    /// <summary>生成 8 位数字口令（密码学随机，不是 <c>Random</c>）。</summary>
    public static string GeneratePassphrase()
    {
        var chars = new char[PassphraseLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }

        return new string(chars);
    }

    /// <summary>WPA2 的合法口令范围（8–63 个可打印 ASCII）。只用它挡住空值与脏数据。</summary>
    public static bool IsValidPassphrase(string? passphrase)
        => !string.IsNullOrEmpty(passphrase)
           && passphrase.Length is >= 8 and <= 63
           && passphrase.All(c => c is >= ' ' and < (char)127);

    /// <summary>已有口令可用就沿用（避免每次开热点都换密码踢掉已连设备），否则新生成。</summary>
    public static string EnsurePassphrase(string? existing)
        => IsValidPassphrase(existing) ? existing! : GeneratePassphrase();

    /// <summary>SSID 最大 32 字节，超出部分系统会拒绝；这里做一次兜底截断。</summary>
    public static string ClampSsid(string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return SsidPrefix;
        }

        var text = ssid.Trim();
        return text.Length <= 32 ? text : text[..32];
    }
}
