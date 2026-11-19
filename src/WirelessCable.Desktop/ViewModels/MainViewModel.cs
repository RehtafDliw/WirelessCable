using Serilog;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WirelessCable.Core.Abstractions;
using WirelessCable.Core.Diagnostics;
using WirelessCable.Infrastructure.Certificates;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server;
using WirelessCable.Storage.Entities;
using WirelessCable.Server.Identity;
using WirelessCable.Storage.Repositories;
using WirelessCable.Core.Domain;
using WirelessCable.Desktop.Services;
using WirelessCable.Desktop.ViewModels;

namespace WirelessCable.Desktop.ViewModels;

/// <summary>主窗口视图模型：承载服务运行态、导航与设备列表。</summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly WirelessCableServer _server;
    private readonly IAppPaths _paths;
    private readonly AppConfigRepository _appConfig;
    private readonly DeviceRepository _devices;
    private readonly WindowsHotspotService _hotspot;
    private readonly DispatcherTimer _timer;

    private object _currentPage;
    private string _pageTitle = "首页";
    private bool _isHome = true;
    private string _serviceTone = "idle";
    private string _serviceStatusText = "服务未启动";
    private string _subtitleText = "本地设备连接服务未启动";
    private string _endpointText = "—";
    private string _fingerprintDisplay = "—";
    private string _uptimeText = "—";
    private string _databaseReadyText = "—";
    private string _deviceSummary = "正在读取…";
    private bool _hasDevices;
    private bool _hasActiveTransfer;

    public MainViewModel(
        WirelessCableServer server,
        IAppPaths paths,
        AppConfigRepository appConfig,
        DeviceRepository devices,
        WindowsHotspotService? hotspot = null)
    {
        _server = server;
        _paths = paths;
        _appConfig = appConfig;
        _devices = devices;
        _hotspot = hotspot ?? new WindowsHotspotService();

        Home = new HomeViewModel(this);
        MyComputer = new MyComputerViewModel(this);
        Devices = new DevicesViewModel(this);
        Transfers = new TransfersViewModel(this);
        Settings = new SettingsViewModel(this);
        SharedDirectories = new SharedDirectoriesViewModel(this);
        PhoneFiles = new PhoneFilesViewModel(this);
        _currentPage = Home;

        NavigateCommand = new RelayCommand(p => Navigate(p as string));
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        CopyFingerprintCommand = new RelayCommand(_ => CopyFingerprint());
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as string));
        CopyTextCommand = new RelayCommand(p => CopyText(p as string));
        ShowPairingQrCommand = new RelayCommand(_ => _ = ShowPairingQrAsync());
        StopHotspotCommand = new RelayCommand(_ => _ = StopHotspotAsync());
        HidePairingQrCommand = new RelayCommand(_ => HidePairingQr());
        DismissIntroCommand = new RelayCommand(_ => DismissIntro());
        DismissPairingPromptCommand = new RelayCommand(_ => DismissPairingPrompt());
        SelectDeviceCommand = new RelayCommand(p => SelectDevice(p as DeviceRowViewModel));
        OpenDeviceCommand = new RelayCommand(p => OpenDevice(p as DeviceRowViewModel));
        GrantDeviceCommand = new RelayCommand(p => _ = GrantDeviceAsync(p as DeviceRowViewModel));
        ToggleDeviceConnectionCommand = new RelayCommand(
            p => _ = ToggleDeviceConnectionAsync(p as DeviceRowViewModel));

        NavItems =
        [
            new NavItemViewModel { Key = "home", Label = "首页", Glyph = "\uE80F" },
            new NavItemViewModel { Key = "computer", Label = "传输目录", Glyph = "\uE7F4" },
            new NavItemViewModel { Key = "transfers", Label = "传输中心", Glyph = "\uE895" },
            new NavItemViewModel { Key = "shared", Label = "共享目录", Glyph = "\uE8B7" },
            new NavItemViewModel { Key = "settings", Label = "设置", Glyph = "\uE713" },
            // 新版界面（WebView2）与旧页并存：点它开独立窗口，不切换右侧内容
            new NavItemViewModel { Key = "webui", Label = "新版界面", Glyph = "\uE774" },
        ];

        SetActiveNav("home");
        LoadSelectedDevice();
        ApprovePairingCommand = new RelayCommand(p => DecidePairing(p as string, approve: true));
        RejectPairingCommand = new RelayCommand(p => DecidePairing(p as string, approve: false));
        RevokeDeviceCommand = new RelayCommand(p => _ = RevokeDeviceAsync(p as string));
        DeleteDeviceCommand = new RelayCommand(p => _ = DeleteDeviceAsync(p as string));
        RenameDeviceCommand = new RelayCommand(p => _ = RenameDeviceAsync(p as string));

        _server.Pairing.Changed += OnPairingChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => ApplySnapshot();
    }

    public HomeViewModel Home { get; }

    /// <summary>「传输目录」：手机上传到这台电脑的文件（按设备分文件夹）。</summary>
    public MyComputerViewModel MyComputer { get; }
    public DevicesViewModel Devices { get; }
    public TransfersViewModel Transfers { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>共享目录管理（阶段 5）。</summary>
    public SharedDirectoriesViewModel SharedDirectories { get; }

    /// <summary>手机授权目录浏览（阶段 5）。</summary>
    public PhoneFilesViewModel PhoneFiles { get; }

    /// <summary>服务端实例。传输页需要直接从引擎取任务。</summary>
    public WirelessCableServer Server => _server;

    /// <summary>接收目录（手机上传落点）。</summary>
    public string SharedDirectory => _server.SharedDirectory;

    public RelayCommand NavigateCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CopyFingerprintCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyTextCommand { get; }
    public RelayCommand ShowPairingQrCommand { get; }

    /// <summary>关掉应用开的电脑热点（用户自己开的不会被关）。</summary>
    public RelayCommand StopHotspotCommand { get; }

    /// <summary>收起二维码（首页点「收起」）。</summary>
    public RelayCommand HidePairingQrCommand { get; }

    /// <summary>关闭首次使用引导。</summary>
    public RelayCommand DismissIntroCommand { get; }

    /// <summary>是否显示首次使用引导（只在第一次启动出现）。</summary>
    public bool IsIntroVisible { get; private set; } = !Services.DesktopSettingsStore.Load().IntroSeen;

    private void DismissIntro()
    {
        IsIntroVisible = false;
        OnPropertyChanged(nameof(IsIntroVisible));
        Services.DesktopSettingsStore.Update(s => s.IntroSeen = true);
    }
    public RelayCommand ApprovePairingCommand { get; }
    public RelayCommand RejectPairingCommand { get; }
    public RelayCommand RevokeDeviceCommand { get; }
    public RelayCommand DeleteDeviceCommand { get; }

    /// <summary>改设备的显示名（只影响本机显示与接收子目录名）。</summary>
    public RelayCommand RenameDeviceCommand { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    public bool IsHome
    {
        get => _isHome;
        private set => SetProperty(ref _isHome, value);
    }

    /// <summary>左侧导航项。激活态由 <see cref="SetActiveNav"/> 维护。</summary>
    public System.Collections.ObjectModel.ObservableCollection<NavItemViewModel> NavItems { get; }

    /// <summary>状态色调：ok / idle / error。控制胶囊配色。</summary>
    public string ServiceTone
    {
        get => _serviceTone;
        private set => SetProperty(ref _serviceTone, value);
    }

    public string ServiceStatusText
    {
        get => _serviceStatusText;
        private set => SetProperty(ref _serviceStatusText, value);
    }

    public string SubtitleText
    {
        get => _subtitleText;
        private set => SetProperty(ref _subtitleText, value);
    }

    public string EndpointText
    {
        get => _endpointText;
        private set => SetProperty(ref _endpointText, value);
    }

    public string FingerprintDisplay
    {
        get => _fingerprintDisplay;
        private set => SetProperty(ref _fingerprintDisplay, value);
    }

    public string UptimeText
    {
        get => _uptimeText;
        private set => SetProperty(ref _uptimeText, value);
    }

    public string DatabaseReadyText
    {
        get => _databaseReadyText;
        private set => SetProperty(ref _databaseReadyText, value);
    }

    public string DeviceSummary
    {
        get => _deviceSummary;
        private set => SetProperty(ref _deviceSummary, value);
    }

    public bool HasDevices
    {
        get => _hasDevices;
        private set => SetProperty(ref _hasDevices, value);
    }

    private string _deviceDetailText = "等待手机连接";

    /// <summary>设备卡副标题：有设备时显示该设备信息，否则显示等待态。</summary>
    public string DeviceDetailText
    {
        get => _deviceDetailText;
        private set => SetProperty(ref _deviceDetailText, value);
    }

    /// <summary>是否有进行中的传输。由传输页的真实任务状态驱动（首页据此显示进度提示）。</summary>
    public bool HasActiveTransfer
    {
        get => _hasActiveTransfer;
        private set => SetProperty(ref _hasActiveTransfer, value);
    }

    public string AppVersionText { get; private set; } = "—";

    public string ProtocolVersionText { get; private set; } = "—";

    private string _deviceNameText = "—";

    /// <summary>本机在协议中的设备名（持久化于 AppConfig）。</summary>
    public string DeviceNameText
    {
        get => _deviceNameText;
        private set => SetProperty(ref _deviceNameText, value);
    }

    public ObservableCollection<DeviceRowViewModel> DeviceRows { get; } = [];

    /// <summary>待处理/已处理的配对请求（本机可见验证码）。</summary>
    public ObservableCollection<PairingRequestRowViewModel> PairingRequests { get; } = [];

    /// <summary>
    /// 仅「等待确认」的配对请求。
    ///
    /// 首页直接用它渲染：请求来了就有验证码和允许/拒绝按钮，用户不必先找到设备页。
    /// 已批准/已过期的不进这个列表——它们在首页没有可做的操作。
    /// </summary>
    public ObservableCollection<PairingRequestRowViewModel> PendingPairingRequests { get; } = [];

    /// <summary>
    /// 当前用于浏览与传输的设备。
    ///
    /// 多设备时必须显式区分：以前恒取 <c>FirstOrDefault()</c>，两台手机配对后
    /// "我的手机"永远读的是列表第一台，另一台的授权目录怎么都看不到。
    /// </summary>
    public DeviceRowViewModel? PrimaryDevice =>
        DeviceRows.FirstOrDefault(d => d.DeviceId == _selectedDeviceId) ?? DeviceRows.FirstOrDefault();

    private string _selectedDeviceId = string.Empty;

    /// <summary>选中的设备 ID；为空时退回列表第一台。</summary>
    public string SelectedDeviceId
    {
        get => _selectedDeviceId;
        private set
        {
            if (!SetProperty(ref _selectedDeviceId, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PrimaryDevice));
            OnPropertyChanged(nameof(SelectedDeviceName));
            MarkActiveDevice();

            // 正停在手机文件页时切设备：必须当场重取根，否则页面还挂着上一台的目录
            if (ReferenceEquals(CurrentPage, PhoneFiles))
            {
                _ = PhoneFiles.RefreshAsync();
            }
        }
    }

    /// <summary>当前设备名；没有设备时给一句可直接显示的话。</summary>
    public string SelectedDeviceName => PrimaryDevice?.DisplayName ?? "未选择设备";

    /// <summary>某台设备现在是否在线（30 秒内有过心跳）。</summary>
    public async Task<bool> IsDeviceOnlineAsync(string deviceId)
    {
        var device = await GetDeviceAsync(deviceId).ConfigureAwait(true);
        return device?.LastSeenUtc is { } seen
               && DateTimeOffset.UtcNow - seen <= TimeSpan.FromSeconds(ProtocolConstants.DeviceOfflineAfterSeconds);
    }

    /// <summary>切换当前设备。</summary>
    public RelayCommand SelectDeviceCommand { get; }

    /// <summary>首页设备行上的「访问」：切到这台设备并打开手机文件页。</summary>
    public RelayCommand OpenDeviceCommand { get; }

    /// <summary>首页设备行上的「授权」：整机或某个共享目录。</summary>
    public RelayCommand GrantDeviceCommand { get; }

    /// <summary>首页设备行上的「断开 / 连接」：只断会话，不动配对与授权。</summary>
    public RelayCommand ToggleDeviceConnectionCommand { get; }

    private string _deviceActionHint = string.Empty;
    private DateTimeOffset _deviceActionHintUntil;

    /// <summary>
    /// 断开/连接之后的一行提示。
    ///
    /// 点「连接」后手机要等下次心跳才接上，不给这句话用户会以为没生效，
    /// 转头去重新配对。几秒后自动消失，不留常驻噪声。
    /// </summary>
    public string DeviceActionHint
    {
        get => _deviceActionHint;
        private set
        {
            if (SetProperty(ref _deviceActionHint, value))
            {
                OnPropertyChanged(nameof(HasDeviceActionHint));
            }
        }
    }

    public bool HasDeviceActionHint => !string.IsNullOrEmpty(_deviceActionHint);

    private PillViewModel _devicePill = PillViewModel.Idle("未配对");

    /// <summary>首页「设备连接」行右侧的状态胶囊。</summary>
    public PillViewModel DevicePill
    {
        get => _devicePill;
        private set => SetProperty(ref _devicePill, value);
    }

    private ImageSource? _qrImage;
    private string _qrStatusText = "未生成";
    private string _hotspotText = string.Empty;
    private bool _isHotspotActive;
    private bool _pairingQrBusy;
    private string _pairingError = string.Empty;
    private readonly Dictionary<string, StoredDevice> _pairedDevices = new(StringComparer.Ordinal);

    private bool _hasPairingRequests;
    private bool _hasAnyPairingSession;
    private string _lastPromptedRequestId = string.Empty;
    private string _dismissedPromptId = string.Empty;

    private PairingRequestRowViewModel? _pairingPrompt;
    private string _pairingPromptExtra = string.Empty;

    /// <summary>当前弹窗展示的配对请求；为空表示不弹。</summary>
    public PairingRequestRowViewModel? PairingPrompt
    {
        get => _pairingPrompt;
        private set
        {
            if (SetProperty(ref _pairingPrompt, value))
            {
                OnPropertyChanged(nameof(IsPairingPromptVisible));
            }
        }
    }

    public bool IsPairingPromptVisible => _pairingPrompt is not null;

    /// <summary>还有几个请求排队（弹窗只展示第一个）。</summary>
    public string PairingPromptExtra
    {
        get => _pairingPromptExtra;
        private set => SetProperty(ref _pairingPromptExtra, value);
    }

    /// <summary>配对二维码图像，由服务端生成（含一次性随机数）。</summary>
    public ImageSource? QrImage
    {
        get => _qrImage;
        private set => SetProperty(ref _qrImage, value);
    }

    public string QrStatusText
    {
        get => _qrStatusText;
        private set => SetProperty(ref _qrStatusText, value);
    }

    /// <summary>电脑热点名与密码（开启后显示），或开启失败的原因。</summary>
    public string HotspotText
    {
        get => _hotspotText;
        private set => SetProperty(ref _hotspotText, value);
    }

    /// <summary>热点是否由本应用开着（控制「关闭热点」按钮）。</summary>
    public bool IsHotspotActive
    {
        get => _isHotspotActive;
        private set => SetProperty(ref _isHotspotActive, value);
    }

    public string PairingError
    {
        get => _pairingError;
        private set => SetProperty(ref _pairingError, value);
    }

    public bool HasPairingRequests
    {
        get => _hasPairingRequests;
        private set => SetProperty(ref _hasPairingRequests, value);
    }

    /// <summary>
    /// 队列里是否还有任何请求（含已批准/已过期）。
    ///
    /// 与 <see cref="HasPairingRequests"/> 分开：后者只算"等待确认"的。
    /// 设备页的「无待处理请求」占位要按"队列是否为空"判断，否则
    /// 队列里有已批准请求时，占位和请求卡会同时出现。
    /// </summary>
    public bool HasAnyPairingSession
    {
        get => _hasAnyPairingSession;
        private set => SetProperty(ref _hasAnyPairingSession, value);
    }

    public async Task StartAsync()
    {
        ApplySnapshot();
        await _server.StartAsync().ConfigureAwait(true);
        ApplySnapshot();
        await LoadDevicesAsync().ConfigureAwait(true);
        _timer.Start();
        Transfers.Start();

        // 拉起后立刻再取一次，确保指纹/端口已就绪
        ApplySnapshot();
    }

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        Transfers.Stop();
        await _server.StopAsync().ConfigureAwait(true);
    }

    /// <summary>退出前停掉轮询并解除事件订阅。不涉及服务生命周期，可在 UI 线程同步调用。</summary>
    public void Detach()
    {
        _timer.Stop();
        Transfers.Stop();
        _server.Pairing.Changed -= OnPairingChanged;
    }

    /// <summary>取一台已配对且未撤销的设备。发送文件时用。</summary>
    public async Task<WirelessCable.Storage.Entities.StoredDevice?> GetPrimaryDeviceAsync()
    {
        var stored = await _devices.ListAsync().ConfigureAwait(true);

        if (!string.IsNullOrEmpty(_selectedDeviceId))
        {
            var selected = stored.FirstOrDefault(d =>
                d.DeviceId == _selectedDeviceId && d.IsPaired && !d.IsRevoked);

            if (selected is not null)
            {
                return selected;
            }
        }

        // 没显式选过设备时优先给一台**在线**的：
        // 多设备下第一台常常是早就关掉的旧手机，落到它上面等于什么都打不开。
        var online = stored.FirstOrDefault(static d =>
            d.IsPaired && !d.IsRevoked &&
            d.LastSeenUtc is { } seen &&
            DateTimeOffset.UtcNow - seen <= TimeSpan.FromSeconds(ProtocolConstants.DeviceOfflineAfterSeconds));

        return online ?? stored.FirstOrDefault(static d => d.IsPaired && !d.IsRevoked);
    }

    /// <summary>设备 ID → 显示名，用于传输列表里标注来源/去向。</summary>
    /// <summary>
    /// 从缓存里取一台已配对设备（含身份公钥）。
    /// 反向服务建立连接前需要同步判断"这台手机是不是我配对过的"。
    /// </summary>
    public bool TryGetPairedDevice(string deviceId, out StoredDevice? device)
    {
        if (_pairedDevices.TryGetValue(deviceId, out var found) && found.IsPaired && !found.IsRevoked)
        {
            device = found;
            return true;
        }

        device = null;
        return false;
    }

    /// <summary>取设备记录（带令牌与授权信息）。</summary>
    public async Task<WirelessCable.Storage.Entities.StoredDevice?> GetDeviceAsync(string deviceId)
    {
        return await _devices.GetAsync(deviceId).ConfigureAwait(true);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDeviceNamesAsync()
    {
        var stored = await _devices.ListAsync().ConfigureAwait(true);
        return stored.ToDictionary(static d => d.DeviceId, static d => d.DisplayName, StringComparer.Ordinal);
    }

    /// <summary>切换当前设备：所有按设备取数的页面都会跟着变。</summary>
    private void SelectDevice(DeviceRowViewModel? row)
    {
        if (row is null || string.Equals(row.DeviceId, _selectedDeviceId, StringComparison.Ordinal))
        {
            return;
        }

        SelectedDeviceId = row.DeviceId;
        SaveSelectedDevice();

        Log.Information("切换当前设备 {DeviceId} {Name}", row.DeviceId, row.DisplayName);
        _ = RefreshAsync();
    }

    /// <summary>
    /// 首页设备行 →「授权」：先选模式（整机 / 目录），再选档位。
    ///
    /// 数据源与「共享目录」页一致：整机写 Devices.machine_access，
    /// 目录写 SharedDirectoryGrants（按设备一行），两边看到的是同一份。
    /// </summary>
    private async Task GrantDeviceAsync(DeviceRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var directories = await _server.SharedDirectories.ListAsync().ConfigureAwait(true);
            var options = directories
                .Select(d => new Views.DeviceGrantWindow.SharedDirectoryOption(
                    d.DirectoryId, d.DisplayName, d.RootPath))
                .ToList();

            var window = new Views.DeviceGrantWindow(
                row.DisplayName,
                options,
                MachineAccessLevels.Default,
                new Dictionary<string, SharedDirectoryPermissions>())
            {
                Owner = Application.Current?.MainWindow,
            };

            if (window.ShowDialog() != true)
            {
                return;
            }

            if (window.IsMachineMode)
            {
                await _server.Devices
                    .SetMachineAccessAsync(row.DeviceId, window.SelectedMachineLevel)
                    .ConfigureAwait(true);

                Log.Information("设备授权（整机）{Device} {Level}",
                    row.DisplayName, MachineAccessLevels.Describe(window.SelectedMachineLevel));
            }
            else if (window.SelectedDirectory is { } directory)
            {
                await _server.SharedDirectories
                    .SetGrantAsync(directory.DirectoryId, row.DeviceId, window.SelectedFolderPermissions)
                    .ConfigureAwait(true);

                Log.Information("设备授权（目录）{Device} {Directory} {Permissions}",
                    row.DisplayName, directory.DisplayName, window.SelectedFolderPermissions);
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "设备授权失败 {DeviceId}", row.DeviceId);
            Dialogs.Alert("授权失败", ex.Message);
        }
    }

    /// <summary>
    /// 首页设备行 →「断开 / 连接」。
    ///
    /// "断开"不是撤销：配对记录、令牌、目录授权全部保留，只是服务端拒绝这台设备的请求
    /// （423 session_paused）。手机收到后会停下来并提示"在电脑上恢复连接"，
    /// 恢复后它会自己接上，不需要重新扫码配对。
    /// </summary>
    private async Task ToggleDeviceConnectionAsync(DeviceRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var device = await GetDeviceAsync(row.DeviceId).ConfigureAwait(true);
            if (device is null)
            {
                return;
            }

            var pause = !device.SessionPaused;
            await _devices.SetSessionPausedAsync(row.DeviceId, pause).ConfigureAwait(true);

            Log.Information("设备会话{Action}：{Device}", pause ? "断开" : "恢复", row.DisplayName);

            DeviceActionHint = pause
                ? $"已断开「{row.DisplayName}」。配对与授权都保留，点「连接」即可恢复。"
                : $"已恢复「{row.DisplayName}」。手机下次心跳（约 30 秒内）会自动接上，不用碰手机。";
            _deviceActionHintUntil = DateTimeOffset.UtcNow.AddSeconds(8);

            await LoadDevicesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "切换设备连接失败 {DeviceId}", row.DeviceId);
            Dialogs.Alert("操作失败", ex.Message);
        }
    }

    /// <summary>
    /// 首页设备行 →「访问」：选中这台设备，直接进它的目录。
    ///
    /// 「我的手机」不再是侧栏页签：多设备时它只有一个"当前设备"，
    /// 用户得先在别处切换设备再回来，绕。现在从设备列表点名进入。
    /// </summary>
    private void OpenDevice(DeviceRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        SelectDevice(row);
        Navigate("phone");
        PageTitle = "手机文件";
    }

    private void MarkActiveDevice()
    {
        foreach (var row in DeviceRows)
        {
            // 未显式选择时，列表第一台就是实际生效的那台，标记要跟上
            row.IsActive = string.Equals(row.DeviceId, _selectedDeviceId, StringComparison.Ordinal);
        }
    }

    private string SelectedDeviceFile => Path.Combine(_paths.DataDirectory, "selected-device.txt");

    private void LoadSelectedDevice()
    {
        try
        {
            if (File.Exists(SelectedDeviceFile))
            {
                _selectedDeviceId = File.ReadAllText(SelectedDeviceFile).Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取上次选中的设备失败");
        }
    }

    private void SaveSelectedDevice()
    {
        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            File.WriteAllText(SelectedDeviceFile, _selectedDeviceId);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "保存选中的设备失败");
        }
    }

    public async Task RefreshAsync()
    {
        ApplySnapshot();
        await LoadDevicesAsync().ConfigureAwait(true);
    }

    private void Navigate(string? key)
    {
        switch (key)
        {
            case "webui":
                // 独立窗口：React 应用自带侧栏，塞进右侧内容区会出现两条导航。
                // 这里直接 return，不换页、不动高亮（旧页状态保持原样）。
                OpenWebUi();
                return;
            case "computer":
                CurrentPage = MyComputer;
                PageTitle = "传输目录";
                IsHome = false;
                _ = MyComputer.RefreshAsync();
                break;
            case "shared":
                CurrentPage = SharedDirectories;
                PageTitle = "共享目录";
                IsHome = false;
                _ = SharedDirectories.RefreshAsync();
                break;
            case "phone":
                CurrentPage = PhoneFiles;
                PageTitle = "我的手机";
                Log.Information("导航到我的手机：CurrentPage={Type} PhoneFiles={IsNull}",
                    CurrentPage?.GetType().FullName, PhoneFiles is null);
                IsHome = false;
                _ = PhoneFiles.RefreshAsync();
                break;
            case "devices":
                CurrentPage = Devices;
                PageTitle = "设备";
                IsHome = false;
                _ = LoadDevicesAsync();
                break;
            case "transfers":
                CurrentPage = Transfers;
                PageTitle = "传输中心";
                IsHome = false;
                break;
            case "settings":
                CurrentPage = Settings;
                PageTitle = "设置";
                IsHome = false;
                break;
            default:
                CurrentPage = Home;
                PageTitle = "首页";
                IsHome = true;
                break;
        }

        SetActiveNav(key);
    }

    /// <summary>
    /// 高亮当前分区的导航项。
    ///
    /// 设备管理是首页与我的手机里的次级页面，侧栏没有对应项，
    /// 归到所属分区保持高亮——否则整条侧栏都没有选中项，用户会以为自己走丢了。
    /// </summary>
    private WirelessCable.Desktop.Views.WebUiWindow? _webUiWindow;

    /// <summary>
    /// 打开新版界面窗口。地址固定走 loopback（浏览器视角的本机），
    /// 证书指纹取服务端当前那张 —— 指纹不匹配时宿主会拒绝加载，不会"忽略证书错误"。
    /// </summary>
    /// <summary>诊断用：启动后自动打开新版界面窗口（环境变量 WC_OPEN_WEBUI=1）。</summary>
    public void OpenWebUiForDiagnostics() => OpenNewUiWindow();

    /// <summary>
    /// 新版界面窗口是否开着且可见。
    ///
    /// 配对请求到达时，「把提示送到哪个窗口」必须按用户正在看的界面决定：
    /// 以前旧窗口无条件 Show/置前，用户在新界面里扫码配对，一亮请求界面就跳回旧页面。
    /// </summary>
    public bool IsNewUiWindowOpen => _webUiWindow is { IsVisible: true };

    /// <summary>
    /// 把新版界面窗口带到前台（还原最小化 + 置前）。
    /// 返回 false 表示它没开着，调用方自己决定退路。
    /// </summary>
    public bool TryBringNewUiToFront()
    {
        if (_webUiWindow is not { IsVisible: true } window)
        {
            return false;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
        return true;
    }

    /// <summary>
    /// 打开新版界面窗口。返回 false 表示打不开（服务没起来 / WebView2 运行时缺失），
    /// 调用方据此退回旧界面，而不是把用户丢在一个空桌面上。
    /// </summary>
    public bool OpenNewUiWindow(bool maximized = false) => OpenWebUi(maximized);

    private bool OpenWebUi(bool maximized = false)
    {
        if (_webUiWindow is { IsLoaded: true } existing)
        {
            try
            {
                // 叉号只把窗口藏起来（`WebUiWindow.OnClosing` 里 `e.Cancel + Hide`），
                // 这时 `IsLoaded` 仍是 true、`IsVisible` 是 false。
                // 光 `Activate()` 对隐藏窗口没有任何效果——用户点了叉号再双击托盘会"叫不出来"，
                // 旧窗口之所以能回来，是因为它走的是 `Show()`。
                if (!existing.IsVisible)
                {
                    existing.Show();
                }

                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }

                existing.Activate();
                return true;
            }
            catch (InvalidOperationException)
            {
                // 窗口已经进入关闭流程（`Closed` 回调还没跑到），当作不存在，往下重建
                _webUiWindow = null;
            }
        }

        var snapshot = _server.Snapshot();
        if (snapshot.Port is null)
        {
            return false;
        }

        // 运行时缺失时 WebView2 会在窗口初始化阶段抛异常，提前问一次更好：
        // 这样启动流程可以干脆退回旧界面，而不是先弹一个空窗口再关掉。
        try
        {
            if (string.IsNullOrEmpty(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString()))
            {
                Log.Warning("未安装 WebView2 运行时，启动界面退回 WPF 旧版");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "检查 WebView2 运行时失败，启动界面退回 WPF 旧版");
            return false;
        }

        var origin = $"https://127.0.0.1:{snapshot.Port.Value}";
        var window = new WirelessCable.Desktop.Views.WebUiWindow(
            origin,
            _server.Fingerprint ?? string.Empty,
            maximized);

        // 只有旧窗口已经显示时才挂 Owner：WPF 不允许把没显示过的窗口设为 Owner
        // （启动默认走新版界面时旧窗口是隐藏的，这里必须跳过，否则直接抛异常退出）。
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            window.Owner = owner;
        }

        window.Closed += (_, _) => _webUiWindow = null;
        _webUiWindow = window;

        // 新版界面是主力窗口，任务栏角标也要跟着它显示
        Services.TaskbarBadge.Bind(window, this);

        window.Show();
        return true;
    }

    private void SetActiveNav(string? key)
    {
        var group = key switch
        {
            "devices" => "home",
            "phone" => "home",
            null or "" => "home",
            _ => key,
        };

        foreach (var item in NavItems)
        {
            item.IsActive = string.Equals(item.Key, group, StringComparison.Ordinal);
        }
    }

    private void ApplySnapshot()
    {
        // 每行按"最后心跳时间"重算连接状态：设备掉线后界面要跟着变，
        // 不能停在打开页面那一刻的快照上。
        foreach (var row in DeviceRows)
        {
            row.RefreshPresence();
        }

        // 光用内存里的时间戳不够：心跳是手机直接写库的，界面不重新读一次，
        // "已连接"会一直停在打开页面那一刻（真机验收时发现的）。
        RefreshPresenceInBackground();

        var onlineCount = DeviceRows.Count(static d => d.Tone == "ok");
        if (DeviceRows.Count > 0)
        {
            DevicePill = onlineCount > 0
                ? PillViewModel.Ok("已连接")
                : PillViewModel.Idle("断开");

            DeviceSummary = onlineCount > 0
                ? $"{DeviceRows.Count} 台设备 · {onlineCount} 台在线"
                : $"{DeviceRows.Count} 台设备";
        }

        var snapshot = _server.Snapshot();

        (ServiceStatusText, ServiceTone, SubtitleText) = snapshot.State switch
        {
            ServiceState.Running => ("服务运行中", "ok", "本地设备连接服务已启动"),
            ServiceState.Starting => ("服务启动中", "idle", "正在启动本地服务…"),
            ServiceState.Stopping => ("服务停止中", "idle", "正在停止本地服务…"),
            ServiceState.Failed => ("服务启动失败", "error", snapshot.LastError ?? "本地服务启动失败"),
            _ => ("服务未启动", "idle", "本地设备连接服务未启动"),
        };

        EndpointText = snapshot.Port is null
            ? "—"
            : snapshot.Addresses.Count > 0
                ? $"{snapshot.Addresses[0]}:{snapshot.Port}"
                : $"127.0.0.1:{snapshot.Port}";

        FingerprintDisplay = string.IsNullOrEmpty(snapshot.Fingerprint)
            ? "—"
            : ServerCertificateManager.FormatForDisplay(snapshot.Fingerprint);

        UptimeText = FormatUptime(snapshot.UptimeSeconds);
        DatabaseReadyText = snapshot.DatabaseReady ? "正常" : "不可用";
        AppVersionText = snapshot.AppVersion;
        ProtocolVersionText = $"v{snapshot.ProtocolVersion}";

        OnPropertyChanged(nameof(AppVersionText));
        OnPropertyChanged(nameof(ProtocolVersionText));

        HasActiveTransfer = Transfers.HasActive;

        if (HasDeviceActionHint && DateTimeOffset.UtcNow > _deviceActionHintUntil)
        {
            DeviceActionHint = string.Empty;
        }
    }

    private DateTimeOffset _lastPresenceRead = DateTimeOffset.MinValue;
    private bool _presenceReadInFlight;

    /// <summary>
    /// 每 3 秒从库里重读一次在线状态。
    ///
    /// 心跳是手机直接写库的，界面手里的 LastSeenUtc 只是打开页面时的快照；
    /// 不重新读，手机明明连着、首页却一直写"断开"（除非重新进页面）。
    /// 只更新 Presence，不重建列表——重建会打断选中态并闪烁。
    /// </summary>
    private void RefreshPresenceInBackground()
    {
        var now = DateTimeOffset.UtcNow;
        if (_presenceReadInFlight ||
            DeviceRows.Count == 0 ||
            now - _lastPresenceRead < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastPresenceRead = now;
        _presenceReadInFlight = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var stored = await _devices.ListAsync().ConfigureAwait(false);
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null)
                {
                    return;
                }

                await dispatcher.InvokeAsync(() =>
                {
                    foreach (var row in DeviceRows)
                    {
                        var device = stored.FirstOrDefault(d =>
                            string.Equals(d.DeviceId, row.DeviceId, StringComparison.Ordinal));

                        if (device is not null)
                        {
                            row.SetPresence(device.LastSeenUtc, device.IsRevoked, device.SessionPaused);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "刷新设备在线状态失败");
            }
            finally
            {
                _presenceReadInFlight = false;
            }
        });
    }

    private void OnPairingChanged(object? sender, EventArgs e)    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RefreshPairing();
            _ = LoadDevicesAsync();
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            RefreshPairing();
            _ = LoadDevicesAsync();
        });
    }

    /// <summary>刷新配对队列。验证码只在这里（本机界面）出现，绝不进入网络响应。</summary>
    private void RefreshPairing()
    {
        PairingRequests.Clear();
        PendingPairingRequests.Clear();

        foreach (var session in _server.Pairing.GetSessions())
        {
            var (stateText, tone, canDecide) = session.State switch
            {
                PairingStates.Pending => ("等待确认", "idle", true),
                PairingStates.Approved => ("已批准，等待手机确认", "ok", false),
                PairingStates.Completed => ("已完成", "ok", false),
                PairingStates.Rejected => ("已拒绝", "error", false),
                PairingStates.Expired => ("已过期", "error", false),
                PairingStates.Locked => ("已锁定", "error", false),
                _ => (session.State, "idle", false),
            };

            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(session.ExpiresAtUnixMs).ToLocalTime();
            var remain = expiresAt - DateTimeOffset.Now;

            var row = new PairingRequestRowViewModel
            {
                RequestId = session.RequestId,
                DeviceName = session.DeviceName,
                PlatformText = DeviceRowViewModel.DescribePlatform(session.Platform),
                Code = session.Code,
                StateText = stateText,
                StatusText = canDecide ? "待处理" : stateText,
                Tone = tone,
                CanDecide = canDecide,
                ExpiresText = remain > TimeSpan.Zero ? $"{Math.Ceiling(remain.TotalSeconds)} 秒后失效" : "已失效",
            };

            PairingRequests.Add(row);

            if (canDecide)
            {
                PendingPairingRequests.Add(row);
            }
        }

        HasPairingRequests = PendingPairingRequests.Count > 0;
        HasAnyPairingSession = PairingRequests.Count > 0;

        // 弹窗只盯第一个待确认请求：新请求进来就弹一次，避免同一个请求反复抢焦点
        var candidate = PendingPairingRequests.FirstOrDefault();
        PairingPrompt = candidate is not null &&
                        string.Equals(candidate.RequestId, _dismissedPromptId, StringComparison.Ordinal)
            ? null
            : candidate;

        PairingPromptExtra = PendingPairingRequests.Count > 1
            ? $"另有 {PendingPairingRequests.Count - 1} 个请求"
            : string.Empty;

        var promptId = PairingPrompt?.RequestId;
        if (promptId is not null && !string.Equals(promptId, _lastPromptedRequestId, StringComparison.Ordinal))
        {
            _lastPromptedRequestId = promptId;
            PairingPromptOpened?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>新配对请求到达时触发，宿主窗口据此把自己置前。</summary>
    public event EventHandler? PairingPromptOpened;

    /// <summary>「稍后」：收起弹窗但保留请求。请求本身还在首页/设备页里等着处理。</summary>
    public RelayCommand DismissPairingPromptCommand { get; }

    private void DismissPairingPrompt()
    {
        _dismissedPromptId = PairingPrompt?.RequestId ?? string.Empty;
        PairingPrompt = null;
    }

    /// <summary>
    /// 生成配对二维码。
    ///
    /// 先确保电脑热点开着：手机和电脑不在同一网络时，这是唯一不依赖路由器、不依赖云端的
    /// 连接路径。热点不可用时不阻塞出码——同一 Wi-Fi 的场景照样能扫。
    /// </summary>
    private async Task ShowPairingQrAsync()
    {
        if (_pairingQrBusy)
        {
            return;
        }

        _pairingQrBusy = true;
        try
        {
            PairingError = string.Empty;

            // 服务没起来时开热点毫无意义，还白白占用无线网卡
            if (_server.Port is null)
            {
                QrImage = null;
                QrStatusText = "服务未就绪";
                return;
            }

            QrStatusText = "正在准备热点…";

            var offer = await _hotspot.EnsureStartedAsync();

            QrHotspotDto? hotspot = null;
            if (offer is null)
            {
                IsHotspotActive = false;
                HotspotText = _hotspot.QueryState() == HotspotState.Unsupported
                    ? "无法开热点：电脑需要一个可共享的网络（网线或 Wi-Fi）"
                    : "电脑热点开启失败，手机可连同一 Wi-Fi";
            }
            else
            {
                IsHotspotActive = true;
                HotspotText = $"备用热点 {offer.Ssid} · 密码 {offer.Passphrase}";

                // 地址还没就绪时不写进二维码：手机端会先连热点再按地址访问，
                // 空地址会让它连上热点却不知道连谁。
                if (!string.IsNullOrWhiteSpace(offer.Address))
                {
                    hotspot = new QrHotspotDto
                    {
                        Ssid = offer.Ssid,
                        Passphrase = offer.Passphrase,
                        Address = offer.Address!,
                    };
                }
            }

            var qr = _server.CreatePairingQr(hotspot);
            if (qr is null)
            {
                QrImage = null;
                QrStatusText = "服务未就绪";
                return;
            }

            QrImage = CreateBitmap(qr.PngBytes);
            var remain = TimeSpan.FromMilliseconds(qr.ExpiresAtUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            QrStatusText = remain > TimeSpan.Zero ? $"有效期 {Math.Ceiling(remain.TotalSeconds)} 秒" : "已过期";

            Log.Information(
                "配对二维码已生成 hotspot={Ssid} hotspotAddress={Address}",
                hotspot?.Ssid ?? "<无>",
                hotspot?.Address ?? "<无>");
        }
        catch (Exception ex)
        {
            // 命令是 fire-and-forget，异常漏出去会变成未处理异常直接崩进程
            Log.Warning(ex, "生成配对二维码失败");
            QrImage = null;
            QrStatusText = "生成失败";
        }
        finally
        {
            _pairingQrBusy = false;
        }
    }

    /// <summary>
    /// 新版界面「扫码配对」用：确保热点开着并返回连接信息。
    ///
    /// 复用同一套热点服务（不能开两次），同时更新旧界面的绑定 ——
    /// 两个界面各说一套"热点有没有开"是最容易让人误判的。
    /// </summary>
    public async Task<HotspotOffer?> EnsureHotspotForQrAsync(CancellationToken cancellationToken)
    {
        var offer = await _hotspot.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var dispatcher = Application.Current?.Dispatcher;
        void Apply()
        {
            if (offer is null)
            {
                IsHotspotActive = false;
                HotspotText = _hotspot.QueryState() == HotspotState.Unsupported
                    ? "无法开热点：电脑需要一个可共享的网络（网线或 Wi-Fi）"
                    : "电脑热点开启失败，手机可连同一 Wi-Fi";
            }
            else
            {
                IsHotspotActive = true;
                HotspotText = $"备用热点 {offer.Ssid} · 密码 {offer.Passphrase}";
            }
        }

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            dispatcher.BeginInvoke(Apply);
        }

        return offer;
    }

    private async Task StopHotspotAsync()
    {
        await _hotspot.StopAsync();
        IsHotspotActive = false;
        HotspotText = "热点已关闭";
    }

    /// <summary>退出时收尾：只关应用自己开的热点，用户手动开的保留。</summary>
    public async Task ShutdownHotspotAsync()
    {
        try
        {
            await _hotspot.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出时关闭热点失败");
        }
    }

    private void HidePairingQr()
    {
        QrImage = null;
        QrStatusText = "未生成";
    }

    private void DecidePairing(string? requestId, bool approve)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        var ok = approve
            ? _server.Pairing.TryApprove(requestId, out var error)
            : _server.Pairing.TryReject(requestId, out error);

        PairingError = ok ? string.Empty : error ?? "操作失败";
        RefreshPairing();
    }

    private async Task RevokeDeviceAsync(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        var confirm = Dialogs.Confirm(
            "撤销授权",
            "撤销后这台设备立即失去访问权限，需要重新配对才能再用。",
            confirmText: "撤销",
            danger: true);

        if (!confirm)
        {
            return;
        }

        await _devices.SetRevokedAsync(deviceId, true).ConfigureAwait(true);
        Serilog.Log.Information("已撤销设备授权 device={DeviceId}", deviceId);
        await LoadDevicesAsync().ConfigureAwait(true);
    }

    private async Task DeleteDeviceAsync(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        var confirm = Dialogs.Confirm(
            "删除设备",
            "删除后这台设备的记录与授权一并清除，需要重新配对才能再用。\n传输记录同时清空，已接收的文件保留在原处。",
            confirmText: "删除",
            danger: true);

        if (!confirm)
        {
            return;
        }

        // 先清传输记录再删设备：记录按 device_id 关联，设备没了就再也找不到这些行。
        // 只删数据库行与半截临时文件，接收目录里的文件属于用户，原样保留。
        var purged = await _server.Transfers.PurgeDeviceRecordsAsync(deviceId).ConfigureAwait(true);

        await _devices.DeleteAsync(deviceId).ConfigureAwait(true);
        Serilog.Log.Information("已删除设备 device={DeviceId} 清理传输记录={Count}", deviceId, purged);

        await LoadDevicesAsync().ConfigureAwait(true);
        await Transfers.RefreshAsync().ConfigureAwait(true);
    }
    /// <summary>重命名已配对设备。名字同时决定手机上传文件的接收子目录。</summary>
    private async Task RenameDeviceAsync(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        var device = await _devices.GetAsync(deviceId).ConfigureAwait(true);
        if (device is null)
        {
            return;
        }

        var dialog = new WirelessCable.Desktop.Views.InputDialog("设备名称", device.DisplayName);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }

        var name = dialog.Value!.Trim();
        if (name.Length > 64)
        {
            name = name[..64];
        }

        await _devices.SetDisplayNameAsync(deviceId, name).ConfigureAwait(true);
        Serilog.Log.Information("设备已重命名 device={DeviceId} name={Name}", deviceId, name);
        await LoadDevicesAsync().ConfigureAwait(true);
    }

    private static ImageSource CreateBitmap(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var deviceName = await _appConfig.GetAsync(DeviceIdentityService.DisplayNameKey).ConfigureAwait(true);
            DeviceNameText = string.IsNullOrWhiteSpace(deviceName) ? "—" : deviceName;

            var stored = await _devices.ListAsync().ConfigureAwait(true);

            // 反向服务连接前要同步拿到设备身份公钥，这里留一份缓存
            _pairedDevices.Clear();
            foreach (var device in stored)
            {
                _pairedDevices[device.DeviceId] = device;
            }

            DeviceRows.Clear();
            foreach (var device in stored)
            {
                var row = new DeviceRowViewModel
                {
                    DeviceId = device.DeviceId,
                    DisplayName = device.DisplayName,
                    PlatformText = DeviceRowViewModel.DescribePlatform(device.Platform),
                };

                // 列表里本来就只有已配对的设备，"已配对"是废话；用户要看的是现在通不通
                row.SetPresence(device.LastSeenUtc, device.IsRevoked, device.SessionPaused);
                DeviceRows.Add(row);
            }

            MarkActiveDevice();

            HasDevices = DeviceRows.Count > 0;
            var onlineCount = DeviceRows.Count(static d => d.Tone == "ok");

            DeviceSummary = DeviceRows.Count == 0
                ? "无设备"
                : onlineCount > 0
                    ? $"{DeviceRows.Count} 台设备 · {onlineCount} 台在线"
                    : $"{DeviceRows.Count} 台设备";

            DevicePill = onlineCount > 0
                ? PillViewModel.Ok("已连接")
                : PillViewModel.Idle("断开");

            DeviceDetailText = DeviceRows.Count > 0
                ? DeviceRows[0].Detail
                : "等待手机连接";

            OnPropertyChanged(nameof(PrimaryDevice));
        }
        catch (Exception ex)
        {
            DeviceSummary = "设备列表读取失败";
            Serilog.Log.Error(ex, "读取设备列表失败");
        }
    }

    private void CopyFingerprint()
    {
        var fingerprint = _server.Fingerprint;
        if (string.IsNullOrEmpty(fingerprint))
        {
            return;
        }

        TrySetClipboard(ServerCertificateManager.FormatForDisplay(fingerprint));
    }

    private void CopyText(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            TrySetClipboard(text);
        }
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 剪贴板被其他进程占用时忽略，不影响主流程
        }
    }

    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "打开目录失败 {@Path}", path);
        }
    }

    private static string FormatUptime(long seconds)
    {
        if (seconds <= 0)
        {
            return "—";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒"
                : $"{span.Seconds} 秒";
    }
}
