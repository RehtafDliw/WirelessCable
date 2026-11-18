namespace WirelessCable.Storage.Entities;

/// <summary>PairingRecords 表的一行。阶段 2 配对流程使用；配对码只存哈希。</summary>
public sealed class PairingRecord
{
    public required string PairingId { get; init; }
    public required string DeviceId { get; init; }

    /// <summary>配对码的 PBKDF2/哈希，绝不存明文。</summary>
    public required string CodeHash { get; init; }

    public required DateTimeOffset IssuedAtUtc { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public int Attempts { get; init; }

    /// <summary>pending / consumed / expired / locked。</summary>
    public required string Status { get; init; }
}
