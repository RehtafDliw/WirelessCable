using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WirelessCable.Infrastructure.Networking;

/// <summary>
/// 局域网 IPv4 地址提供者。用于二维码、直连兜底、mDNS A 记录与证书 SAN。
///
/// 必须排除虚拟网卡：Hyper-V 的 <c>vEthernet (Default Switch)</c> 常见为 172.x/20，
/// 若被当作服务地址广播出去，手机能"发现"却永远连不上（真机实测踩过）。
/// </summary>
public static class LanAddressProvider
{
    /// <summary>虚拟/隧道网卡的特征词，命中即排除。</summary>
    private static readonly string[] VirtualAdapterMarkers =
    [
        "vethernet",
        "hyper-v",
        "vmware",
        "virtualbox",
        "vbox",
        "tap-",
        "tun",
        "wsl",
        "docker",
        "loopback",
        "bluetooth",
        "npcap",
        "pseudo",
        "virbr",
        "wan miniport",
    ];

    /// <summary>返回已启用、非回环、非 APIPA、非虚拟网卡的 IPv4 地址（物理网卡优先）。</summary>
    public static IReadOnlyList<IPAddress> GetLocalIPv4Addresses()
    {
        var physical = new List<IPAddress>();
        var other = new List<IPAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            if (IsVirtual(nic))
            {
                continue;
            }

            var bucket = IsPhysical(nic) ? physical : other;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                if (IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                // 169.254.x.x 为 APIPA，不能用于连接
                var bytes = unicast.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254)
                {
                    continue;
                }

                if (!physical.Contains(unicast.Address) && !other.Contains(unicast.Address))
                {
                    bucket.Add(unicast.Address);
                }
            }
        }

        physical.AddRange(other);
        return physical;
    }

    /// <summary>首选地址（二维码/直连默认值），无可用网卡时返回 null。</summary>
    public static IPAddress? GetPreferredIPv4Address()
        => GetLocalIPv4Addresses().FirstOrDefault();

    /// <summary>
    /// 系统实际会用作出站源地址的那一个地址。
    ///
    /// 做法是对一个外部地址做 UDP <c>Connect</c>（不会真的发包）后读 <c>LocalEndPoint</c>，
    /// 等价于"操作系统按路由表选哪张网卡"。多网卡机器上这比"枚举顺序"可靠得多。
    /// </summary>
    public static IPAddress? GetPrimaryIPv4Address()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("223.5.5.5"), 53));

            if (socket.LocalEndPoint is IPEndPoint local && !IPAddress.IsLoopback(local.Address))
            {
                return local.Address;
            }
        }
        catch (SocketException)
        {
            // 无默认路由（纯内网）时退回枚举顺序
        }

        return GetPreferredIPv4Address();
    }

    private static bool IsVirtual(NetworkInterface nic)
    {
        var name = nic.Name;
        var description = nic.Description;
        var typeName = nic.NetworkInterfaceType.ToString();

        foreach (var marker in VirtualAdapterMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)
                || description.Contains(marker, StringComparison.OrdinalIgnoreCase)
                || typeName.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPhysical(NetworkInterface nic)
        => nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit;
}
