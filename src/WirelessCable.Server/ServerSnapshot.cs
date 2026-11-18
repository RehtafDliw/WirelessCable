using WirelessCable.Core.Diagnostics;

namespace WirelessCable.Server;

/// <summary>服务端运行态快照，供 UI 与健康检查读取真实状态。</summary>
public sealed record ServerSnapshot(
    ServiceState State,
    int? Port,
    string? Fingerprint,
    DateTimeOffset? StartedAtUtc,
    long UptimeSeconds,
    bool DatabaseReady,
    string? LastError,
    IReadOnlyList<string> Addresses,
    string AppVersion,
    int ProtocolVersion)
{
    public static ServerSnapshot Stopped { get; } = new(
        ServiceState.Stopped, null, null, null, 0, false, null, [], AppVersionInfo.Current, 1);
}
