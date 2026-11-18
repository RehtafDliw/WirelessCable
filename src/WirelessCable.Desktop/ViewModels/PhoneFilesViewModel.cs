using WirelessCable.Core.Domain;
using WirelessCable.Desktop.Services;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;

namespace WirelessCable.Desktop.ViewModels;

/// <summary>手机目录里的一行。排序规则复用 Core 的 <see cref="ISortableFileEntry"/>。</summary>
public sealed class PhoneFileRowViewModel : ViewModelBase, ISortableFileEntry
{
    private bool _selected;

    public required string RelativePath { get; init; }

    public required string Name { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>原始字节数，排序用。</summary>
    public long SizeBytes { get; init; }

    /// <summary>修改时间（Unix 毫秒），排序用。0 表示未知。</summary>
    public long ModifiedUnixMs { get; init; }

    /// <summary>该条目自己的能力位（手机上报）。0 表示未知，按放行处理。</summary>
    public int Capabilities { get; init; }

    public string SizeText => IsDirectory ? string.Empty : FormatSize(SizeBytes);

    /// <summary>
    /// 类型列。目录写「文件夹」，文件写扩展名（大写）——资源管理器也只给这么多，
    /// 再细的类型名要读注册表，为一个列表列不值当。
    /// </summary>
    public string TypeText
    {
        get
        {
            if (IsDirectory)
            {
                return "文件夹";
            }

            var extension = System.IO.Path.GetExtension(Name);
            return extension.Length > 1 ? extension[1..].ToUpperInvariant() : "文件";
        }
    }

    public string ModifiedText => ModifiedUnixMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(ModifiedUnixMs).ToLocalTime().ToString("MM-dd HH:mm")
        : string.Empty;

    /// <summary>
    /// 一行的主操作。目录是「打开」，文件是「保存到电脑」——
    /// 文件没有"打开"这回事：电脑上没有它的副本，双击也只能先下载。
    /// </summary>
    public string PrimaryActionText => IsDirectory ? "打开" : "保存到电脑";

    public bool CanRunPrimaryAction => FileCapabilityGate.Allows(
        Capabilities,
        IsDirectory ? FileCapabilities.ListDirectory : FileCapabilities.ReadFile);

    public bool CanRename => FileCapabilityGate.Allows(Capabilities, FileCapabilities.Rename);

    public bool CanDelete => FileCapabilityGate.Allows(Capabilities, FileCapabilities.Delete);

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    private bool _isChecked;

    /// <summary>多选勾选。刷新会重建行对象，勾选随之清空——列表变了，旧勾选没有意义。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var units = new[] { "KB", "MB", "GB", "TB" };
        double value = bytes / 1024d;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }
}

/// <summary>手机授权目录里的一行。</summary>
public sealed class PhoneRootRowViewModel : ViewModelBase
{
    private bool _isSelected;

    public required string RootId { get; init; }

    /// <summary>当前选中的根。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public required string DisplayName { get; init; }

    public required string CapabilitiesText { get; init; }

    public required string DeviceName { get; init; }

    /// <summary>根所属设备 id。按 id 定位设备，不按名字——两台手机可以重名。</summary>
    public required string DeviceId { get; init; }

    /// <summary>该根的能力位。</summary>
    public int Capabilities { get; init; }
}

/// <summary>
/// 手机文件浏览视图模型。
///
/// 电脑浏览手机目录靠的是**手机主动上报的索引快照**（手机把授权目录的条目列表推给电脑），
/// 所以这里读的是服务端库里保存的快照，而不是实时去问手机。
///
/// 对手机目录的写操作（新建/重命名/删除/移动/下载）不在本机执行，而是**下发作业**让手机执行——
/// SAF 的 URI 授权只在手机上有效，服务端碰不到 content:// URI。
/// </summary>
public sealed class PhoneFilesViewModel : ViewModelBase
{
    /// <summary>一页多少条。手机目录动辄几千项，一次性全渲染会明显卡。</summary>
    private const int PageSize = 200;

    /// <summary>作业等多久算"还在进行"。超过就交给「传输」页看进度。</summary>
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(10);

    /// <summary>轮询作业状态的间隔。库在本地，读一次很便宜。</summary>
    private const int JobPollMs = 1200;

    private readonly MainViewModel _shell;

    /// <summary>手机在 30 秒内有过心跳才算在线（与设备页、服务端口径一致）。</summary>
    private static readonly TimeSpan OnlineWindow =
        TimeSpan.FromSeconds(ProtocolConstants.DeviceOfflineAfterSeconds);

    /// <summary>已下发但还没结果的作业键（操作|源路径|名字）。挡住连点两下发两份。</summary>
    /// <summary>
    /// 当前页面属于哪台手机。
    ///
    /// 1.5.2 之前这里没有这个字段：根列表把所有在线手机的根混在一起，
    /// 再由「根 id → 设备」反查。两台手机的根 id 往往相同（都是 allfiles/machine），
    /// 反查永远命中最先加入的那一台——于是"访问哪台都进第一台的目录"。
    /// </summary>
    private string _deviceId = string.Empty;

    private readonly HashSet<string> _inflight = new(StringComparer.Ordinal);

    /// <summary>
    /// 刚删掉、手机还没重推索引的条目（key = 相对路径）。
    ///
    /// 删除是"确认即生效"的：行当场从列表里拿掉，不等手机回执。但手机每 12 秒才推一次索引，
    /// 这期间的刷新会把旧快照里的同一条又带回来——看起来就是"删了还在"。用这份挡板挡住。
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _pendingDeletes = new(StringComparer.Ordinal);

    /// <summary>
    /// 现场问过手机的目录（key = rootId|path）：结果缓存 30 秒。
    /// 来回切目录时不该每次都打一台手机，也不该每次都等一次往返。
    /// </summary>
    private readonly Dictionary<string, (IReadOnlyList<FileEntryDto> Entries, DateTimeOffset At)> _listCache =
        new(StringComparer.Ordinal);

    private static readonly TimeSpan ListCacheWindow = TimeSpan.FromSeconds(30);

    private string _sortField = FileSortFields.Name;
    private bool _sortDescending;
    private int _pageIndex;
    private List<PhoneFileRowViewModel> _view = [];
    private int _rootCapabilities;

    /// <summary>
    /// 依赖 Shell 的构造函数。
    /// 必须先跑无参构造：命令的初始化在那里，漏掉就是"按钮点了没反应"
    /// （绑定拿不到命令时 WPF 不会报错，只会静默失败——0.5.x 踩过）。
    /// </summary>
    public PhoneFilesViewModel(MainViewModel shell) : this()
    {
        _shell = shell;
        Shell = shell;
    }

    private string _statusText = string.Empty;
    private bool _hasStatus;
    private bool _statusIsError;

