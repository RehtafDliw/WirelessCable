using WirelessCable.Core.Domain;
using Xunit;

namespace WirelessCable.Core.Tests;

/// <summary>
/// 电脑热点的凭据规则。这些值会出现在二维码里、也会写进 settings.json，
/// 因此锁死三件事：SSID 稳定、口令是 WPA2 合法长度、脏数据不会原样用出去。
/// </summary>
public sealed class HotspotCredentialsTests
{
    [Fact]
    public void DeriveSsid_IsStableAndPrefixed()
    {
        var first = HotspotCredentials.DeriveSsid("DESKTOP-ABC123");
        var second = HotspotCredentials.DeriveSsid("DESKTOP-ABC123");
        var other = HotspotCredentials.DeriveSsid("DESKTOP-XYZ789");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.StartsWith("WirelessCable-", first, StringComparison.Ordinal);
        Assert.Equal("WirelessCable-".Length + 4, first.Length);
        Assert.True(first.Length <= 32);
    }

    [Fact]
    public void DeriveSsid_HandlesNullOrEmptyMachineName()
    {
        var empty = HotspotCredentials.DeriveSsid(null);

        Assert.StartsWith("WirelessCable-", empty, StringComparison.Ordinal);
        Assert.Equal(empty, HotspotCredentials.DeriveSsid(string.Empty));
    }

    [Fact]
    public void GeneratePassphrase_IsEightDigits()
    {
        for (var i = 0; i < 32; i++)
        {
            var passphrase = HotspotCredentials.GeneratePassphrase();

            Assert.Equal(HotspotCredentials.PassphraseLength, passphrase.Length);
            Assert.All(passphrase, c => Assert.InRange(c, '0', '9'));
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("1234 567", true)]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234", false)]
    [InlineData("correct horse battery staple", true)]
    public void IsValidPassphrase_FollowsWpa2Range(string? passphrase, bool expected)
    {
        Assert.Equal(expected, HotspotCredentials.IsValidPassphrase(passphrase));
    }

    [Fact]
    public void EnsurePassphrase_KeepsExistingValueWhenUsable()
    {
        Assert.Equal("87654321", HotspotCredentials.EnsurePassphrase("87654321"));
    }

    [Fact]
    public void EnsurePassphrase_ReplacesUnusableValue()
    {
        var generated = HotspotCredentials.EnsurePassphrase("short");

        Assert.NotEqual("short", generated);
        Assert.True(HotspotCredentials.IsValidPassphrase(generated));
    }

    [Fact]
    public void ClampSsid_TrimsAndCapsAt32()
    {
        Assert.Equal("WirelessCable", HotspotCredentials.ClampSsid("  "));
        Assert.Equal("MySpot", HotspotCredentials.ClampSsid(" MySpot "));

        var longName = new string('x', 40);
        Assert.Equal(32, HotspotCredentials.ClampSsid(longName).Length);
    }
}
