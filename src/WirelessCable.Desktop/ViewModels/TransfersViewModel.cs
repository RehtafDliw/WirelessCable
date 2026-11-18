using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using WirelessCable.Desktop.Services;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Transfer;

namespace WirelessCable.Desktop.ViewModels;

/// <summary>
/// 传输页：发送文件/文件夹、实时进度、取消、打开接收目录。
///
/// 进度来自 <see cref="TransferEngine.Progress"/>（后台线程，每 8 MiB 一次），
/// 这里只把它塞进队列；真正刷新 UI 的是 400ms 的 DispatcherTimer。
/// 这样即便将来分块变小，也不会把 UI 线程打满。
/// </summary>
public sealed class TransfersViewModel : ViewModelBase
{
    private readonly MainViewModel _shell;
    private readonly DispatcherTimer _timer;
    private readonly ConcurrentQueue<TransferTaskDto> _pending = new();
    private readonly Dictionary<string, TransferTaskDto> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransferRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpeedWindow> _samples = new(StringComparer.Ordinal);

    /// <summary>剩余时间估算上限。超过它的估算没有意义，宁可不显示。</summary>
    private const double MaxRemainingSeconds = 99 * 3600;

    private int _tick;
    private TransferTab _tab = TransferTab.Active;
    private bool _busy;
    private bool _hasDevice;
    private string _statusMessage = string.Empty;
    private string _activeName = string.Empty;
    private string _activeDetail = string.Empty;
    private double _activePercent;

