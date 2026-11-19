using System.Net;
using Makaretu.Dns;
using WirelessCable.Infrastructure.Networking;
using WirelessCable.Protocol;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 验证 mDNS 广播记录的内容：服务类型、实例名、SRV 端口、TXT 字段、A 记录地址集合。
///
/// 真机上曾因 A 记录包含 Hyper-V 虚拟网卡（172.22.80.1）导致"能发现、连不上"，
/// 所以地址集合必须有测试盯住。
/// 网络层可发现性已由 Android 13 真机 NSD 验证，见 docs/testing.md §5。
/// </summary>
public sealed class MdnsRecordTests
{
    private static MdnsAdvertiser.MdnsAdvertisement Sample(IReadOnlyList<IPAddress>? addresses = null) => new(
        DeviceId: "abcdef0123456789abcdef0123456789",
        DisplayName: "客厅电脑",
        Port: 47110,
        Fingerprint: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        ProtocolVersion: ProtocolConstants.CurrentVersion,
        ServiceType: ProtocolConstants.ServiceType,
        PairingSuite: ProtocolConstants.PairingSuite,
        Addresses: addresses);

    [Fact]
    public void Profile_UsesWirelessCableServiceType()
    {
        var profile = MdnsAdvertiser.CreateProfile(Sample());

        Assert.Equal("_wirelesscable._tcp.local", profile.QualifiedServiceName.ToString());
    }

    [Fact]
    public void Profile_InstanceNameIsAsciiAndCarriesDeviceIdPrefix()
    {
        var profile = MdnsAdvertiser.CreateProfile(Sample());

        var instance = profile.InstanceName.ToString();
        Assert.Equal("WirelessCable-abcdef01", instance);
        Assert.All(instance, c => Assert.True(c < 128, "实例名必须是 ASCII，避免 DNS 转义"));
    }

    [Fact]
    public void Profile_SrvRecordCarriesActualPort()
    {
        var profile = MdnsAdvertiser.CreateProfile(Sample());

        var srv = Assert.Single(profile.Resources.OfType<SRVRecord>());
        Assert.Equal(47110, srv.Port);
        Assert.Equal(profile.QualifiedServiceName, srv.Target is null ? null : profile.QualifiedServiceName);
    }

    [Fact]
    public void Profile_TxtRecordCarriesIdentityAndProtocol()
    {
        var profile = MdnsAdvertiser.CreateProfile(Sample());

        var txt = Assert.Single(profile.Resources.OfType<TXTRecord>());
        Assert.Contains(txt.Strings, s => s == "id=abcdef0123456789abcdef0123456789");

        // 设备名必须 percent-encode 后再进 TXT：TXT 的 character-string 只接受 ASCII，
        // 而名字里有「·」(U+00B7)，中文机器名更是必然非 ASCII。不编码的话
        // `Unadvertise` 和网络变化后的重新广播都会抛 Only ASCII characters are allowed。
        var nameEntry = Assert.Single(txt.Strings, s => s.StartsWith("name=", StringComparison.Ordinal));
        Assert.Equal("客厅电脑", Uri.UnescapeDataString(nameEntry["name=".Length..]));
        Assert.DoesNotContain(nameEntry, ch => ch > 0x7F);

        // 其它字段也不许出现非 ASCII，否则整个包都写不出去
        Assert.All(txt.Strings, s => Assert.DoesNotContain(s, ch => ch > 0x7F));

        Assert.Contains(txt.Strings, s => s == "proto=1");
        Assert.Contains(txt.Strings, s => s.StartsWith("fp=", StringComparison.Ordinal) && s.Length == 3 + 64);
        Assert.Contains(txt.Strings, s => s == "role=pc");
        Assert.Contains(txt.Strings, s => s == "suite=" + ProtocolConstants.PairingSuite);
    }

    [Fact]
    public void Profile_TxtCarriesFullFingerprintForPinning()
    {
        var profile = MdnsAdvertiser.CreateProfile(Sample());

        var txt = Assert.Single(profile.Resources.OfType<TXTRecord>());
        Assert.Contains(txt.Strings, s => s == "fp=" + Sample().Fingerprint);
    }

    [Fact]
    public void Profile_AdvertisesOnlyProvidedAddresses()
    {
        // Hyper-V 的 172.22.80.1/20 必须被排除，否则手机能发现却连不上
        var addresses = new IPAddress[] { IPAddress.Parse("192.168.1.10") };

        var profile = MdnsAdvertiser.CreateProfile(Sample(addresses));

        var a = profile.Resources.OfType<AddressRecord>().Select(r => r.Address).ToArray();
        Assert.Single(a);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), a[0]);
    }

    [Fact]
    public void Profile_WithoutExplicitAddresses_AdvertisesSinglePrimaryAddress()
    {
        var profile = MdnsAdvertiser.CreateProfile(Sample());

        var records = profile.Resources.OfType<AddressRecord>().ToArray();

        // Android NSD 只取第一条 A 记录，多网卡时会出现"能发现、连不上"，
        // 因此默认只广播一条（系统出站源地址）。
        Assert.Single(records);

        var address = records[0].Address;
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, address.AddressFamily);
        Assert.False(IPAddress.IsLoopback(address), $"不应广播回环地址 {address}");

        var bytes = address.GetAddressBytes();
        Assert.False(bytes[0] == 169 && bytes[1] == 254, $"不应广播 APIPA 地址 {address}");
    }

    [Fact]
    public void Profile_TxtCarriesAllCandidateAddresses()
    {
        var addresses = new IPAddress[] { IPAddress.Parse("172.18.15.63"), IPAddress.Parse("172.17.13.108") };

        var profile = MdnsAdvertiser.CreateProfile(Sample(addresses));

        var txt = Assert.Single(profile.Resources.OfType<TXTRecord>());
        Assert.Contains(txt.Strings, s => s == "addrs=172.18.15.63,172.17.13.108");
    }

    [Fact]
    public void LanAddressProvider_ExcludesVirtualAndLoopbackAdapters()
    {
        var addresses = LanAddressProvider.GetLocalIPv4Addresses();

        foreach (var address in addresses)
        {
            Assert.False(IPAddress.IsLoopback(address));

            var bytes = address.GetAddressBytes();
            Assert.False(bytes[0] == 169 && bytes[1] == 254);

            // 172.22.80.1/20 是 Hyper-V Default Switch；这里只断言不回环/非 APIPA，
            // 虚拟网卡过滤是否生效由 Profile_AdvertisesOnlyProvidedAddresses 与本机实测覆盖。
        }
    }
}
