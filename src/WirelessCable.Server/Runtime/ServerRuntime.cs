using System.Net;
using WirelessCable.Core.Diagnostics;
using WirelessCable.Infrastructure.Networking;
using WirelessCable.Protocol;
using WirelessCable.Server.Identity;
using WirelessCable.Server.Pairing;

namespace WirelessCable.Server.Runtime;

/// <summary>
/// 服务端可变运行态。由宿主写入，端点与 UI 只读。
/// 所有字段读写都在同一把锁内，避免 UI 线程与请求线程看到半更新状态。
/// </summary>
public sealed class ServerRuntime : ServerRuntimeIdentityProvider
{
    private readonly Lock _gate = new();
    private readonly List<string> _capabilities = [];
    private IReadOnlyList<IPAddress> _addresses = [];
    private ServiceState _state = ServiceState.Stopped;
    private int _port;
    private string _fingerprint = string.Empty;
    private DateTimeOffset _startedAt;
    private bool _databaseReady;
    private string? _lastError;
    private int _activeSessions;
    private DeviceIdentity? _identity;

    public ServerRuntime(ServerOptions options) => Options = options;

    public ServerOptions Options { get; }

    /// <summary>供配对服务读取本机身份（避免配对服务直接依赖宿主）。</summary>
    public DeviceIdentity? GetIdentity() => Identity;

    /// <summary>本机身份，启动时写入。</summary>
    public DeviceIdentity? Identity
    {
        get { lock (_gate) { return _identity; } }
    }

    public void SetIdentity(DeviceIdentity identity)
    {
        lock (_gate) { _identity = identity; }
    }

    /// <summary>活跃会话数（阶段 2 起由会话管理维护）。</summary>
    public int ActiveSessions
    {
        get { lock (_gate) { return _activeSessions; } }
    }

    /// <summary>能力标识，阶段 1 只有健康检查与设备信息。</summary>
    public IReadOnlyList<string> Capabilities
    {
        get { lock (_gate) { return _capabilities.ToArray(); } }
    }

    public void SetCapabilities(IEnumerable<string> capabilities)
    {
        lock (_gate)
        {
            _capabilities.Clear();
            _capabilities.AddRange(capabilities);
        }
    }

    public void MarkStarting(IReadOnlyList<IPAddress> addresses, string fingerprint, bool databaseReady)
    {
        lock (_gate)
        {
            _state = ServiceState.Starting;
            _addresses = addresses;
            _fingerprint = fingerprint;
            _databaseReady = databaseReady;
            _lastError = null;
            _startedAt = DateTimeOffset.UtcNow;
        }
    }

    public void MarkRunning(int port)
    {
        lock (_gate)
        {
            _state = ServiceState.Running;
            _port = port;
            _startedAt = DateTimeOffset.UtcNow;
        }
    }

    public void MarkStopping()
    {
        lock (_gate) { _state = ServiceState.Stopping; }
    }

    public void MarkStopped()
    {
        lock (_gate)
        {
            _state = ServiceState.Stopped;
            _port = 0;
            _activeSessions = 0;
        }
    }

    public void MarkFailed(string error)
    {
        lock (_gate)
        {
            _state = ServiceState.Failed;
            _lastError = error;
        }
    }

    public void IncrementSessions()
    {
        lock (_gate) { _activeSessions++; }
    }

    public void DecrementSessions()
    {
        lock (_gate) { _activeSessions = Math.Max(0, _activeSessions - 1); }
    }

    public ServerSnapshot Snapshot()
    {
        lock (_gate)
        {
            var uptime = _state == ServiceState.Running && _startedAt != default
                ? (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds
                : 0L;

            return new ServerSnapshot(
                _state,
                _port == 0 ? null : _port,
                string.IsNullOrEmpty(_fingerprint) ? null : _fingerprint,
                _startedAt == default ? null : _startedAt,
                uptime,
                _databaseReady,
                _lastError,
                // 地址**每次实时枚举**，不能用启动时那份。
                //
                // 移动办公的典型动作是"开手机热点 → 电脑连上去"，这时电脑的 IP 从上一个
                // 网络换成了 192.168.x.y；缓存值会让二维码（以及 /status、设备端
                // /device/info）一直写着旧地址，手机扫码必然连不上，且日志上看不出问题。
                // 枚举网卡是几毫秒的事，调用频率也低（出码、状态轮询），不值得加缓存。
                LanAddressProvider.GetLocalIPv4Addresses()
                    .Select(static a => a.ToString())
                    .ToArray(),
                Options.AppVersion,
                ProtocolConstants.CurrentVersion);
        }
    }
}
