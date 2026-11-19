using System.Net;
using System.Net.NetworkInformation;
using Makaretu.Dns;
using Serilog;

namespace WirelessCable.Infrastructure.Networking;

/// <summary>
/// mDNS 服务发布。服务类型 <c>_wirelesscable._tcp</c>，TXT 携带 deviceId、协议版本、
/// 证书指纹与配对套件，SRV 携带实际端口，A 记录只通告真实局域网地址。
///
/// 设计要点：
/// <list type="bullet">
/// <item>实例名带 deviceId 前缀，避免同网段多台电脑重名；</item>
/// <item>端口或指纹变化时重建 ServiceProfile 并重新广播（IP 变化同理）；</item>
/// <item>地址不写死，由 <see cref="LanAddressProvider"/> 按当前网卡挑选，排除虚拟网卡。</item>
/// </list>
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    private readonly MulticastService _mdns = MdnsBus.Instance;
    private ServiceDiscovery? _discovery;
    private readonly Lock _gate = new();
    private readonly Timer _networkDebounce;

    private ServiceProfile? _profile;
    private bool _started;
    private bool _disposed;
    private MdnsAdvertisement? _pending;

    public MdnsAdvertiser()
    {
        _networkDebounce = new Timer(_ => Reannounce(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsRunning
    {
        get { lock (_gate) { return _started; } }
    }

    public string? LastError { get; private set; }

    /// <summary>开始广播。</summary>
    public void Start(
        string deviceId,
        string displayName,
        int port,
        string fingerprint,
        int protocolVersion,
        string serviceType,
        string pairingSuite)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = new MdnsAdvertisement(deviceId, displayName, port, fingerprint, protocolVersion, serviceType, pairingSuite);

            try
            {
                if (!_started)
                {
                    // Stop 会把 _discovery 释放掉，重新启动必须重建，不能用已释放对象
                    _discovery ??= new ServiceDiscovery(_mdns);
                    MdnsBus.Acquire();
                    _started = true;
                    NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                }

                AnnounceLocked();
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error(ex, "mDNS 启动失败");
            }
        }
    }

    /// <summary>端口/指纹变化（例如证书轮换或端口回退）后调用。</summary>
    public void Update(int port, string fingerprint)
    {
        lock (_gate)
        {
            if (_pending is null)
            {
                return;
            }

            _pending = _pending with { Port = port, Fingerprint = fingerprint };

            if (!_started)
            {
                return;
            }

            try
            {
                AnnounceLocked();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Warning(ex, "mDNS 更新失败");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                if (_profile is not null)
                {
                    _discovery?.Unadvertise(_profile);
                }

                _discovery?.Dispose();
                _discovery = null;

                // 通道是共享的：只交还自己的引用，别把别人的也关掉
                MdnsBus.Release();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "停止 mDNS 时出现异常");
            }
            finally
            {
                _started = false;
                _profile = null;
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        Stop();
        _networkDebounce.Dispose();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // 网络切换往往连续触发多次事件，延迟合并
        _networkDebounce.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private void Reannounce()
    {
        lock (_gate)
        {
            if (!_started || _pending is null)
            {
                return;
            }

            try
            {
                // 重建 Profile 才能刷新 A/AAAA 记录（网卡地址可能已变）
                AnnounceLocked();
                Log.Information("网络地址变化，已重新广播 mDNS");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Warning(ex, "网络变化后重新广播失败");
            }
        }
    }

    private void AnnounceLocked()
    {
        if (_pending is null)
        {
            return;
        }

        if (_profile is not null)
        {
            try
            {
                _discovery?.Unadvertise(_profile);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "撤销旧 mDNS 公告失败（可忽略）");
            }
        }

        var instanceName = BuildInstanceName(_pending);
        var profile = CreateProfile(_pending);

        _discovery!.Advertise(profile);
        _profile = profile;

        Log.Information(
            "mDNS 已广播 service={Service} instance={Instance} port={Port} address={Address}",
            _pending.ServiceType,
            instanceName,
            _pending.Port,
            _pending.Addresses is { Count: > 0 } ? string.Join(",", _pending.Addresses) : LanAddressProvider.GetPrimaryIPv4Address());
    }

    /// <summary>
    /// 构造 mDNS 服务记录。抽成纯函数以便单测。
    ///
    /// 地址策略（真机实测得出）：
    /// <list type="bullet">
    /// <item>Android 的 NSD 只返回**第一条** A 记录，而库会按自己的顺序重排地址，
    /// 客户端无法选择；多网卡时曾解析到 Hyper-V 的 172.22.80.1 与电脑 Wi-Fi 的
    /// 172.17.13.108，两者手机都连不上。</item>
    /// <item>因此默认只广播「系统出站源地址」这一条，即路由表真正会用的网卡。</item>
    /// <item>显式传入地址时按传入值广播（测试与特殊场景）。</item>
    /// </list>
    /// 其它网卡的地址仍可通过二维码/手动配对使用，服务端监听所有网卡不受影响。
    /// </summary>
    public static ServiceProfile CreateProfile(MdnsAdvertisement advertisement)
    {
        var instanceName = BuildInstanceName(advertisement);

        IReadOnlyList<IPAddress> addresses;
        if (advertisement.Addresses is { Count: > 0 })
        {
            addresses = advertisement.Addresses;
        }
        else
        {
            var primary = LanAddressProvider.GetPrimaryIPv4Address();
            addresses = primary is null ? [] : [primary];
        }

        var profile = addresses.Count > 0
            ? new ServiceProfile(instanceName, advertisement.ServiceType, (ushort)advertisement.Port, addresses)
            : new ServiceProfile(instanceName, advertisement.ServiceType, (ushort)advertisement.Port);

        profile.AddProperty("id", advertisement.DeviceId);
        // TXT 的 character-string 只接受 ASCII（Makaretu 的 WriteString 会硬校验），
        // 而设备名是「机器名 · 短ID」——那个间隔号 U+00B7 就不是 ASCII。
        // 不编码的后果：广播时侥幸能过，`Unadvertise` 重新序列化就抛
        // `Only ASCII characters are allowed`，反注册失败、手机端缓存残留到 TTL。
        // 用 percent-encoding 传输，手机端 URL-decode 还原；不含 '%' 的旧值解码后仍是原样。
        profile.AddProperty("name", Uri.EscapeDataString(advertisement.DisplayName));
        profile.AddProperty("proto", advertisement.ProtocolVersion.ToString());
        profile.AddProperty("fp", advertisement.Fingerprint);
        profile.AddProperty("suite", advertisement.PairingSuite);
        profile.AddProperty("role", "pc");

        // 多网卡时客户端拿到的 A 记录未必可达，TXT 里给出全部候选地址供其择优
        if (addresses.Count > 0)
        {
            profile.AddProperty("addrs", string.Join(",", addresses));
        }

        return profile;
    }

    /// <summary>
    /// 实例名只用 ASCII：DNS-SD 标签对非 ASCII 会转义成 \23458 这类十进制序列，
    /// 各平台解码行为不一致。可读名称放在 TXT 的 name 字段里。
    /// </summary>
    private static string BuildInstanceName(MdnsAdvertisement announce)
    {
        var suffix = announce.DeviceId.Length >= 8 ? announce.DeviceId[..8] : announce.DeviceId;
        return $"WirelessCable-{suffix}";
    }

    /// <summary>一次广播所需的全部参数。Addresses 为空时按当前网卡自动选取。</summary>
    public sealed record MdnsAdvertisement(
        string DeviceId,
        string DisplayName,
        int Port,
        string Fingerprint,
        int ProtocolVersion,
        string ServiceType,
        string PairingSuite,
        IReadOnlyList<IPAddress>? Addresses = null);
}