    public TransfersViewModel(MainViewModel shell)
    {
        _shell = shell;

        SendFilesCommand = new RelayCommand(_ => ChooseFiles(), _ => !_busy && _hasDevice);
        SendFolderCommand = new RelayCommand(_ => ChooseFolder(), _ => !_busy && _hasDevice);
        PauseCommand = new RelayCommand(p => _ = PauseAsync(p as string));
        ResumeCommand = new RelayCommand(p => _ = ResumeAsync(p as string));
        CancelCommand = new RelayCommand(p => _ = CancelAsync(p as string));
        OpenSharedFolderCommand = new RelayCommand(_ => OpenSharedFolder());
        SelectTabCommand = new RelayCommand(p => SelectTab(p as string));
        SelectFilterCommand = new RelayCommand(p => SelectFilter(p as string));
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection());
        DeleteRecordsCommand = new RelayCommand(p => _ = DeleteRecordsAsync(p as TransferRowViewModel));
        RevealCommand = new RelayCommand(p => Reveal(p as TransferRowViewModel));

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _timer.Tick += OnTick;
    }

    public MainViewModel Shell => _shell;

    public ObservableCollection<TransferRowViewModel> Tasks { get; } = [];

    public RelayCommand SendFilesCommand { get; }
    public RelayCommand SendFolderCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenSharedFolderCommand { get; }
    public RelayCommand SelectTabCommand { get; }

    public RelayCommand SelectFilterCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand DeleteRecordsCommand { get; }
    public RelayCommand RevealCommand { get; }

    public bool HasTasks => Tasks.Count > 0;

    // ------------------------------------------------------------ 页签

    /// <summary>
    /// 只留两个页签：传输中 / 已完成。
    ///
    /// 失败、暂停、中断都归「传输中」——它们都是**还没结束**的任务，
    /// 单开一堆页签只会让人在两处之间找同一条记录。已完成才是真正的"归档"。
    /// </summary>
    public TransferTab CurrentTab
    {
        get => _tab;
        private set
        {
            if (SetProperty(ref _tab, value))
            {
                OnPropertyChanged(nameof(IsActiveTab));
                OnPropertyChanged(nameof(IsCompletedTab));
                OnPropertyChanged(nameof(VisibleTasks));
                OnPropertyChanged(nameof(HasVisibleTasks));
                OnPropertyChanged(nameof(EmptyHint));
            }
        }
    }

    public bool IsActiveTab => _tab == TransferTab.Active;

    public bool IsCompletedTab => _tab == TransferTab.Completed;

    /// <summary>「传输中」= 所有还没完成的任务（含失败、暂停、中断）。</summary>
    public IEnumerable<TransferRowViewModel> ActiveTabTasks
        => Tasks.Where(static t => t.State != TransferStates.Completed);

    public IEnumerable<TransferRowViewModel> CompletedTabTasks
        => Tasks.Where(static t => t.State == TransferStates.Completed);

    /// <summary>方向筛选：一条记录不是"我发的"就是"我收的"。</summary>
    public enum TransferFilter { All, Sent, Received }

    private TransferFilter _filter = TransferFilter.All;

    public bool IsFilterAll => _filter == TransferFilter.All;

    public bool IsFilterSent => _filter == TransferFilter.Sent;

    public bool IsFilterReceived => _filter == TransferFilter.Received;

    public IEnumerable<TransferRowViewModel> VisibleTasks
    {
        get
        {
            var byTab = IsActiveTab ? ActiveTabTasks : CompletedTabTasks;
            return _filter switch
            {
                TransferFilter.Sent => byTab.Where(static t => !t.IsDownload),
                TransferFilter.Received => byTab.Where(static t => t.IsDownload),
                _ => byTab,
            };
        }
    }

    private void SelectFilter(string? key)
    {
        var next = key switch
        {
            "sent" => TransferFilter.Sent,
            "received" => TransferFilter.Received,
            _ => TransferFilter.All,
        };
        if (next == _filter)
        {
            return;
        }

        _filter = next;
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterSent));
        OnPropertyChanged(nameof(IsFilterReceived));
        OnPropertyChanged(nameof(VisibleTasks));
        OnPropertyChanged(nameof(HasVisibleTasks));
        OnPropertyChanged(nameof(IsAllSelected));
    }

    public int ActiveTabCount => Tasks.Count(static t => t.State != TransferStates.Completed);

    public int CompletedTabCount => Tasks.Count(static t => t.State == TransferStates.Completed);

    public string ActiveTabLabel => $"传输中 {ActiveTabCount}";

    public string CompletedTabLabel => $"已完成 {CompletedTabCount}";

    public bool HasVisibleTasks => VisibleTasks.Any();

    public string EmptyHint => IsActiveTab ? "没有正在进行的传输" : "还没有已完成的传输";

    // ------------------------------------------------------------ 多选

    public IEnumerable<TransferRowViewModel> SelectedRows => Tasks.Where(static t => t.IsSelected);

    public int SelectedCount => SelectedRows.Count();

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => $"已选 {SelectedCount} 项";

    /// <summary>
    /// 表头那一列的"全选"。null 表示部分选中——三态复选框的中间态，只用于显示。
    ///
    /// 只作用于**当前页签可见的行**：用户看到哪一批就选哪一批。
    /// 跨页签全选会选到看不见的行，"已选 N 项"里的 N 就对不上屏幕了。
    /// </summary>
    public bool? IsAllSelected
    {
        get
        {
            var visible = VisibleTasks.ToArray();
            if (visible.Length == 0)
            {
                return false;
            }

            var selected = visible.Count(static r => r.IsSelected);
            return selected == 0 ? false : selected == visible.Length ? true : null;
        }

        set
        {
            // IsThreeState=false，这里只会收到 true/false；
            // 已经全选时点一下是取消，其余情况（含部分选中）都是补全
            var selectAll = value == true;
            foreach (var row in VisibleTasks.ToArray())
            {
                row.IsSelected = selectAll;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>按状态分组：正在传输 / 等待 / 已暂停 / 失败 / 已完成。</summary>
    public IEnumerable<TransferRowViewModel> ActiveTasks
        => Tasks.Where(static t => TransferStates.IsActive(t.State));

    public IEnumerable<TransferRowViewModel> WaitingTasks
        => Tasks.Where(static t => t.State is TransferStates.Waiting or TransferStates.Preparing);

    public IEnumerable<TransferRowViewModel> PausedTasks
        => Tasks.Where(static t => t.State is TransferStates.Paused or TransferStates.Interrupted);

    public IEnumerable<TransferRowViewModel> FailedTasks
        => Tasks.Where(static t => t.State is TransferStates.Failed or TransferStates.Cancelled);

    public IEnumerable<TransferRowViewModel> CompletedTasks
        => Tasks.Where(static t => t.State == TransferStates.Completed);

    public bool HasActiveGroup => ActiveTasks.Any();

    public bool HasWaitingGroup => WaitingTasks.Any();

    public bool HasPausedGroup => PausedTasks.Any();

    public bool HasFailedGroup => FailedTasks.Any();

    public bool HasCompletedGroup => CompletedTasks.Any();

    /// <summary>首页「最近任务」只展示前三条。Tasks 本身已按更新时间倒序。</summary>
    public System.Collections.Generic.IEnumerable<TransferRowViewModel> RecentTasks
        => Tasks.Take(3);

    public bool HasRecent => Tasks.Count > 0;

    public bool HasActive => _latest.Values.Any(static d => TransferStates.IsActive(d.State));

    /// <summary>首页「当前传输」显示的文件名。没有活跃任务时为空。</summary>
    public string ActiveName
    {
        get => _activeName;
        private set => SetProperty(ref _activeName, value);
    }

    /// <summary>首页「当前传输」的明细行，与传输页用同一套格式化。</summary>
    public string ActiveDetail
    {
        get => _activeDetail;
        private set => SetProperty(ref _activeDetail, value);
    }

    public double ActivePercent
    {
        get => _activePercent;
        private set => SetProperty(ref _activePercent, value);
    }

    public bool HasDevice
    {
        get => _hasDevice;
        private set
        {
            if (SetProperty(ref _hasDevice, value))
            {
                OnPropertyChanged(nameof(DeviceHint));
            }
        }
    }

    /// <summary>
    /// 发送目标写在工具栏上。
    ///
    /// 多设备时"发送文件"必须说清发给谁——以前这里只写"尚未配对手机"或空着，
    /// 用户以为发给了刚访问的那台，实际发给的是第一个连上的设备。
    /// </summary>
    public string DeviceHint => _hasDevice ? $"发送到 {_shell.SelectedDeviceName}" : "尚未配对手机";

    public bool Busy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_statusMessage);

    public string SharedDirectory => _shell.SharedDirectory;


    /// <summary>由 MainViewModel 在服务启动后调用。</summary>
    public void Start()
    {
        _shell.Server.Transfers.Progress += OnProgress;
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>退出前解绑，避免定时器在窗口销毁后继续跑。</summary>
    public void Stop()
    {
        _timer.Stop();
        _shell.Server.Transfers.Progress -= OnProgress;
    }

    public async Task RefreshAsync()
    {
        try
        {
            var device = await _shell.GetPrimaryDeviceAsync().ConfigureAwait(true);
            HasDevice = device is not null;
            OnPropertyChanged(nameof(DeviceHint));

            var deviceNames = await _shell.GetDeviceNamesAsync().ConfigureAwait(true);
            var tasks = await _shell.Server.Transfers
                // 与 SyncRows 的窗口一致：界面最多显示 50 条，就取 50 条。
                // 取少了会让"服务端有、界面没有"，取多了纯属浪费。
                .ListRecentAsync(50, deviceNames)
                .ConfigureAwait(true);

            Merge(tasks);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "刷新传输任务失败");
        }
    }

    private void OnProgress(object? sender, TransferTaskDto dto) => _pending.Enqueue(dto);

    private void OnTick(object? sender, EventArgs e)
    {
        while (_pending.TryDequeue(out var dto))
        {
            _latest[dto.TaskId] = dto;
        }

        SyncRows();

        // 全量刷新只做兜底：手机发起的任务、终态变化都在这里补齐
        if (++_tick % 5 == 0)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>把引擎返回的列表合并进 UI。内存里的会话进度比数据库新，不要被旧快照覆盖。</summary>
    private void Merge(IReadOnlyList<TransferTaskDto> tasks)
    {
        /*
         * 服务端列表是这个页面的唯一真相：已经删掉/被回收的条目必须消失。
         *
         * 以前这里只往里加、从不移除，于是"删了记录还在"——删除时清了内存，
         * 下一轮刷新又把服务端的历史拉回来；服务端那边清空后，界面上还留着一堆幽灵行
         * （1.5.8 真机确认：TransferTasks 表 0 行，界面仍显示 23 条已完成 + 7 条失败）。
         *
         * 两个例外不能误删：
         * 1. 本地正在传的条目——进度比快照新，等服务端状态追上来；
         * 2. 比服务端返回的最旧一条还旧的——接口只给最近 N 条，那些是被截断而非被删除。
         */
        var remoteIds = tasks.Select(static d => d.TaskId).ToHashSet(StringComparer.Ordinal);
        var oldestRemote = tasks.Count == 0
            ? long.MaxValue
            : tasks.Min(static d => d.UpdatedAtUnixMs);

        foreach (var stale in _latest.Keys.Where(id => !remoteIds.Contains(id)).ToArray())
        {
            var local = _latest[stale];
            if (TransferStates.IsActive(local.State))
            {
                continue;
            }

            if (local.UpdatedAtUnixMs < oldestRemote)
            {
                continue;
            }

            _latest.Remove(stale);
            _rows.Remove(stale);
            _samples.Remove(stale);
        }

        foreach (var dto in tasks)
        {
            if (!_latest.TryGetValue(dto.TaskId, out var existing)
                || dto.UpdatedAtUnixMs >= existing.UpdatedAtUnixMs)
            {
                _latest[dto.TaskId] = dto;
            }
        }

        SyncRows();
    }

    private void SyncRows()
    {
        var ordered = _latest.Values
            // 只按"是否未完成"分组，组内保持到达顺序：
            // 按 UpdatedAt 排会让两条同时在传的任务每次进度回报都互换位置
            .OrderByDescending(static d => !string.Equals(d.State, TransferStates.Completed))
            .Take(50)
            .ToArray();

        var sameSet = ordered.Length == Tasks.Count
                      && ordered.Select(static d => d.TaskId).SequenceEqual(Tasks.Select(static t => t.TaskId));

        if (!sameSet)
        {
            // 清掉已经不在窗口里的旧任务，避免无限增长
            foreach (var stale in _rows.Keys.Except(ordered.Select(static d => d.TaskId)).ToArray())
            {
                _rows.Remove(stale);
                _samples.Remove(stale);
            }

            Tasks.Clear();
            foreach (var dto in ordered)
            {
                Tasks.Add(GetOrCreateRow(dto));
            }
        }

        foreach (var dto in ordered)
        {
            UpdateRow(GetOrCreateRow(dto), dto);
        }

        UpdateActive(ordered);

        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(RecentTasks));
        OnPropertyChanged(nameof(ActiveTasks));
        OnPropertyChanged(nameof(WaitingTasks));
        OnPropertyChanged(nameof(PausedTasks));
        OnPropertyChanged(nameof(FailedTasks));
        OnPropertyChanged(nameof(CompletedTasks));
        OnPropertyChanged(nameof(HasActiveGroup));
        OnPropertyChanged(nameof(HasWaitingGroup));
        OnPropertyChanged(nameof(HasPausedGroup));
        OnPropertyChanged(nameof(HasFailedGroup));
        OnPropertyChanged(nameof(HasCompletedGroup));

        OnPropertyChanged(nameof(ActiveTabTasks));
        OnPropertyChanged(nameof(CompletedTabTasks));
        OnPropertyChanged(nameof(VisibleTasks));
        OnPropertyChanged(nameof(HasVisibleTasks));
        OnPropertyChanged(nameof(ActiveTabCount));
        OnPropertyChanged(nameof(CompletedTabCount));
        OnPropertyChanged(nameof(ActiveTabLabel));
        OnPropertyChanged(nameof(CompletedTabLabel));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(IsAllSelected));
    }

    /// <summary>首页「当前传输」只显示最近一个进行中的任务；界面文案与传输页保持一致。</summary>
    private void UpdateActive(IReadOnlyList<TransferTaskDto> ordered)
    {
        var active = ordered.FirstOrDefault(static d => TransferStates.IsActive(d.State));
        if (active is null)
        {
            ActiveName = string.Empty;
            ActiveDetail = string.Empty;
            ActivePercent = 0;
            return;
        }

        ActiveName = active.FileName;
        ActiveDetail = BuildDetail(active);
        ActivePercent = Math.Clamp(
            active.TransferredBytes * 100.0 / Math.Max(1, active.TotalBytes), 0, 100);
    }

    private TransferRowViewModel GetOrCreateRow(TransferTaskDto dto)
    {
        if (_rows.TryGetValue(dto.TaskId, out var row))
        {
            return row;
        }

        row = new TransferRowViewModel
        {
            TaskId = dto.TaskId,
            Direction = dto.Direction,
        };

        // 勾选状态变化要立刻反映到"已选 N 项"与删除按钮上
        row.PropertyChanged += OnRowPropertyChanged;

        _rows[dto.TaskId] = row;
        return row;
    }

    private void UpdateRow(TransferRowViewModel row, TransferTaskDto dto)
    {
        row.FileName = dto.FileName;
        row.Subtitle = dto.Direction == TransferDirections.Upload
            ? $"来自 {dto.DeviceName}"
            : $"发送到 {dto.DeviceName}";

        var (stateText, tone) = DescribeState(dto.State);
        row.StateText = stateText;
        row.Tone = tone;
        row.State = dto.State;
        row.CanCancel = TransferStates.IsActive(dto.State);
        row.CanPause = dto.State is TransferStates.Waiting or TransferStates.Preparing
            or TransferStates.Transferring or TransferStates.Verifying;
        row.CanResume = dto.State is TransferStates.Paused or TransferStates.Interrupted;

        row.Percent = dto.TotalBytes == 0
            ? (TransferStates.IsTerminal(dto.State) ? 100 : 0)
            : Math.Clamp(dto.TransferredBytes * 100.0 / dto.TotalBytes, 0, 100);

        row.Detail = BuildDetail(dto);
        row.ErrorText = dto.ErrorMessage ?? dto.ErrorCode ?? string.Empty;
        row.HasError = !string.IsNullOrEmpty(row.ErrorText);

        row.LocalPath = dto.LocalPath;
        row.CanReveal = !string.IsNullOrEmpty(dto.LocalPath) && File.Exists(dto.LocalPath);

        // 还在传输的不能删记录：先取消，否则临时文件会变孤儿
        row.CanDeleteRecord = !TransferStates.IsActive(dto.State);
    }

    private string BuildDetail(TransferTaskDto dto)
    {
        if (TransferStates.IsTerminal(dto.State))
        {
            return FormatBytes(dto.TotalBytes);
        }

        var speed = SampleSpeed(dto);
        var parts = new List<string>
        {
            $"{FormatBytes(dto.TransferredBytes)} / {FormatBytes(dto.TotalBytes)}",
            $"{Math.Floor(dto.TransferredBytes * 100.0 / Math.Max(1, dto.TotalBytes)):0}%",
        };

        if (speed > 0)
        {
            parts.Add($"{FormatBytes((long)speed)}/s");

            var remaining = (double)(dto.TotalBytes - dto.TransferredBytes);
            var seconds = remaining > 0 ? remaining / speed : 0;

            // 速度只来自真实字节增量；窗口归零时 speed 就是 0，这里连剩余时间一起不显示。
            if (remaining > 0 && double.IsFinite(seconds) && seconds <= MaxRemainingSeconds)
            {
                parts.Add($"剩余 {FormatDuration(seconds)}");
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>速度只来自真实字节增量；窗口内没有新增字节就是 0，绝不外推。</summary>
    private double SampleSpeed(TransferTaskDto dto)
    {
        if (!_samples.TryGetValue(dto.TaskId, out var window))
        {
            window = new SpeedWindow();
            _samples[dto.TaskId] = window;
        }

        return window.Sample(DateTimeOffset.UtcNow, dto.TransferredBytes);
    }

    private void ChooseFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要发送到手机的文件",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            _ = SendPathsAsync(dialog.FileNames);
        }
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要发送到手机的文件夹",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory
                .EnumerateFiles(dialog.FolderName, "*", SearchOption.AllDirectories)
                .Take(500)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "读取文件夹失败：" + ex.Message;
            return;
        }

        if (files.Length == 0)
        {
            StatusMessage = "该文件夹里没有文件。";
            return;
        }

        _ = SendPathsAsync(files);
    }

    private async Task SendPathsAsync(IReadOnlyList<string> paths)
    {
        var device = await _shell.GetPrimaryDeviceAsync();
        if (device is null)
        {
            StatusMessage = "请先在首页配对手机。";
            return;
        }

        if (device.SessionPaused)
        {
            StatusMessage = $"{device.DisplayName} 已被断开，在首页点「连接」后再发。";
            return;
        }

        // 离线时直接拒绝：任务会躺在队列里，等手机几小时后连上再突然开传
        if (!await _shell.IsDeviceOnlineAsync(device.DeviceId).ConfigureAwait(true))
        {
            StatusMessage = $"{device.DisplayName} 不在线，先让它连上电脑再发。";
            return;
        }

        Busy = true;
        var accepted = 0;
        var failed = 0;
        string? firstError = null;

        try
        {
            foreach (var path in paths)
            {
                var result = await _shell.Server.Transfers.CreateDownloadAsync(device, path);
                if (result.Success)
                {
                    accepted++;
                }
                else
                {
                    failed++;
                    firstError ??= $"{Path.GetFileName(path)}：{result.Error!.Message}";
                }
            }
        }
        finally
        {
            Busy = false;
        }

        StatusMessage = failed == 0
            ? $"已加入 {accepted} 个文件，等待手机接收。"
            : $"已加入 {accepted} 个，{failed} 个失败（{firstError}）。";

        await RefreshAsync();
    }

    private async Task PauseAsync(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return;
        }

        var device = await _shell.GetPrimaryDeviceAsync();
        if (device is null)
        {
            return;
        }

        var result = await _shell.Server.Transfers.PauseAsync(device, taskId);
        StatusMessage = result.Success ? "已暂停。" : result.Error!.Message;

        await RefreshAsync();
    }

    private async Task ResumeAsync(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return;
        }

        var device = await _shell.GetPrimaryDeviceAsync();
        if (device is null)
        {
            return;
        }

        var result = await _shell.Server.Transfers.ResumeAsync(device, taskId);
        StatusMessage = result.Success ? "已继续。" : result.Error!.Message;

        await RefreshAsync();
    }

    private async Task CancelAsync(string? taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return;
        }

        var device = await _shell.GetPrimaryDeviceAsync();
        if (device is null)
        {
            return;
        }

        var result = await _shell.Server.Transfers.CancelAsync(device, taskId);
        StatusMessage = result.Success ? "已取消。" : result.Error!.Message;

        if (result.Success && _latest.TryGetValue(taskId, out var dto))
        {
            _latest[taskId] = dto with
            {
                State = TransferStates.Cancelled,
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }

        await RefreshAsync();
    }

    // ------------------------------------------------------------ 页签与多选

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransferRowViewModel.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        // 表头那个三态复选框要跟着变：部分选中是横杠，全选是勾
        OnPropertyChanged(nameof(IsAllSelected));
    }

    private void SelectTab(string? key)
    {
        var tab = string.Equals(key, "completed", StringComparison.Ordinal)
            ? TransferTab.Completed
            : TransferTab.Active;

        if (tab == _tab)
        {
            return;
        }

        // 切页签时清空选择：勾选跨页保留会让"已选 N 项"里的 N 看不见
        ClearSelection();
        CurrentTab = tab;
    }

    public void ClearSelection()
    {
        foreach (var row in Tasks)
        {
            row.IsSelected = false;
        }
    }

    /// <summary>右键点在没勾选的行上：把它变成唯一选中项（资源管理器行为）。</summary>
    public void EnsureRowSelected(TransferRowViewModel row)
    {
        if (row.IsSelected)
        {
            return;
        }

        ClearSelection();
        row.IsSelected = true;
    }

    /// <summary>「在资源管理器中显示」：只对电脑上确实存在的文件有效。</summary>
    public void Reveal(TransferRowViewModel? row)
    {
        if (row?.LocalPath is null)
        {
            StatusMessage = "这条记录没有对应的电脑文件。";
            return;
        }

        if (!File.Exists(row.LocalPath))
        {
            StatusMessage = "文件已经不在了。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{row.LocalPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "定位传输文件失败 {Path}", row.LocalPath);
            StatusMessage = "打开资源管理器失败。";
        }
    }

    /// <summary>
    /// 删除传输记录。点在某一行上时：那一行没被勾选就只删它，否则删掉整批勾选的。
    ///
    /// 文件删除是**可选的**，并且只会删手机传来的文件（见引擎里的规则）；
    /// 电脑发出去的原文件永远不动。
    /// </summary>
    private async Task DeleteRecordsAsync(TransferRowViewModel? row)
    {
        var targets = row is not null && !row.IsSelected
            ? new[] { row }
            : SelectedRows.ToArray();

        if (targets.Length == 0)
        {
            // 在空白处右键（没点在某一行上）又没有勾选时，以前是静默返回——
            // 用户只看到"点了没反应"，以为删除坏了（1.5.8）
            StatusMessage = "先选中要删除的记录：右键某一行，或勾选后再删。";
            return;
        }

        var blocked = targets.Count(static t => !t.CanDeleteRecord);
        if (blocked > 0)
        {
            Dialogs.Alert("无法删除", $"有 {blocked} 条还在传输。请先取消，再删除记录。");
            return;
        }

        var localFiles = targets.Count(static t => t.CanReveal);
        var dialog = new Views.DeleteRecordsWindow(targets.Length, localFiles);
        if (dialog.ShowOverOwner(Application.Current?.MainWindow) != true)
        {
            return;
        }

        /*
         * 删记录**不需要设备在线**，也不需要设备还存在。
         *
         * 以前这里先取 `GetPrimaryDeviceAsync()`，取不到就 return——设备一被移除，
         * 界面上的历史记录就再也删不掉了，用户只看到「请先在首页配对手机」（1.5.8 真机）。
         * 走的是进程内重载（不校验归属）：界面显示什么，用户就能删什么。
         */
        var result = await _shell.Server.Transfers
            .DeleteRecordsAsync(targets.Select(static t => t.TaskId).ToArray(), dialog.DeleteFiles)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            StatusMessage = result.Error!.Message;
            return;
        }

        var value = result.Value!;

        // 立刻从列表里去掉，不等下一轮刷新
        foreach (var target in targets)
        {
            _latest.Remove(target.TaskId);
            _rows.Remove(target.TaskId);
            _samples.Remove(target.TaskId);
        }

        StatusMessage = value.FilesDeleted > 0
            ? $"已删除 {value.Deleted} 条记录、{value.FilesDeleted} 个文件。"
            : $"已删除 {value.Deleted} 条记录。";

        if (value.Rejected.Count > 0)
        {
            StatusMessage += $" {value.Rejected.Count} 条未删除：{value.Rejected[0]}";
        }

        // 再拉一次服务端列表收敛：本地已经清干净了，这一步保证"服务端也确实没有"
        // （记录本来就不存在时 deleted 会是 0，但界面同样不该留着那一行）
        await RefreshAsync().ConfigureAwait(true);
    }

    private void OpenSharedFolder()
    {
        var directory = _shell.SharedDirectory;

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开接收目录失败");
            StatusMessage = "打开接收目录失败。";
        }
    }

    private static (string Text, string Tone) DescribeState(string state) => state switch
    {
        TransferStates.Waiting => ("等待中", "idle"),
        TransferStates.Preparing => ("准备中", "idle"),
        TransferStates.Transferring => ("传输中", "ok"),
        TransferStates.Verifying => ("校验中", "ok"),
        TransferStates.Completed => ("已完成", "ok"),
        TransferStates.Failed => ("失败", "error"),
        TransferStates.Cancelled => ("已取消", "idle"),
        TransferStates.Interrupted => ("已中断", "idle"),
        _ => (state, "idle"),
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;

        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return value >= 100 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 1)
        {
            return "不到 1 秒";
        }

        if (seconds < 60)
        {
            return $"{seconds:0} 秒";
        }

        if (seconds < 3600)
        {
            return $"{seconds / 60:0} 分";
        }

        return $"{seconds / 3600:0.0} 小时";
    }

    /// <summary>
    /// 3 秒滑动窗口速度。
    ///
    /// 原来用指数平滑（0.6/0.4）：链路断开后平滑值只会无限逼近 0、永远不等于 0，
    /// 界面就会显示 "0 B/s · 剩余 1787683537762380000000000000000000 小时"（真机截图）。
    /// 换成一元窗口后，没有新增字节窗口自然归零，速度归零就不再算剩余时间，
    /// 也不需要任何"魔法下限"。
    /// </summary>
    private sealed class SpeedWindow
    {
        private const double WindowSeconds = 3.0;

        /// <summary>窗口跨度不足这个值时沿用上一次的速度，避免刚开始传输时抖动。</summary>
        private const double MinSpanSeconds = 0.3;

        private readonly Queue<(DateTimeOffset At, long Bytes)> _points = new();
        private long _lastBytes;
        private double _lastSpeed;

        public double Sample(DateTimeOffset now, long bytes)
        {
            if (_points.Count > 0 && bytes < _lastBytes)
            {
                // 进度回退（任务被取消后重建）：整窗丢弃重来，不能给出负速度
                _points.Clear();
                _lastSpeed = 0;
            }

            _lastBytes = bytes;
            _points.Enqueue((now, bytes));

            // 丢掉窗口外的点，至少留一个基准点
            while (_points.Count > 2 && (now - _points.Peek().At).TotalSeconds > WindowSeconds)
            {
                _points.Dequeue();
            }

            var oldest = _points.Peek();
            var span = (now - oldest.At).TotalSeconds;
            if (span >= MinSpanSeconds)
            {
                _lastSpeed = (bytes - oldest.Bytes) / span;
            }

            return _lastSpeed;
        }
    }
}

/// <summary>传输页的两个页签。失败/暂停/中断都归在「传输中」。</summary>
public enum TransferTab
{
    Active,
    Completed,
}
