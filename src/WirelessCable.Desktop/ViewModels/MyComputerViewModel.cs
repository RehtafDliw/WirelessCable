using System.Collections.ObjectModel;
using System.IO;
using Serilog;
using WirelessCable.Core.Abstractions;
using WirelessCable.Core.Domain;
using WirelessCable.Infrastructure.Files;
using WirelessCable.Desktop.Services;

namespace WirelessCable.Desktop.ViewModels;

/// <summary>「传输目录」里的根（当前只有接收目录）。</summary>
public sealed class LocalRootRowViewModel : ViewModelBase
{
    public required string RootKey { get; init; }

    public required string DisplayName { get; init; }

    public required string Path { get; init; }

    public required string Subtitle { get; init; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>本地文件列表的一行。</summary>
public sealed class LocalFileRowViewModel : ViewModelBase, ISortableFileEntry
{
    private bool _isChecked;

    public required string Name { get; init; }

    public required string RelativePath { get; init; }

    public required bool IsDirectory { get; init; }

    public long SizeBytes { get; init; }

    public long ModifiedUnixMs { get; init; }

    public string SizeText => IsDirectory ? string.Empty : PhoneFileRowViewModel.FormatSize(SizeBytes);

    /// <summary>类型列：文件夹 / 扩展名。</summary>
    public string TypeText
    {
        get
        {
            if (IsDirectory)
            {
                return "文件夹";
            }

            var extension = Path.GetExtension(Name);
            return extension.Length > 1 ? extension[1..].ToUpperInvariant() : "文件";
        }
    }

    public string ModifiedText => ModifiedUnixMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(ModifiedUnixMs).ToLocalTime().ToString("MM-dd HH:mm")
        : string.Empty;

    /// <summary>批量操作的勾选状态。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public static string DescribeEntry(FileEntry entry) => entry.Name;
}

/// <summary>
/// 「传输目录」：直接读本机磁盘，不经过服务端 HTTP。
/// 只列手机上传过来的文件（`共享目录/<设备名>/`），共享目录的浏览在「共享目录」页。
///
/// 手机看到的是这些目录的授权视图，电脑这边是同一批文件的本地视图——
/// 两边必须指向同一个物理目录，否则用户在电脑上删了文件手机还能看到。
/// </summary>
public sealed class MyComputerViewModel : ViewModelBase
{
    private const int PageSize = 300;

    private readonly MainViewModel _shell;

    private string _currentRootKey = string.Empty;
    private string _currentPath = string.Empty;
    private string _statusText = string.Empty;
    private bool _hasStatus;
    private bool _busy;
    private string _sortField = FileSortFields.Name;
    private bool _sortDescending;
    private int _pageIndex;
    private List<LocalFileRowViewModel> _view = [];

    public MyComputerViewModel(MainViewModel shell) : this()
    {
        _shell = shell;
        Shell = shell;
    }

    public MyComputerViewModel()
    {
        SelectRootCommand = new RelayCommand(p => _ = SelectRootAsync(p as string));
        OpenCommand = new RelayCommand(p => _ = OpenAsync(p as LocalFileRowViewModel));
        GoUpCommand = new RelayCommand(_ => _ = GoUpAsync());
        CreateDirectoryCommand = new RelayCommand(_ => CreateDirectory());
        RenameCommand = new RelayCommand(p => _ = RenameAsync(p as LocalFileRowViewModel));
        DeleteCommand = new RelayCommand(p => _ = DeleteAsync(p as LocalFileRowViewModel));
        DeleteSelectedCommand = new RelayCommand(_ => _ = DeleteSelectedAsync());
        SelectAllCommand = new RelayCommand(p => SelectAll(p as string));
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        SortCommand = new RelayCommand(p => ApplySort(p as string));
        PrevPageCommand = new RelayCommand(_ => MovePage(-1));
        NextPageCommand = new RelayCommand(_ => MovePage(1));
        OpenInExplorerCommand = new RelayCommand(_ => OpenInExplorer());
        RevealCommand = new RelayCommand(p => Reveal(p as LocalFileRowViewModel));
    }

    public MainViewModel Shell { get; }

    public ObservableCollection<LocalRootRowViewModel> Roots { get; } = [];

    public ObservableCollection<LocalFileRowViewModel> Entries { get; } = [];

    public RelayCommand SelectRootCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand GoUpCommand { get; }
    public RelayCommand CreateDirectoryCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SortCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand OpenInExplorerCommand { get; }
    public RelayCommand RevealCommand { get; }

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

    public string PathDisplay => string.IsNullOrEmpty(CurrentPath) ? "/" : "/" + CurrentPath;

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

    public bool Busy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    public string NameSortText => DescribeSort("名称", FileSortFields.Name);

    public string SizeSortText => DescribeSort("大小", FileSortFields.Size);

    public string TimeSortText => DescribeSort("时间", FileSortFields.Modified);

    public int PageIndex => _pageIndex + 1;

    public int PageCount => FileListOrder.PageCount(_view.Count, PageSize);

    public string PageText => $"{PageIndex}/{PageCount}";

    public bool HasPages => PageCount > 1;

    public int CheckedCount => Entries.Count(static e => e.IsChecked);

    public string CheckedText => CheckedCount > 0 ? $"已选 {CheckedCount} 项" : string.Empty;

    public bool HasChecked => CheckedCount > 0;

    /// <summary>当前页条目（过滤/排序/分页都在 <see cref="RefreshView"/> 里做完）。</summary>
    public IEnumerable<LocalFileRowViewModel> VisibleEntries
        => FileListOrder.Page(_view, _pageIndex, PageSize);

    public async Task RefreshAsync()
    {
        await LoadRootsAsync().ConfigureAwait(true);
        await LoadEntriesAsync().ConfigureAwait(true);
    }

    private async Task LoadRootsAsync()
    {
        var previous = _currentRootKey;
        Roots.Clear();

        // 这一页只放"传输目录"：手机上传过来的文件都落在这里，
        // 按设备名各一个文件夹。共享目录的管理与浏览在「共享目录」页，
        // 两处都列一遍只会让人分不清哪个才是"我从手机收到的文件"。
        Roots.Add(new LocalRootRowViewModel
        {
            RootKey = "received",
            DisplayName = "传输目录",
            Path = _shell.SharedDirectory,
            Subtitle = "手机上传的文件",
        });

        await Task.CompletedTask.ConfigureAwait(true);

        var select = Roots.FirstOrDefault(r => r.RootKey == previous) ?? Roots.FirstOrDefault();
        if (select is null)
        {
            _currentRootKey = string.Empty;
            CurrentPath = string.Empty;
            return;
        }

        _currentRootKey = select.RootKey;
        if (!string.Equals(previous, select.RootKey, StringComparison.Ordinal))
        {
            CurrentPath = string.Empty;
        }

        foreach (var root in Roots)
        {
            root.IsSelected = root.RootKey == _currentRootKey;
        }
    }

    private async Task SelectRootAsync(string? rootKey)
    {
        if (string.IsNullOrEmpty(rootKey) || rootKey == _currentRootKey)
        {
            return;
        }

        _currentRootKey = rootKey;
        CurrentPath = string.Empty;
        foreach (var root in Roots)
        {
            root.IsSelected = root.RootKey == rootKey;
        }

        await LoadEntriesAsync().ConfigureAwait(true);
    }

    private IFileProvider? CurrentProvider()
    {
        var root = Roots.FirstOrDefault(r => r.RootKey == _currentRootKey);
        return root is null ? null : new LocalFileProvider(root.Path);
    }

    private async Task LoadEntriesAsync()
    {
        var provider = CurrentProvider();
        if (provider is null)
        {
            Entries.Clear();
            RefreshView();
            return;
        }

        try
        {
            var result = await provider.ListDirectoryAsync(CurrentPath, CancellationToken.None)
                .ConfigureAwait(true);

            Entries.Clear();

            if (!result.Success)
            {
                SetStatus(FailureText(result));
                RefreshView();
                return;
            }

            foreach (var entry in result.Value!)
            {
                Entries.Add(new LocalFileRowViewModel
                {
                    Name = entry.Name,
                    RelativePath = entry.RelativePath,
                    IsDirectory = entry.IsDirectory,
                    SizeBytes = entry.SizeBytes,
                    ModifiedUnixMs = entry.LastModifiedUtc.ToUnixTimeMilliseconds(),
                });
            }

            RefreshView();
            ClearStatus();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取本机目录失败 {Path}", CurrentPath);
            Entries.Clear();
            RefreshView();
            SetStatus("读取失败：" + ex.Message);
        }
    }

    private async Task OpenAsync(LocalFileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsDirectory)
        {
            CurrentPath = row.RelativePath;
            await LoadEntriesAsync().ConfigureAwait(true);
            return;
        }

        OpenPath(row.RelativePath);
    }

    private async Task GoUpAsync()
    {
        if (string.IsNullOrEmpty(CurrentPath))
        {
            return;
        }

        var index = CurrentPath.LastIndexOf('/');
        CurrentPath = index <= 0 ? string.Empty : CurrentPath[..index];
        await LoadEntriesAsync().ConfigureAwait(true);
    }

    private void CreateDirectory()
    {
        var dialog = new Views.InputDialog("文件夹名称");
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }

        _ = RunAsync(async provider =>
        {
            var name = dialog.Value!.Trim();
            var path = string.IsNullOrEmpty(CurrentPath) ? name : CurrentPath + "/" + name;
            var result = await provider.CreateDirectoryAsync(path, CancellationToken.None).ConfigureAwait(true);
            return result.Success ? null : FailureText(result);
        });
    }

