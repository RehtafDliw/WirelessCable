namespace WirelessCable.Desktop.ViewModels;

/// <summary>配对队列中的一行。</summary>
public sealed class PairingRequestRowViewModel
{
    public required string RequestId { get; init; }
    public required string DeviceName { get; init; }
    public required string PlatformText { get; init; }

    /// <summary>六位配对码，仅在本机界面显示。</summary>
    public required string Code { get; init; }

    public required string StateText { get; init; }
    public required string StatusText { get; init; }

    /// <summary>状态色调：ok / idle / error。</summary>
    public required string Tone { get; init; }

    /// <summary>仅 pending 状态允许批准/拒绝。</summary>
    public required bool CanDecide { get; init; }

    public required string ExpiresText { get; init; }
}
