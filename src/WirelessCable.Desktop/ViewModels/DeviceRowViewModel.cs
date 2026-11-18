using WirelessCable.Protocol;

namespace WirelessCable.Desktop.ViewModels;

/// <summary>设备列表中的一行。</summary>
public sealed class DeviceRowViewModel : ViewModelBase
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>平台显示名（Android / Windows …），与最后在线时间拼成副标题。</summary>
    public required string PlatformText { get; init; }

    private string _statusText = string.Empty;
    private string _tone = "idle";

    /// <summary>连接状态文字：已连接 / 断开 / 已撤销。</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>状态色调：ok / idle / error。</summary>
    public string Tone
    {
        get => _tone;
        set => SetProperty(ref _tone, value);
    }

    /// <summary>
    /// 在线判定的窗口，与协议里的"多久没心跳算离线"同一个数（<see cref="ProtocolConstants.DeviceOfflineAfterSeconds"/>）。
    ///
    /// 原来这里写死 90 秒：手机早被杀掉了，电脑端还要再挂一分多钟才肯翻成离线
    /// （现场在另一台电脑上测出来，1.5.59 修）。现在心跳 10 秒一次、窗口 30 秒，
    /// 最坏约 40 秒翻状态。
    /// </summary>
    private static readonly TimeSpan OnlineWindow =
        TimeSpan.FromSeconds(ProtocolConstants.DeviceOfflineAfterSeconds);

    private DateTimeOffset? _lastSeenUtc;
    private bool _revoked;
    private bool _isOnline;

    /// <summary>
    /// 是否在线（30 秒内有过心跳）。
    /// 副标题要不要显示"最后在线"由它决定——在线时那个时间就是刚刚，写出来纯属噪声。
    /// </summary>
    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (SetProperty(ref _isOnline, value))
            {
                OnPropertyChanged(nameof(Detail));
            }
        }
    }

    /// <summary>
    /// 副标题：平台 + 连接信息。
    /// 在线时只留平台名——"已连接"旁边再挂一个刚刷新过的时间没有任何信息量；
    /// 只有离线了，"最后在线"才是用户真正关心的东西。
    /// </summary>
    public string Detail
    {
        get
        {
            if (_revoked)
            {
                return $"{PlatformText} · 授权已撤销";
            }

            if (_paused)
            {
                return $"{PlatformText} · 手动断开，点「连接」恢复";
            }

            if (IsOnline)
            {
                return PlatformText;
            }

            return _lastSeenUtc is { } seen
                ? $"{PlatformText} · 最后在线 {seen.ToLocalTime():MM-dd HH:mm}"
                : $"{PlatformText} · 从未连接";
        }
    }

    private bool _paused;

    /// <summary>用户主动断开了这台设备（配对还在，只是服务端拒绝它的请求）。</summary>
    public bool IsPaused
    {
        get => _paused;
        private set
        {
            if (SetProperty(ref _paused, value))
            {
                OnPropertyChanged(nameof(ConnectionActionText));
                OnPropertyChanged(nameof(Detail));
            }
        }
    }

    /// <summary>行内按钮文字：断开 / 连接。</summary>
    public string ConnectionActionText => _paused ? "连接" : "断开";

    /// <summary>由设备记录写入在线信息，并立即算一次状态。</summary>
    public void SetPresence(DateTimeOffset? lastSeenUtc, bool revoked, bool paused = false)
    {
        var seenChanged = _lastSeenUtc != lastSeenUtc;
        _lastSeenUtc = lastSeenUtc;
        _revoked = revoked;
        IsPaused = paused;
        RefreshPresence();

        if (seenChanged)
        {
            OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>
    /// 按当前时间重算状态。首页每秒调用一次，设备掉线后不会一直挂着"已连接"。
    /// </summary>
    public void RefreshPresence()
    {
        var online = !_revoked &&
                     _lastSeenUtc is { } seen &&
                     DateTimeOffset.UtcNow - seen <= OnlineWindow;

        // 先写在线状态：Detail 依赖它，属性变更事件会把副标题一起刷新
        IsOnline = online;

        // "已断开"要压在"已连接"之上：手动断开是最强的那条状态，
        // 否则设备还没走到 30 秒窗口时会先显示"已连接"，用户以为没断成功。
        StatusText = _revoked
            ? "已撤销"
            : _paused
                ? "已断开"
                : online
                    ? "已连接"
                    : "断开";

        Tone = _revoked ? "error" : _paused ? "idle" : online ? "ok" : "idle";
    }

    private bool _isActive;

    /// <summary>是否为当前选中的设备。多设备时用它给列表项加标记。</summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public static string DescribePlatform(string platform) => platform switch
    {
        "android" => "Android",
        "windows" => "Windows",
        "ios" => "iOS",
        _ => platform,
    };
}