    private async Task RenameAsync(LocalFileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var dialog = new Views.InputDialog("新名称", row.Name);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }

        var provider = CurrentProvider();
        if (provider is null)
        {
            return;
        }

        var result = await provider
            .RenameAsync(row.RelativePath, dialog.Value!.Trim(), CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Success)
        {
            SetStatus(FailureText(result));
            return;
        }

        await LoadEntriesAsync().ConfigureAwait(true);
    }

    private Task DeleteAsync(LocalFileRowViewModel? row)
    {
        if (row is null)
        {
            return Task.CompletedTask;
        }

        var confirmed = Dialogs.Confirm(
            "删除文件",
            $"删除「{row.Name}」？删除后无法恢复。",
            confirmText: "删除",
            danger: true);

        if (!confirmed)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async provider =>
        {
            var result = await provider
                .DeleteAsync(row.RelativePath, recursive: row.IsDirectory, CancellationToken.None)
                .ConfigureAwait(true);

            return result.Success ? null : FailureText(result);
        });
    }

    private Task DeleteSelectedAsync()
    {
        var selected = Entries.Where(static e => e.IsChecked).ToArray();
        if (selected.Length == 0)
        {
            return Task.CompletedTask;
        }

        var confirmed = Dialogs.Confirm(
            "批量删除",
            $"删除选中的 {selected.Length} 项？删除后无法恢复。",
            confirmText: "删除",
            danger: true);

        if (!confirmed)
        {
            return Task.CompletedTask;
        }

        return RunAsync(async provider =>
        {
            var failed = 0;
            foreach (var row in selected)
            {
                var result = await provider
                    .DeleteAsync(row.RelativePath, recursive: row.IsDirectory, CancellationToken.None)
                    .ConfigureAwait(true);

                if (!result.Success)
                {
                    failed++;
                }
            }

            return failed == 0 ? null : $"{failed} 项删除失败（可能被占用或没有权限）。";
        });
    }

    /// <summary>删除/新建这类动作共用：跑一次、带上忙碌态、失败给原因、完成后重读目录。</summary>
    private async Task RunAsync(Func<IFileProvider, Task<string?>> action)
    {
        var provider = CurrentProvider();
        if (provider is null)
        {
            return;
        }

        try
        {
            Busy = true;
            var error = await action(provider).ConfigureAwait(true);
            if (error is not null)
            {
                SetStatus(error);
            }
            else
            {
                ClearStatus();
            }

            await LoadEntriesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "本机文件操作失败");
            SetStatus("操作失败：" + ex.Message);
        }
        finally
        {
            Busy = false;
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

    private void RefreshView()
    {
        _view = Entries.ToList();
        _view.Sort((l, r) => FileListOrder.Compare(l, r, _sortField, _sortDescending));
        _pageIndex = 0;

        // 勾选状态跟着视图刷新，否则"已选 N 项"会和列表对不上
        NotifyChecked();
        OnPropertyChanged(nameof(VisibleEntries));
        OnPropertyChanged(nameof(PageIndex));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(NameSortText));
        OnPropertyChanged(nameof(SizeSortText));
        OnPropertyChanged(nameof(TimeSortText));
    }

    private void NotifyChecked()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(CheckedText));
    }

    private string DescribeSort(string label, string field)
        => _sortField == field ? $"{label} {(_sortDescending ? "↓" : "↑")}" : label;

    private void OpenPath(string relativePath)
    {
        var root = Roots.FirstOrDefault(r => r.RootKey == _currentRootKey);
        if (root is null)
        {
            return;
        }

        var full = Path.Combine(root.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = full,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开文件失败 {@Path}", relativePath);
            SetStatus("打不开这个文件，可能已被移动或没有关联程序。");
        }
    }

    /// <summary>右键「在资源管理器中显示」：用 /select 直接选中该条目。</summary>
    private void Reveal(LocalFileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var full = FullPathOf(row);
        if (full is null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{full}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "在资源管理器中定位失败 {@Path}", full);
        }
    }

    /// <summary>右键「属性」：本地文件直接读磁盘，不再走服务端。</summary>
    public void ShowProperties(LocalFileRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var full = FullPathOf(row);
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

        if (!string.IsNullOrEmpty(full))
        {
            lines.Add($"位置：{Path.GetDirectoryName(full)}");
        }

        Dialogs.Alert("属性", string.Join(Environment.NewLine, lines));
    }

    /// <summary>条目在当前根下的绝对路径；根不存在时返回 null。</summary>
    private string? FullPathOf(LocalFileRowViewModel row)
    {
        var root = Roots.FirstOrDefault(r => r.RootKey == _currentRootKey);
        if (root is null)
        {
            return null;
        }

        return Path.Combine(root.Path, row.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private void OpenInExplorer()
    {
        var root = Roots.FirstOrDefault(r => r.RootKey == _currentRootKey);
        if (root is null)
        {
            return;
        }

        var full = string.IsNullOrEmpty(CurrentPath)
            ? root.Path
            : Path.Combine(root.Path, CurrentPath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = full,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开资源管理器失败 {@Path}", full);
        }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        HasStatus = true;
    }

    private void ClearStatus()
    {
        StatusText = string.Empty;
        HasStatus = false;
    }

    /// <summary>失败文案：优先用提供方给的中文说明，没有才按状态翻译。</summary>
    public static string FailureText(FileOperationResult result)
        => string.IsNullOrWhiteSpace(result.Message) ? DescribeFailure(result.Status) : result.Message;

    public static string FailureText<T>(FileOperationResult<T> result)
        => string.IsNullOrWhiteSpace(result.Message) ? DescribeFailure(result.Status) : result.Message;

    /// <summary>把状态翻成人话，不要把枚举名直接丢给用户。</summary>
    public static string DescribeFailure(FileOperationStatus status) => status switch
    {
        FileOperationStatus.NotFound => "目标不存在，可能已被移动或删除。",
        FileOperationStatus.PermissionDenied => "没有权限访问这个位置。",
        FileOperationStatus.AlreadyExists => "同名文件或文件夹已存在。",
        FileOperationStatus.Unsupported => "当前存储不支持这个操作。",
        FileOperationStatus.PathInvalid => "路径不合法。",
        FileOperationStatus.AuthorizationRevoked => "授权已失效，请重新授权该目录。",
        FileOperationStatus.StorageUnavailable => "存储不可用，检查磁盘是否被拔出。",
        FileOperationStatus.IoFailure => "磁盘读写失败，请检查磁盘状态。",
        _ => "操作失败。",
    };
}