    /// <summary>
    /// 状态条这句是"环境描述"（没配对 / 不在线 / 没目录）还是"操作反馈"（已删除 x）。
    ///
    /// 环境类只描述**刷新那一刻**，刷新成功后就不成立了，必须收掉；
    /// 操作反馈要留到下一次操作，不能被刷新清掉。
    /// </summary>
    private bool _statusIsEnvironment;

    /// <summary>
    /// 刷新代号。导航、切设备、托盘、手动刷新都会各发一次 RefreshAsync，
    /// 它们会交错：慢的旧结果后回来就会把新结果盖掉
    /// （列表是新的、底部状态是旧的，看着就是"提示错了"）。
    /// 只有最后一次有资格写界面。
    /// </summary>
    private int _refreshToken;

    private bool IsStale(int token) => token != 0 && token != _refreshToken;
    private string _currentRootId = string.Empty;
    private string _currentPath = string.Empty;
    private string _searchKeyword = string.Empty;
    private bool _busy;

    public MainViewModel Shell { get; }

    public System.Collections.ObjectModel.ObservableCollection<PhoneRootRowViewModel> Roots { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<PhoneFileRowViewModel> Entries { get; } = [];

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasStatus
    {
        get => _hasStatus;
        private set => SetProperty(ref _hasStatus, value);
    }

    /// <summary>状态行是不是错误。错误用红色，进度提示用次要文字色。</summary>
    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public string CurrentRootId
    {
        get => _currentRootId;
        private set => SetProperty(ref _currentRootId, value);
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(PathDisplay));
            }
        }
    }

    /// <summary>给人看的路径。根是 "/"，其它是相对路径——手机绝不让电脑看到绝对路径。</summary>
    public string PathDisplay => CurrentPath.Length == 0 ? "/" : CurrentPath;

    public string SearchKeyword
    {
        get => _searchKeyword;
        set
        {
            if (SetProperty(ref _searchKeyword, value))
            {
                RefreshView();
            }
        }
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                OnPropertyChanged(nameof(CanSendToPhone));
                OnPropertyChanged(nameof(CanCreateFolder));
            }
        }
    }

    /// <summary>当前根能不能往里写。能力位为 0（未知）时放行，由服务端定性。</summary>
    public bool CanSendToPhone => !Busy && FileCapabilityGate.Allows(_rootCapabilities, FileCapabilities.WriteFile);

    public bool CanCreateFolder => !Busy
        && FileCapabilityGate.Allows(_rootCapabilities, FileCapabilities.CreateDirectory);

    /// <summary>当前页的条目（已按关键词过滤、按当前排序规则排好）。</summary>
    public System.Collections.Generic.IEnumerable<PhoneFileRowViewModel> VisibleEntries
        => FileListOrder.Page(_view, _pageIndex, PageSize);

    /// <summary>排序按钮文案：当前字段带箭头，其他字段只有名字。</summary>
    public string NameSortText => DescribeSort("名称", FileSortFields.Name);

    public string SizeSortText => DescribeSort("大小", FileSortFields.Size);

    public string TimeSortText => DescribeSort("时间", FileSortFields.Modified);

    /// <summary>第几页（从 1 开始，给人看）。</summary>
    public int PageIndex => _pageIndex + 1;

    public int PageCount => FileListOrder.PageCount(_view.Count, PageSize);

    public string PageText => $"{PageIndex}/{PageCount}";

    public bool HasPages => PageCount > 1;

    private string DescribeSort(string label, string field)
        => _sortField == field ? $"{label} {(_sortDescending ? "↓" : "↑")}" : label;

    public RelayCommand SelectRootCommand { get; }

    /// <summary>一行的主操作：目录进下一级，文件存到电脑。</summary>
    public RelayCommand PrimaryActionCommand { get; }

    public RelayCommand GoUpCommand { get; }

    public RelayCommand CreateFolderCommand { get; }

    public RelayCommand RenameCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand RefreshCommand { get; }

    /// <summary>切换排序字段/方向。</summary>
    public RelayCommand SortCommand { get; }

    public RelayCommand PrevPageCommand { get; }

    public RelayCommand NextPageCommand { get; }

    /// <summary>把电脑上的文件发到手机当前目录。</summary>
    public RelayCommand SendToPhoneCommand { get; }

    /// <summary>全选 / 取消选择当前页。</summary>
    public RelayCommand SelectAllCommand { get; }

    /// <summary>删除勾选的条目。</summary>
    public RelayCommand DeleteSelectedCommand { get; }

    public PhoneFilesViewModel()
    {
        SelectRootCommand = new RelayCommand(p => _ = SelectRootAsync(p as string));
        PrimaryActionCommand = new RelayCommand(p => _ = RunPrimaryActionAsync(p as PhoneFileRowViewModel));
        GoUpCommand = new RelayCommand(_ => _ = GoUpAsync());
        CreateFolderCommand = new RelayCommand(_ => CreateFolder());
        RenameCommand = new RelayCommand(p => Rename(p as PhoneFileRowViewModel));
        DeleteCommand = new RelayCommand(p => _ = DeleteAsync(p as PhoneFileRowViewModel));
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(keepPage: true));
        SortCommand = new RelayCommand(p => ApplySort(p as string));
        PrevPageCommand = new RelayCommand(_ => MovePage(-1));
        NextPageCommand = new RelayCommand(_ => MovePage(1));
        SendToPhoneCommand = new RelayCommand(_ => SendFilesToPhone());

        // 勾选是"这一页的这几项"，不是"整台手机的搜索结果"：
        // 翻页后看不到的条目被悄悄删掉，比删错更糟。
        SelectAllCommand = new RelayCommand(p => SelectAll(p as string));
        DeleteSelectedCommand = new RelayCommand(_ => _ = DeleteSelectedAsync());
    }

    // ------------------------------------------------------------ 多选

    public int CheckedCount => VisibleEntries.Count(static row => row.IsChecked);

    public string CheckedText => $"已选 {CheckedCount} 项";

    public bool HasChecked => CheckedCount > 0;

    /// <summary>表头复选框：全勾上时显示勾，点一下全选/全不选。</summary>
    public bool AllChecked
    {
        get => VisibleEntries.Any() && VisibleEntries.All(static row => row.IsChecked);
        set
        {
            foreach (var row in VisibleEntries)
            {
                row.IsChecked = value;
            }

            NotifyChecked();
        }
    }

    private void SelectAll(string? mode)
    {
        var all = string.Equals(mode, "all", StringComparison.Ordinal);
        foreach (var row in VisibleEntries)
        {
            row.IsChecked = all;
        }

        NotifyChecked();
    }

    private void NotifyChecked()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(CheckedText));
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(AllChecked));
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PhoneFileRowViewModel.IsChecked))
        {
            return;
        }

        NotifyChecked();
    }

    /// <summary>刷新手机根与当前目录。</summary>
    public async Task RefreshAsync(bool keepPage = false)
    {
        // 导航、切设备、托盘、手动「刷新」都会发起一次，它们可能交错。
        // 只让最后一次写界面，否则慢的旧结果会把新结果盖掉——
        // 表现就是"列表已经是新的，底部状态还停在上一轮"（1.5.10 真机反馈）。
        var token = ++_refreshToken;

        try
        {
            // 只服务**当前选中的那台**手机。
            //
            // 以前这里把所有在线手机的根混进同一个列表，再由根 id 反查设备：
            // 两台手机的根 id 一样时，反查永远命中最先那台，
            // 于是"访问哪台都进第一台"、"发文件也发给第一台"（1.5.2 修）。
            // 从库里取，不用首页那份 DeviceRows：页面可能比设备列表更早打开
            var target = await _shell.GetPrimaryDeviceAsync().ConfigureAwait(true);
            if (IsStale(token))
            {
                return;
            }

            if (target is null)
            {
                Roots.Clear();
                Entries.Clear();
                _listCache.Clear();
                CurrentRootId = string.Empty;
                CurrentPath = string.Empty;
                RefreshView();
                SetStatus("还没有配对手机", environment: true);
                return;
            }

            var deviceChanged = !string.Equals(_deviceId, target.DeviceId, StringComparison.Ordinal);
            _deviceId = target.DeviceId;

            if (deviceChanged)
            {
                // 换了一台手机：路径、列表缓存、待删挡板都属于上一台，不能带过来
                CurrentPath = string.Empty;
                _listCache.Clear();
                _pendingDeletes.Clear();
                keepPage = false;
            }

            Serilog.Log.Information("刷新手机目录：设备 {Device}", target.DisplayName);

            // 用户主动断开的设备：服务端会拒绝它的一切请求，这里先说清原因
            if (target.SessionPaused)
            {
                Roots.Clear();
                Entries.Clear();
                _listCache.Clear();
                CurrentRootId = string.Empty;
                CurrentPath = string.Empty;
                RefreshView();
                SetStatus($"{target.DisplayName} 已被断开，在首页点「连接」恢复",
                    isError: true, environment: true);
                return;
            }

            // 手机不在线就不显示任何目录内容。
            //
            // 以前这里会把上次的索引快照照原样列出来——那是"上次看到的样子"，
            // 不是"手机现在的内容"，用户点进去才发现文件早就不在了（1.3.0 去掉）。
            var online = await IsDeviceOnlineAsync(_deviceId).ConfigureAwait(true);
            if (IsStale(token))
            {
                return;
            }

            if (!online)
            {
                Roots.Clear();
                Entries.Clear();
                _listCache.Clear();
                CurrentRootId = string.Empty;
                CurrentPath = string.Empty;
                RefreshView();
                SetStatus($"{target.DisplayName} 不在线", isError: true, environment: true);
                return;
            }

            var deviceRoots = await _shell.Server.Files
                .ListDeviceRootsAsync(_deviceId)
                .ConfigureAwait(true);
            if (IsStale(token))
            {
                return;
            }

            Roots.Clear();
            foreach (var root in deviceRoots)
            {
                Roots.Add(new PhoneRootRowViewModel
                {
                    RootId = root.RootId,
                    DisplayName = root.DisplayName,
                    CapabilitiesText = DescribeCapabilities(root.Capabilities),
                    DeviceName = target.DisplayName,
                    DeviceId = _deviceId,
                    Capabilities = root.Capabilities,
                });
            }

            // 当前根可能已经被撤销（手机清掉了授权），停在它上面会显示空目录。
            // 这种情况退回第一个根；一个根都不剩时清空选择。
            if (Roots.Count == 0)
            {
                if (CurrentRootId.Length != 0)
                {
                    CurrentRootId = string.Empty;
                    CurrentPath = string.Empty;
                }

                // 服务端只知道"根列表为空"，分不清"真没授权"和"手机还没上报"
                // （刚连上、刚重装、或索引还没推完）。以前一口咬定"还没有授权任何目录"，
                // 用户明明授权了整机就会觉得是 bug（1.5.10 真机）。
                SetStatus($"{target.DisplayName} 还没有可浏览的目录：确认手机上已授权，或点「刷新」",
                    environment: true);
            }
            else if (string.IsNullOrEmpty(CurrentRootId) ||
                     Roots.All(root => root.RootId != CurrentRootId))
            {
                CurrentRootId = Roots[0].RootId;
                CurrentPath = string.Empty;
                keepPage = false;
            }

            foreach (var root in Roots)
            {
                root.IsSelected = root.RootId == CurrentRootId;
            }

            SyncRootCapabilities();

            Serilog.Log.Information("刷新手机目录完成：根 {Roots} 个，当前 {Current}",
                Roots.Count, CurrentRootId);

            // 根有了，上面那些"没配对 / 不在线 / 没目录"的提示全部过期。
            // 不收掉的话它会一直挂在底部，和列表内容互相打脸。
            ClearEnvironmentStatus();

            await LoadEntriesAsync(keepPage, token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "刷新手机目录失败");
            SetStatus("手机目录读取失败", isError: true, environment: true);
        }
    }

    private async Task SelectRootAsync(string? rootId)
    {
        if (string.IsNullOrEmpty(rootId))
        {
            return;
        }

        // 让还在路上的刷新作废：它回来时会把用户刚选的根改回去
        _refreshToken++;

        ResetSearch();
        CurrentRootId = rootId;
        CurrentPath = string.Empty;
        foreach (var root in Roots)
        {
            root.IsSelected = root.RootId == rootId;
        }

        SyncRootCapabilities();

        Serilog.Log.Information("切换授权根 {RootId}", rootId);

        await LoadEntriesAsync().ConfigureAwait(true);
    }

    private async Task RunPrimaryActionAsync(PhoneFileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsDirectory)
        {
            await EnterAsync(row).ConfigureAwait(true);
            return;
        }

        await DownloadAsync(row).ConfigureAwait(true);
    }

    private async Task EnterAsync(PhoneFileRowViewModel row)
    {
        ResetSearch();
        CurrentPath = row.RelativePath;
        await LoadEntriesAsync().ConfigureAwait(true);
    }

    private async Task GoUpAsync()
    {
        if (string.IsNullOrEmpty(CurrentPath))
        {
            return;
        }

        ResetSearch();
        var index = CurrentPath.LastIndexOf('/');
        CurrentPath = index <= 0 ? string.Empty : CurrentPath[..index];
        await LoadEntriesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 换目录时清掉搜索词。
    /// 不清的话关键字会跟着进新目录，把里面的东西全过滤掉——看起来就是"空文件夹"。
    /// </summary>
    private void ResetSearch()
    {
        if (_searchKeyword.Length == 0)
        {
            return;
        }

        _searchKeyword = string.Empty;
        OnPropertyChanged(nameof(SearchKeyword));
    }

    private async Task LoadEntriesAsync(bool keepPage = false, int token = 0)
    {
        if (string.IsNullOrEmpty(CurrentRootId))
        {
            Entries.Clear();
            RefreshView(keepPage);
            return;
        }

        try
        {
            var entries = await _shell.Server.Files
                .ListDeviceEntriesAsync(_deviceId, CurrentRootId, CurrentPath)
                .ConfigureAwait(true);
            if (IsStale(token))
            {
                return;
            }

            // 索引快照有深度上限（整机 4 层 / 2 万条）。空列表有两种含义，必须分开：
            //   · 快照里有这个目录、只是没有子项 —— 真空目录，不要再问手机；
            //   · 快照里连这个目录都没有 —— 超出索引深度，现场问一次。
            // 以前只看"列表为空"，于是每个空目录都去问一遍手机，页面会一直卡在"正在读取"。
            if (entries.Count == 0 && !string.IsNullOrEmpty(CurrentPath))
            {
                var indexed = await _shell.Server.Files
                    .DeviceEntryExistsAsync(_deviceId, CurrentRootId, CurrentPath)
                    .ConfigureAwait(true);

                if (IsStale(token))
                {
                    return;
                }

                if (!indexed)
                {
                    var listed = await ListDirectoryFromPhoneAsync(CurrentRootId, CurrentPath).ConfigureAwait(true);
                    if (IsStale(token))
                    {
                        return;
                    }

                    if (listed is not null)
                    {
                        entries = listed;
                    }
                }
            }

            ApplyEntries(entries, keepPage);

            Serilog.Log.Information("读取条目 root={RootId} path={Path} 数量={Count}",
                CurrentRootId, CurrentPath.Length == 0 ? "/" : CurrentPath, Entries.Count);
        }
        catch (Exception ex)
        {
            if (IsStale(token))
            {
                return;
            }

            Serilog.Log.Error(ex, "读取手机目录条目失败");
            Entries.Clear();
            RefreshView(keepPage);
            SetStatus("读取手机目录失败", isError: true, environment: true);
        }
    }

    /// <summary>用一份条目列表重建当前列表。快照与现场列表都走这里，避免两套过滤逻辑。</summary>
    private void ApplyEntries(IReadOnlyList<FileEntryDto> entries, bool keepPage = false)
    {
        PrunePendingDeletes(entries);

        Entries.Clear();
        foreach (var entry in entries)
        {
            // 本地刚删掉、手机还没重推索引的：不要让它"复活"
            if (_pendingDeletes.ContainsKey(entry.RelativePath))
            {
                continue;
            }

            var row = new PhoneFileRowViewModel
            {
                RelativePath = entry.RelativePath,
                Name = entry.Name,
                IsDirectory = entry.IsDirectory,
                SizeBytes = entry.SizeBytes,
                ModifiedUnixMs = entry.ModifiedUnixMs,
                Capabilities = entry.Capabilities,
            };

            // 勾选状态要立刻反映到"已选 N 项"与表头复选框上
            row.PropertyChanged += OnRowPropertyChanged;
            Entries.Add(row);
        }

        RefreshView(keepPage);
        NotifyChecked();
    }

    /// <summary>
    /// 作业改过手机目录之后，直接问手机要一次真实列表。
    ///
    /// 为什么不靠索引快照：快照有上限（整机 2 万条 / 手选 5000 条，按 BFS 截断），
    /// 大盘里新建的目录很可能压根没进快照——于是"手机里明明有了，电脑刷新也看不到"。
    /// 现场 list 一次最可靠：手机列完直接回传，与索引是否跟上无关。
    /// 只有手机没答（离线、超时）才退回"等索引重推"的老路子。
    /// </summary>
    private async Task RefreshFromPhoneAsync(string operation, string? sourcePath, string? displayName)
    {
        var rootId = CurrentRootId;
        var path = CurrentPath;
        if (string.IsNullOrEmpty(rootId))
        {
            return;
        }

        var entries = await ListDirectoryFromPhoneAsync(rootId, path, force: true, quiet: true)
            .ConfigureAwait(true);

        if (entries is null)
        {
            await WaitForEntryChangeAsync(operation, sourcePath, displayName).ConfigureAwait(true);
            return;
        }

        // 期间用户可能已经翻到别的目录，别把结果糊到他眼前
        if (!string.Equals(rootId, CurrentRootId, StringComparison.Ordinal) ||
            !string.Equals(path, CurrentPath, StringComparison.Ordinal))
        {
            return;
        }

        ApplyEntries(entries, keepPage: true);
    }

    /// <summary>
    /// 重建当前视图：过滤 → 排序 → 回第一页（或留在当前页）。
    /// 排序与过滤都只在内存里做：条目来自手机上报的索引快照，本机不发起新的请求。
    /// </summary>
    private void RefreshView(bool keepPage = false)
    {
        IEnumerable<PhoneFileRowViewModel> query = Entries;

        if (!string.IsNullOrWhiteSpace(SearchKeyword))
        {
            query = query.Where(e => e.Name.Contains(SearchKeyword, StringComparison.OrdinalIgnoreCase));
        }

        _view = query.ToList();
        _view.Sort(CompareEntries);

        // 换目录、改排序、改搜索词都从第一页看起：停在原来的页码会让人以为文件没了。
        // 刷新（或作业完成后重读）则留在原页，只是把越界的页码收回来。
        _pageIndex = keepPage
            ? FileListOrder.ClampPage(_pageIndex, _view.Count, PageSize)
            : 0;

        OnPropertyChanged(nameof(VisibleEntries));
        OnPropertyChanged(nameof(PageIndex));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(NameSortText));
        OnPropertyChanged(nameof(SizeSortText));
        OnPropertyChanged(nameof(TimeSortText));
    }

    /// <summary>目录恒在最前；同类型内按当前字段比较，重名再按名称兜底，保证顺序稳定。</summary>
    private int CompareEntries(PhoneFileRowViewModel left, PhoneFileRowViewModel right)
        => FileListOrder.Compare(left, right, _sortField, _sortDescending);

    private void ApplySort(string? field)
    {
        var target = FileSortFields.Normalize(field);

        if (_sortField == target)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortField = target;

            // 名称从小到大、时间/大小从大到小：都是各自最常用的顺序
            _sortDescending = target != FileSortFields.Name;
        }

        RefreshView();
    }

    private void MovePage(int delta)
    {
        var target = FileListOrder.ClampPage(_pageIndex + delta, _view.Count, PageSize);
        if (target == _pageIndex)
        {
            return;
        }

        _pageIndex = target;
        OnPropertyChanged(nameof(VisibleEntries));
        OnPropertyChanged(nameof(PageIndex));
        OnPropertyChanged(nameof(PageText));
    }

    /// <summary>新建目录：弹输入框取名字。</summary>
    private void CreateFolder()
    {
        if (string.IsNullOrEmpty(CurrentRootId))
        {
            return;
        }

        if (!CanCreateFolder)
        {
            SetStatus("手机不允许在这个目录里新建文件夹", isError: true);
            return;
        }

        var dialog = new WirelessCable.Desktop.Views.InputDialog("文件夹名称");
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }

        var name = dialog.Value!.Trim();
        _ = EnqueueJobAsync(
            FileOperations.Create,
            sourcePath: CurrentPath,
            destinationPath: null,
            displayName: name,
            successText: $"已新建「{name}」",
            mutatesPhone: true);
    }

    /// <summary>重命名：弹输入框取新名字。</summary>
    private void Rename(PhoneFileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.CanRename)
        {
            SetStatus("手机不允许重命名这一项", isError: true);
            return;
        }

        var dialog = new WirelessCable.Desktop.Views.InputDialog("新名称", row.Name);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }

        _ = EnqueueJobAsync(
            FileOperations.Rename,
            sourcePath: row.RelativePath,
            destinationPath: null,
            displayName: dialog.Value!.Trim(),
            successText: $"已重命名为「{dialog.Value!.Trim()}」",
            mutatesPhone: true);
    }

    /// <summary>
    /// 右键「属性」：就地展示，不再请求服务端——行里已经有全部字段。
    /// 能力位为 0（老手机没上报）时不写这一行，免得写成"没有可用操作"的假话。
    /// </summary>
    public void ShowProperties(PhoneFileRowViewModel row)
    {
        var lines = new List<string>
        {
            $"名称：{row.Name}",
            $"类型：{row.TypeText}",
        };

        if (!row.IsDirectory)
        {
            lines.Add($"大小：{PhoneFileRowViewModel.FormatSize(row.SizeBytes)}（{row.SizeBytes:N0} 字节）");
        }

        if (row.ModifiedUnixMs > 0)
        {
            var modified = DateTimeOffset.FromUnixTimeMilliseconds(row.ModifiedUnixMs).ToLocalTime();
            lines.Add($"修改时间：{modified:yyyy-MM-dd HH:mm}");
        }

        lines.Add($"位置：{(CurrentPath.Length == 0 ? "/" : CurrentPath)}");

        if (row.Capabilities != 0)
        {
            lines.Add($"可用操作：{DescribeCapabilities(row.Capabilities)}");
        }

        Dialogs.Alert("属性", string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// 删除手机上的一个条目。
    ///
    /// 提示只在动手的这一端出现一次；确认之后没有第二步——不再为"允许删除"单独要授权，
    /// 也不显示"操作已下发、等待手机执行"，行当场从列表里消失，手机那边失败了再放回来。
    /// </summary>
    private async Task DeleteAsync(PhoneFileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.CanDelete)
        {
            SetStatus("手机不允许删除这一项", isError: true);
            return;
        }

        var confirmed = Dialogs.Confirm(
            "删除文件",
            $"删除「{row.Name}」？删除后无法恢复。",
            confirmText: "删除",
            danger: true);

        if (!confirmed)
        {
            return;
        }

        await DeleteNowAsync(row).ConfigureAwait(true);
    }

    /// <summary>
    /// 删除所有勾选项。确认一次，再逐条下发——每条各自有回执，一条失败不影响其余。
    /// </summary>
    private async Task DeleteSelectedAsync()
    {
        var selected = VisibleEntries.Where(static row => row.IsChecked).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        // 能力位不够的先摘出去：混在一起删会让用户以为"全删了"，实际有几项没动
        var deletable = selected.Where(static row => row.CanDelete).ToArray();
        var blocked = selected.Length - deletable.Length;

        if (deletable.Length == 0)
        {
            SetStatus("选中的条目都不允许删除", isError: true);
            return;
        }

        var confirmed = Dialogs.Confirm(
            "批量删除",
            $"删除选中的 {deletable.Length} 项？删除后无法恢复。",
            confirmText: "删除",
            danger: true);

        if (!confirmed)
        {
            return;
        }

        foreach (var row in deletable)
        {
            await DeleteNowAsync(row).ConfigureAwait(true);
        }

        SetStatus(blocked == 0
            ? $"已删除 {deletable.Length} 项"
            : $"已删除 {deletable.Length} 项，{blocked} 项不允许删除");
    }

    /// <summary>
    /// 确认之后的删除：本机立刻生效，不等回执。
    ///
    /// 数据仍然得由手机动手——SAF 的 content:// 只在手机上有效，电脑碰不到，
    /// 所以命令还是发给手机（长轮询下通常几十毫秒被取走）。去掉的是中间态：
    /// 不显示「等待手机执行」，不等索引重推，失败了再回滚并把原因说出来。
    /// </summary>
    private async Task DeleteNowAsync(PhoneFileRowViewModel row)
    {
        if (string.IsNullOrEmpty(CurrentRootId))
        {
            return;
        }

        var deviceId = ResolveDeviceId();
        if (deviceId is null)
        {
            SetStatus("找不到对应的手机设备", isError: true);
            return;
        }

        // 离线时不删：作业会一直躺在队列里，等手机两小时后连上再删掉一个用户早已忘记的文件，
        // 比"当场拒绝"更糟。
        if (!await IsDeviceOnlineAsync(deviceId).ConfigureAwait(true))
        {
            SetStatus("手机不在线，没有删除", isError: true);
            return;
        }

        var index = Entries.IndexOf(row);

        try
        {
            var job = await _shell.Server.Files
                .EnqueueDeviceJobAsync(
                    deviceId, CurrentRootId, FileOperations.Delete, row.RelativePath, null, row.Name)
                .ConfigureAwait(true);

            // 立刻生效：挡板先立起来，否则下一次索引刷新会把它带回来
            _pendingDeletes[row.RelativePath] = DateTimeOffset.UtcNow;

            Entries.Remove(row);
            RefreshView(keepPage: true);
            SetStatus($"已删除「{row.Name}」");

            Serilog.Log.Information("删除手机条目 path={Path} job={JobId}", row.RelativePath, job.JobId);

            _ = WatchDeleteAsync(deviceId, job.JobId, row, index);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "删除手机条目失败 path={Path}", row.RelativePath);
            SetStatus($"删除失败：{ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// 盯一次删除的结果。成功什么都不做（列表早就更新过了），失败把行放回原位。
    /// 删除不值得等 10 分钟——一分钟没有回执就如实说"结果未确认"。
    /// </summary>
    private async Task WatchDeleteAsync(
        string deviceId,
        string jobId,
        PhoneFileRowViewModel row,
        int index)
    {
        try
        {
            var device = await _shell.GetDeviceAsync(deviceId).ConfigureAwait(true);
            if (device is null)
            {
                return;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500).ConfigureAwait(true);

                var jobs = await _shell.Server.Files.ListJobsAsync(device, 20).ConfigureAwait(true);
                var job = jobs.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
                if (job is null)
                {
                    continue;
                }

                if (string.Equals(job.Status, FileJobStatuses.Failed, StringComparison.Ordinal))
                {
                    _pendingDeletes.Remove(row.RelativePath);
                    RestoreRow(row, index);

                    var reason = string.IsNullOrWhiteSpace(job.ErrorMessage) ? "手机删除失败" : job.ErrorMessage!;
                    SetStatus(reason, isError: true);
                    return;
                }

                if (string.Equals(job.Status, FileJobStatuses.Completed, StringComparison.Ordinal))
                {
                    // 快照有上限，删完不一定跟得上；顺手要一次真实列表，别让"删了还在"再出现
                    await RefreshFromPhoneAsync(FileOperations.Delete, row.RelativePath, row.Name)
                        .ConfigureAwait(true);
                    return;
                }
            }

            SetStatus("手机没有回执，删除结果未确认", isError: true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "跟踪删除结果失败 job={JobId}", jobId);
        }
    }

    /// <summary>把乐观删掉的行放回原位（手机执行失败时）。</summary>
    private void RestoreRow(PhoneFileRowViewModel row, int index)
    {
        if (Entries.Contains(row))
        {
            return;
        }

        Entries.Insert(Math.Clamp(index, 0, Entries.Count), row);
        RefreshView(keepPage: true);
    }

    /// <summary>
    /// 清掉过期的删除挡板：手机把索引重推上来（快照里已经没有这条）之后就不必再挡了。
    /// 五分钟是兜底——手机没重推索引时挡板不该永远留着。
    /// </summary>
    private void PrunePendingDeletes(IReadOnlyList<FileEntryDto> entries)
    {
        if (_pendingDeletes.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var present = new HashSet<string>(
            entries.Select(e => e.RelativePath), StringComparer.Ordinal);

        foreach (var (path, at) in _pendingDeletes.ToList())
        {
            var sameDirectory = string.Equals(ParentOf(path), CurrentPath, StringComparison.Ordinal);
            var indexCaughtUp = sameDirectory && !present.Contains(path);

            if (indexCaughtUp || now - at > TimeSpan.FromMinutes(5))
            {
                _pendingDeletes.Remove(path);
            }
        }
    }

    /// <summary>
    /// 把手机上的一个文件存到电脑。
    ///
    /// 走的是「下载」作业：手机收到后从自己的 SAF 里读出内容，用既有的上传通道推给电脑，
    /// 落点是电脑的接收目录。电脑不主动去手机拉——那需要手机起反向服务，
    /// 而这里的会话方向是手机连电脑（MASTER P1）。
    /// </summary>
    private async Task DownloadAsync(PhoneFileRowViewModel row)
    {
        if (string.IsNullOrEmpty(CurrentRootId) || !row.CanRunPrimaryAction)
        {
            return;
        }

        await EnqueueJobAsync(
            FileOperations.Download,
            sourcePath: row.RelativePath,
            destinationPath: null,
            displayName: row.Name,
            successText: $"「{row.Name}」已保存到电脑的接收目录",
            mutatesPhone: false).ConfigureAwait(true);
    }

    /// <summary>下发一个作业，然后盯着它跑完。手机轮询取走并执行。</summary>
    private async Task EnqueueJobAsync(
        string operation,
        string? sourcePath,
        string? destinationPath,
        string? displayName,
        string? transferTaskId = null,
        string? successText = null,
        bool mutatesPhone = false)
    {
        if (string.IsNullOrEmpty(CurrentRootId))
        {
            return;
        }

        var key = $"{operation}|{sourcePath}|{displayName}";
        if (!_inflight.Add(key))
        {
            SetStatus("同一操作还在进行中");
            return;
        }

        var handedOff = false;
        try
        {
            Busy = true;
            var deviceId = ResolveDeviceId();
            if (deviceId is null)
            {
                SetStatus("找不到对应的手机设备", isError: true);
                return;
            }

            // 手机不在线时作业只会躺在队列里，等 5 分钟被判超时。
            // 与其让用户对着"等待手机执行"发呆，不如当场说清楚（K48 之后仍成立：
            // 超时回收只是兜底，不是让用户白等的手段）。
            if (!await IsDeviceOnlineAsync(deviceId).ConfigureAwait(true))
            {
                SetStatus("手机不在线，操作没有下发", isError: true);
                return;
            }

            var job = await _shell.Server.Files
                .EnqueueDeviceJobAsync(
                    deviceId, CurrentRootId, operation, sourcePath, destinationPath, displayName, transferTaskId)
                .ConfigureAwait(true);

            Serilog.Log.Information(
                "已下发手机文件作业 op={Operation} root={RootId} job={JobId}",
                operation, CurrentRootId, job.JobId);

            SetStatus("操作已下发，等待手机执行");
            handedOff = true;

            // 作业结果只有手机知道：轮询到完成再刷新列表，用户不用自己点「刷新」猜做没做成
            _ = WatchJobAsync(deviceId, job.JobId, operation, sourcePath, displayName, successText, mutatesPhone);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "下发手机文件作业失败");
            SetStatus($"下发失败：{ex.Message}", isError: true);
        }
        finally
        {
            Busy = false;
            if (!handedOff)
            {
                _inflight.Remove(key);
            }
        }
    }

    /// <summary>轮询作业状态直到出结果。超时不报错——大文件本来就要传很久。</summary>
    private async Task WatchJobAsync(
        string deviceId,
        string jobId,
        string operation,
        string? sourcePath,
        string? displayName,
        string? successText,
        bool mutatesPhone)
    {
        var key = $"{operation}|{sourcePath}|{displayName}";
        try
        {
            var device = await _shell.GetDeviceAsync(deviceId).ConfigureAwait(true);
            if (device is null)
            {
                return;
            }

            var deadline = DateTime.UtcNow + JobTimeout;
            var waitingSince = DateTime.UtcNow;
            var warned = false;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(JobPollMs).ConfigureAwait(true);

                var jobs = await _shell.Server.Files.ListJobsAsync(device, 20).ConfigureAwait(true);
                var job = jobs.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
                if (job is null)
                {
                    continue;
                }

                // 作业一直没人取，说明手机上的轮询停了（息屏、应用被杀）。
                // 这里要把两种原因分开说，否则用户只能看到一句"没有响应"：
                //   · 心跳还在 → 手机连着呢，只是没在取作业（应用在后台 / 没开后台保持连接）
                //   · 心跳也没了 → 手机压根没连上，作业在队列里等着
                if (string.Equals(job.Status, FileJobStatuses.Pending, StringComparison.Ordinal))
                {
                    if (!warned && DateTime.UtcNow - waitingSince > TimeSpan.FromSeconds(8))
                    {
                        warned = true;
                        var online = await IsDeviceOnlineAsync(deviceId).ConfigureAwait(true);
                        SetStatus(
                            online
                                ? "手机没有取走操作：请把 App 切到前台，或在设置里打开「后台保持连接」"
                                : "手机已离线：操作还在队列里，手机连上后会自动执行",
                            isError: !online);

                        Serilog.Log.Warning(
                            "作业迟迟没人取 job={JobId} online={Online} waited={Seconds}s",
                            jobId, online, (DateTime.UtcNow - waitingSince).TotalSeconds);
                    }
                }
                else
                {
                    waitingSince = DateTime.UtcNow;
                }

                if (string.Equals(job.Status, FileJobStatuses.Completed, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(successText))
                    {
                        SetStatus(successText);
                    }

                    if (mutatesPhone)
                    {
                        // 先现场问手机要真实列表；拿不到才退回"等索引重推"
                        await RefreshFromPhoneAsync(operation, sourcePath, displayName).ConfigureAwait(true);
                    }

                    return;
                }

                if (string.Equals(job.Status, FileJobStatuses.Failed, StringComparison.Ordinal))
                {
                    var reason = string.IsNullOrWhiteSpace(job.ErrorMessage) ? "手机执行失败" : job.ErrorMessage!;
                    SetStatus(reason, isError: true);
                    return;
                }
            }

            SetStatus("手机上仍在进行，进度见「传输」");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "跟踪手机文件作业失败 job={JobId}", jobId);
            SetStatus("无法获取操作结果", isError: true);
        }
        finally
        {
            _inflight.Remove(key);
        }
    }

    /// <summary>
    /// 等手机把新索引推上来再把列表刷新一遍。
    ///
    /// 索引是快照，手机执行完作业后要下一次上报才会变；立刻重读只会看到旧数据，
    /// 于是"删了还在"。这里按本次操作的目标路径逐次比对，变了一致就停。
    /// </summary>
    private async Task WaitForEntryChangeAsync(string operation, string? sourcePath, string? displayName)
    {
        var (path, shouldExist) = operation switch
        {
            FileOperations.Create => (CombinePath(sourcePath, displayName), true),
            FileOperations.Upload => (CombinePath(sourcePath, displayName), true),
            FileOperations.Rename => (CombinePath(ParentOf(sourcePath), displayName), true),
            FileOperations.Delete => (sourcePath ?? string.Empty, false),
            _ => (string.Empty, true),
        };

        var rootId = CurrentRootId;
        var directory = CurrentPath;

        for (var attempt = 0; attempt < 12; attempt++)
        {
            // 用户此时可能已经翻到别的目录，那就别再动他眼前的列表了
            if (!string.Equals(rootId, CurrentRootId, StringComparison.Ordinal) ||
                !string.Equals(directory, CurrentPath, StringComparison.Ordinal))
            {
                return;
            }

            await LoadEntriesAsync(keepPage: true).ConfigureAwait(true);

            if (path.Length == 0)
            {
                return;
            }

            // 只对"操作发生在当前目录"的情况比对：新建/删除/改名都在当前目录里
            var exists = Entries.Any(e => string.Equals(e.RelativePath, path, StringComparison.Ordinal));
            if (exists == shouldExist)
            {
                return;
            }

            await Task.Delay(700).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 把电脑上的文件传到手机当前目录。
    ///
    /// 数据流复用既有的下载通道：电脑先建一个下载任务（手机去电脑拉），
    /// 再把任务 id 随作业一起下发，手机拉完写进它的 SAF 目录。
    /// 这样 SHA-256 校验与断点续传都是现成能力，不另起炉灶。
    /// </summary>
    private void SendFilesToPhone()
    {
        if (string.IsNullOrEmpty(CurrentRootId))
        {
            SetStatus("没有已配对的手机", isError: true);
            return;
        }

        if (!CanSendToPhone)
        {
            SetStatus("手机不允许写入这个目录", isError: true);
            return;
        }

        _ = SendFilesToPhoneAsync();
    }

    /// <summary>
    /// 先确认手机在线再弹文件选择框。
    /// 以前的顺序是"先让用户挑完文件，再说手机不在线"——白挑一遍，还以为是选文件出了问题。
    /// </summary>
    private async Task SendFilesToPhoneAsync()
    {
        var deviceId = ResolveDeviceId();
        if (deviceId is null || !await IsDeviceOnlineAsync(deviceId).ConfigureAwait(true))
        {
            SetStatus("手机不在线，没有发送", isError: true);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要发到手机的文件",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        _ = SendFilesAsync(dialog.FileNames).ConfigureAwait(true);
    }

    private async Task SendFilesAsync(IReadOnlyList<string> paths)
    {
        try
        {
            Busy = true;
            var deviceId = ResolveDeviceId();
            if (deviceId is null)
            {
                SetStatus("找不到对应的手机设备", isError: true);
                return;
            }

            var device = await _shell.GetDeviceAsync(deviceId).ConfigureAwait(true);
            if (device is null)
            {
                SetStatus("找不到对应的手机设备", isError: true);
                return;
            }

            var accepted = 0;
            var failed = 0;
            string? firstError = null;

            foreach (var path in paths)
            {
                // 电脑侧建下载任务：手机之后按这个任务 id 把内容拉回来。
                // 落点是手机上的授权目录（由下面的作业指定），所以必须标 directory——
                // 否则手机可能先把它当"发到收件箱"自动收掉。
                var created = await _shell.Server.Transfers
                    .CreateDownloadAsync(device, path, TransferPurposes.Directory)
                    .ConfigureAwait(true);

                if (!created.Success)
                {
                    failed++;
                    firstError ??= $"{System.IO.Path.GetFileName(path)}：{created.Error!.Message}";
                    continue;
                }

                var job = await _shell.Server.Files
                    .EnqueueDeviceJobAsync(
                        deviceId,
                        CurrentRootId,
                        FileOperations.Upload,
                        sourcePath: CurrentPath,
                        destinationPath: null,
                        displayName: System.IO.Path.GetFileName(path),
                        transferTaskId: created.Value!.TaskId)
                    .ConfigureAwait(true);

                Serilog.Log.Information(
                    "已下发上传作业 job={JobId} task={TransferTaskId} file={FileName}",
                    job.JobId, created.Value!.TaskId, System.IO.Path.GetFileName(path));

                // 传完文件才出现在手机目录里，盯着它落地再把列表刷过来
                _ = WatchJobAsync(
                    deviceId,
                    job.JobId,
                    FileOperations.Upload,
                    CurrentPath,
                    System.IO.Path.GetFileName(path),
                    successText: $"「{System.IO.Path.GetFileName(path)}」已发送到手机",
                    mutatesPhone: true);

                accepted++;
            }

            SetStatus(failed == 0
                ? $"已下发 {accepted} 个文件，等待手机接收"
                : $"已下发 {accepted} 个，{failed} 个失败：{firstError}",
                isError: failed > 0);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "下发上传作业失败");
            SetStatus($"下发失败：{ex.Message}", isError: true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>当前根属于哪台手机。按根上记的设备 id 取，不按显示名猜。</summary>
    private string? ResolveDeviceId()
        => !string.IsNullOrEmpty(_deviceId)
            ? _deviceId
            : Roots.FirstOrDefault(root => root.RootId == CurrentRootId)?.DeviceId;

    /// <summary>
    /// 现场让手机列一次目录（list 作业），给索引没覆盖到的深目录用。
    /// 返回 null 表示"没问到"（离线 / 没开后台服务 / 超时），调用方按空目录呈现。
    /// </summary>
    /// <param name="force">true 时跳过缓存：作业刚改过目录，必须拿到真实列表。</param>
    /// <param name="quiet">true 时不改状态栏——调用方自己已经写了"已创建/已删除"。</param>
    private async Task<IReadOnlyList<FileEntryDto>?> ListDirectoryFromPhoneAsync(
        string rootId,
        string path,
        bool force = false,
        bool quiet = false)
    {
        var deviceId = ResolveDeviceId();
        if (deviceId is null)
        {
            return null;
        }

        var key = $"{rootId}|{path}";

        // 30 秒内问过的路径直接复用：来回切目录不该每次都打一台手机
        if (!force && _listCache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < ListCacheWindow)
        {
            return cached.Entries;
        }

        if (!await IsDeviceOnlineAsync(deviceId).ConfigureAwait(true))
        {
            if (!quiet)
            {
                SetStatus("手机不在线", isError: true);
            }

            return null;
        }

        try
        {
            if (!quiet)
            {
                SetStatus("正在读取手机目录…");
            }

            var job = await _shell.Server.Files
                .EnqueueDeviceJobAsync(deviceId, rootId, FileOperations.List, path, null, null)
                .ConfigureAwait(true);

            Serilog.Log.Information("已下发 list 作业 root={Root} path={Path} job={Job}",
                rootId, path, job.JobId);

            // 手机端是挂起式轮询，正常一次网络往返就开始执行；这里 200 ms 查一次本地库，
            // 8 秒还没结果就放弃——比让用户对着空目录猜"是不是没有文件"更诚实。
            IReadOnlyList<FileEntryDto>? result = null;

            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(200).ConfigureAwait(true);

                var current = await _shell.Server.Files.GetJobAsync(job.JobId).ConfigureAwait(true);
                if (current is null)
                {
                    break;
                }

                if (current.Status == FileJobStatuses.Failed)
                {
                    Serilog.Log.Warning("list 作业失败 root={Root} path={Path} code={Code} message={Message}",
                        rootId, path, current.ErrorCode, current.ErrorMessage);

                    if (!quiet)
                    {
                        SetStatus(current.ErrorMessage ?? "手机读取目录失败", isError: true);
                    }

                    return null;
                }

                if (current.Status != FileJobStatuses.Completed)
                {
                    continue;
                }

                result = string.IsNullOrEmpty(current.Payload)
                    ? []
                    : WcJson.Deserialize<FileEntryDto[]>(current.Payload) ?? [];

                break;
            }

            if (result is null)
            {
                Serilog.Log.Warning("list 作业超时 root={Root} path={Path}", rootId, path);
                if (!quiet)
                {
                    SetStatus("手机没有响应，该目录可能尚未索引", isError: true);
                }

                return null;
            }

            Serilog.Log.Information("list 作业完成 root={Root} path={Path} 数量={Count}",
                rootId, path, result.Count);

            _listCache[key] = (result, DateTimeOffset.UtcNow);

            // 成功就把"正在读取…"收掉，别让它一直挂在状态栏上
            if (!quiet)
            {
                ClearStatus();
            }

            return result;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "现场列手机目录失败 root={Root} path={Path}", rootId, path);
            if (!quiet)
            {
                SetStatus("读取手机目录失败", isError: true);
            }

            return null;
        }
    }

    /// <summary>30 秒内有过心跳就算在线。读库而不是看界面快照——手机可能刚掉线。</summary>
    private async Task<bool> IsDeviceOnlineAsync(string deviceId)
    {
        var device = await _shell.GetDeviceAsync(deviceId).ConfigureAwait(true);
        return device?.LastSeenUtc is { } seen
               && DateTimeOffset.UtcNow - seen <= OnlineWindow;
    }

    private void SyncRootCapabilities()
    {
        var capabilities = Roots.FirstOrDefault(root => root.RootId == CurrentRootId)?.Capabilities ?? 0;
        if (capabilities == _rootCapabilities)
        {
            return;
        }

        _rootCapabilities = capabilities;
        OnPropertyChanged(nameof(CanSendToPhone));
        OnPropertyChanged(nameof(CanCreateFolder));
    }

    private void SetStatus(string text, bool isError = false, bool environment = false)
    {
        StatusIsError = isError;
        StatusText = text;
        HasStatus = true;
        _statusIsEnvironment = environment;
    }

    /// <summary>
    /// 收掉"环境类"提示。刷新成功后「没配对 / 不在线 / 没目录」都不再成立。
    ///
    /// 以前这些提示只在失败分支里设置、成功路径从不清除，于是：
    /// 进页面时索引还没到 → 显示"还没有授权任何目录" → 索引到了、列表也出来了，
    /// 底部那句还挂着，和列表内容互相打脸（1.5.10 真机反馈）。
    /// </summary>
    private void ClearEnvironmentStatus()
    {
        if (_statusIsEnvironment)
        {
            ClearStatus();
        }
    }

    /// <summary>收掉状态条。成功路径必须调它——否则"正在读取…"会一直挂着。</summary>
    private void ClearStatus()
    {
        StatusIsError = false;
        StatusText = string.Empty;
        HasStatus = false;
        _statusIsEnvironment = false;
    }

    private static string ParentOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var index = path.LastIndexOf('/');
        return index <= 0 ? string.Empty : path[..index];
    }

    private static string CombinePath(string? directory, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        return string.IsNullOrEmpty(directory) ? name : $"{directory}/{name}";
    }

    /// <summary>能力位 → 人话，明确告诉用户哪些操作可用。</summary>
    public static string DescribeCapabilities(int capabilities)
    {
        var parts = new List<string>();
        if ((capabilities & FileCapabilities.ListDirectory) != 0) parts.Add("浏览");
        if ((capabilities & FileCapabilities.ReadFile) != 0) parts.Add("读取");
        if ((capabilities & FileCapabilities.WriteFile) != 0) parts.Add("写入");
        if ((capabilities & FileCapabilities.CreateDirectory) != 0) parts.Add("新建");
        if ((capabilities & FileCapabilities.Rename) != 0) parts.Add("重命名");
        if ((capabilities & FileCapabilities.Delete) != 0) parts.Add("删除");
        if ((capabilities & FileCapabilities.Move) != 0) parts.Add("移动");
        return parts.Count == 0 ? "仅浏览" : string.Join("/", parts);
    }
}
