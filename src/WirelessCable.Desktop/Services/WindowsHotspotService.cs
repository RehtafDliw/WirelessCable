using Serilog;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using WirelessCable.Core.Domain;
using WirelessCable.Desktop.Services;

namespace WirelessCable.Desktop.Services;

/// <summary>一次热点开启的结果。</summary>
/// <param name="Address">热点网段上本机的 IPv4 地址（手机要连的地址），拿不到时为 null。</param>
public sealed record HotspotOffer(string Ssid, string Passphrase, string? Address);

/// <summary>热点状态。</summary>
public enum HotspotState
{
    /// <summary>这台机器/这个网络共享方式不支持热点。</summary>
    Unsupported,

    /// <summary>支持但没开。</summary>
    Off,

    /// <summary>已经在广播。</summary>
    On,
}

/// <summary>
/// Windows 移动热点（软 AP）。
///
/// 手机和电脑不在同一个网络时，唯一不需要路由器、不需要云端的办法是电脑自己开热点：
/// 手机连上电脑的热点，就回到同一网段，配对与文件传输都走原来的局域网路径。
///
/// 几条硬约束（都实测过，见 docs/known-issues.md）：
/// <list type="bullet">
/// <item>Windows 移动热点必须有可共享的上游连接（以太网或 Wi-Fi），
///   "纯局域网热点"在 Win11 上不成立；上游断开热点会跟着消失。</item>
/// <item>「无设备连接时自动关闭」默认开启（约 11 分钟），这里用
///   <c>DisableNoConnectionsTimeout</c> 关掉，否则手机还没扫完码热点就没了。</item>
/// <item>API 读不回热点密码，所以 SSID/密码由应用配置并落盘；
///   用户在系统设置里手动改过热点名，下一次扫码会重新配置（已连设备会被踢掉）。</item>
/// </list>
/// </summary>
public sealed class WindowsHotspotService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private NetworkOperatorTetheringManager? _manager;

    /// <summary>最近一次由应用成功开启的热点信息（含密码），没开过为 null。</summary>
    public HotspotOffer? Current { get; private set; }

    /// <summary>热点是应用开的（退出时要关）还是用户自己开的（不要动）。</summary>
    public bool StartedByUs { get; private set; }

    /// <summary>当前的热点状态。任何异常都按"不支持"处理，不让它影响二维码生成。</summary>
    public HotspotState QueryState()
    {
        try
        {
            var manager = ResolveManager();
            if (manager is null)
            {
                return HotspotState.Unsupported;
            }

            return manager.TetheringOperationalState == TetheringOperationalState.On
                ? HotspotState.On
                : HotspotState.Off;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "查询热点状态失败");
            return HotspotState.Unsupported;
        }
    }

    /// <summary>
    /// 确保热点开着并返回手机可用的连接信息。
    ///
    /// 已经在广播时不重启（重启会踢掉手机），只把 SSID/密码补回来；
    /// 密码来自 settings.json，缺失时无法得知系统里那个热点的密码，只能重新配置。
    /// </summary>
    public async Task<HotspotOffer?> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = ResolveManager();
            if (manager is null)
            {
                Log.Information("这台电脑不支持移动热点（无可用上游连接或系统不支持）");
                return null;
            }

            var settings = DesktopSettingsStore.Load();
            var ssid = HotspotCredentials.ClampSsid(
                string.IsNullOrWhiteSpace(settings.HotspotSsid)
                    ? HotspotCredentials.DeriveSsid(Environment.MachineName)
                    : settings.HotspotSsid);
            var passphrase = HotspotCredentials.EnsurePassphrase(settings.HotspotPassphrase);

            var wasOn = manager.TetheringOperationalState == TetheringOperationalState.On;

            // 只置 true、不清零：第一次扫码开了热点，第二次扫码时热点已经是 On，
            // 若这时把标记改回 false，退出应用就不会关掉它。
            StartedByUs |= !wasOn;

            // 开热点之前把现有地址记下来：开完后"新出现的那个"就是热点网卡地址。
            // 这比按网段猜可靠——用户可能改过热点网段（注册表），也可能上一轮热点还开着。
            var baseline = SnapshotAddresses();

            var current = TryGetConfiguration(manager);
            var needsConfigure = current is null
                                 || !string.Equals(current.Ssid, ssid, StringComparison.Ordinal)
                                 || !HotspotCredentials.IsValidPassphrase(settings.HotspotPassphrase);

            if (needsConfigure)
            {
                try
                {
                    var configuration = new NetworkOperatorTetheringAccessPointConfiguration
                    {
                        Ssid = ssid,
                        Passphrase = passphrase,
                    };

                    // 沿用系统当前频段：5GHz 快，但个别老手机只认 2.4GHz
                    if (current is not null && current.Band != TetheringWiFiBand.Auto)
                    {
                        configuration.Band = current.Band;
                    }

                    await manager.ConfigureAccessPointAsync(configuration).AsTask(cancellationToken)
                        .ConfigureAwait(false);
                    Log.Information("热点配置已更新 ssid={Ssid}", ssid);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "配置热点失败，继续尝试用现有配置启动");
                }
            }

            if (!wasOn)
            {
                var result = await manager.StartTetheringAsync().AsTask(cancellationToken).ConfigureAwait(false);
                if (result.Status != TetheringOperationStatus.Success)
                {
                    Log.Warning("开启热点返回 status={Status} detail={Detail}", result.Status, result.AdditionalErrorMessage);

                    // OperationInProgress：系统正在从别的状态切过来，网卡马上就好，继续等
                    if (result.Status != TetheringOperationStatus.OperationInProgress)
                    {
                        return null;
                    }
                }
            }

            // 无设备连接时系统会在约 11 分钟后自动关热点（K37）；手机可能还没扫完码。
            try
            {
                if (NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled())
                {
                    NetworkOperatorTetheringManager.DisableNoConnectionsTimeout();
                    Log.Information("已关闭热点「无设备连接时自动关闭」");
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "关闭热点空闲超时失败（可忽略）");
            }

            var address = await WaitForHotspotAddressAsync(baseline, cancellationToken).ConfigureAwait(false);

            // 只持久化我们真正配上去的值
            DesktopSettingsStore.Update(s =>
            {
                s.HotspotSsid = ssid;
                s.HotspotPassphrase = passphrase;
            });

            Current = new HotspotOffer(ssid, passphrase, address);
            Log.Information("热点就绪 ssid={Ssid} address={Address}", ssid, address ?? "<未知>");
            return Current;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "准备热点失败");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>关掉应用自己开的热点。用户手动开的不动。</summary>
    public async Task StopAsync()
    {
        if (!StartedByUs)
        {
            return;
        }

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var manager = ResolveManager();
            if (manager is null || manager.TetheringOperationalState != TetheringOperationalState.On)
            {
                return;
            }

            var result = await manager.StopTetheringAsync().AsTask().ConfigureAwait(false);
            Log.Information("关闭热点 status={Status}", result.Status);
            Current = null;
            StartedByUs = false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "关闭热点失败");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>取热点网段上本机的地址；最多等 6 秒让虚拟网卡拿到 IP。</summary>
    private static async Task<string?> WaitForHotspotAddressAsync(
        HashSet<string> baseline,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var address = FindHotspotAddress(baseline);
            if (address is not null)
            {
                return address;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>当前所有 IPv4 地址（不含回环）。</summary>
    private static HashSet<string> SnapshotAddresses()
        => [.. EnumerateIPv4().Select(c => c.Address)];

    private static List<(NetworkInterface Nic, string Address)> EnumerateIPv4()
    {
        var result = new List<(NetworkInterface, string)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                    || System.Net.IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                result.Add((nic, unicast.Address.ToString()));
            }
        }

        return result;
    }

    /// <summary>
    /// 找热点网卡的地址。
    ///
    /// 三条判据按可靠度排序，**拿不到就返回 null**——绝不能把上游网卡的地址当成热点地址，
    /// 那会让手机连上热点后去访问一个热点网段里根本不存在的 IP。
    /// </summary>
    private static string? FindHotspotAddress(HashSet<string> baseline)
    {
        var candidates = EnumerateIPv4();

        // 1. Windows 移动热点的默认网段，最稳
        var defaultSubnet = candidates
            .FirstOrDefault(c => c.Address.StartsWith("192.168.137.", StringComparison.Ordinal));
        if (defaultSubnet.Address is not null)
        {
            return defaultSubnet.Address;
        }

        // 2. 开热点前后新出现的地址（网段被改过时也能命中）
        var upstream = GetUpstreamAddresses();
        var added = candidates
            .FirstOrDefault(c => !baseline.Contains(c.Address) && !upstream.Contains(c.Address));
        if (added.Address is not null)
        {
            return added.Address;
        }

        // 3. 软 AP 网卡特征：系统对热点网卡的命名是「本地连接* 10」/「Local Area Connection* 10」
        var softAp = candidates.FirstOrDefault(c => IsSoftApAdapter(c.Nic) && !upstream.Contains(c.Address));
        return softAp.Address;
    }

    private static bool IsSoftApAdapter(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
        {
            return false;
        }

        var name = nic.Name;
        var description = nic.Description;

        return name.Contains('*')
               || description.Contains("#2", StringComparison.Ordinal)
               || description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>有默认路由的那个地址算上游（热点地址一定不是上游）。</summary>
    private static HashSet<string> GetUpstreamAddresses()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // NetworkAdapter 只暴露 AdapterId，拿不到 IP；改用"能出网的那张网卡"启发式：
            // 用 UDP connect 取本机源地址，它就是系统按路由表选中的上游地址。
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("223.5.5.5"), 53));
            if (socket.LocalEndPoint is System.Net.IPEndPoint local)
            {
                result.Add(local.Address.ToString());
            }
        }
        catch (SocketException)
        {
            // 没有默认路由（纯内网）：只剩热点地址，交给上层兜底
        }

        return result;
    }

    private static NetworkOperatorTetheringAccessPointConfiguration? TryGetConfiguration(
        NetworkOperatorTetheringManager manager)
    {
        try
        {
            return manager.GetCurrentAccessPointConfiguration();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取热点配置失败（按未配置处理）");
            return null;
        }
    }

    private NetworkOperatorTetheringManager? ResolveManager()
    {
        if (_manager is not null)
        {
            return _manager;
        }

        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null)
        {
            // 没有上游连接时 Windows 也不允许开热点（K38）
            return null;
        }

        var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
        if (capability != TetheringCapability.Enabled)
        {
            Log.Information("热点能力不可用 capability={Capability}", capability);
            return null;
        }

        _manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        return _manager;
    }
}
