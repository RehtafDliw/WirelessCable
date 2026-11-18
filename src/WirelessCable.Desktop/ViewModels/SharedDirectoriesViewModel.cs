using WirelessCable.Core.Domain;
using WirelessCable.Desktop.Services;

namespace WirelessCable.Desktop.ViewModels;

/// <summary>共享目录列表里的一行。</summary>
public sealed class SharedDirectoryRowViewModel : ViewModelBase
{
    private string _permissionsText = "未授权";
    private string _tone = "idle";
    private string _grantsText = "无设备授权";
    private bool _hasGrants;

    public required string DirectoryId { get; init; }

    public required string DisplayName { get; init; }

    public required string RootPath { get; init; }

    public required string CreatedText { get; init; }

    /// <summary>本机对这台设备的授权摘要（多设备时逗号分隔）。</summary>
    public string PermissionsText
    {
        get => _permissionsText;
        set => SetProperty(ref _permissionsText, value);
    }

    public string Tone
    {
        get => _tone;
        set => SetProperty(ref _tone, value);
    }

    /// <summary>每台已配对设备的授权情况。</summary>
    public string GrantsText
    {
        get => _grantsText;
        set => SetProperty(ref _grantsText, value);
    }

    public bool HasGrants
    {
        get => _hasGrants;
        set => SetProperty(ref _hasGrants, value);
    }
}

/// <summary>下拉框里的一项：整机访问档位。</summary>
public sealed record MachineAccessOption(int Level, string Text);

/// <summary>整机访问的一行：一台已配对设备。</summary>
public sealed class MachineAccessRowViewModel : ViewModelBase
{
    private int _level;

    public required string DeviceId { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>用户改了档位时回调，由列表 VM 负责落库。</summary>
    public Action<MachineAccessRowViewModel, int>? OnLevelChanged { get; init; }

    /// <summary>该设备对"此电脑"的访问档位：0 关闭 / 1 只读 / 2 可读写。</summary>
    public int Level
    {
        get => _level;
        set
        {
            if (SetProperty(ref _level, value))
            {
                OnPropertyChanged(nameof(LevelText));
                OnLevelChanged?.Invoke(this, value);
            }
        }
    }

    public string LevelText => MachineAccessLevels.Describe(Level);

    public IReadOnlyList<MachineAccessOption> Options { get; } =
    [
        new(MachineAccessLevels.ReadWriteDelete, MachineAccessLevels.Describe(MachineAccessLevels.ReadWriteDelete)),
        new(MachineAccessLevels.ReadWrite, MachineAccessLevels.Describe(MachineAccessLevels.ReadWrite)),
        new(MachineAccessLevels.ReadOnly, MachineAccessLevels.Describe(MachineAccessLevels.ReadOnly)),
        new(MachineAccessLevels.None, MachineAccessLevels.Describe(MachineAccessLevels.None)),
    ];
}

/// <summary>设备授权下拉里的一项。</summary>
public sealed record DeviceGrantOption(string DeviceId, string DisplayName, bool IsPaired)
{
    public string Display => IsPaired ? DisplayName : $"{DisplayName}（未配对）";
}

/// <summary>
/// 共享目录管理视图模型。
///
/// 职责：添加/移除共享目录、按设备配置权限位。
/// 权限是**按设备**的——同一台电脑上，A 手机可以只读、B 手机可以读写。
/// </summary>
public sealed class SharedDirectoriesViewModel : ViewModelBase
{
    private readonly MainViewModel _shell;

    public SharedDirectoriesViewModel(MainViewModel shell) : this()
    {
        _shell = shell;
        Shell = shell;
    }

    private string _statusText = string.Empty;
    private bool _hasStatus;
    private bool _busy;

    public MainViewModel Shell { get; }

    public System.Collections.ObjectModel.ObservableCollection<SharedDirectoryRowViewModel> Rows { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<DeviceGrantOption> Devices { get; } = [];

    /// <summary>整机浏览开关：每台已配对设备一行。</summary>
    public System.Collections.ObjectModel.ObservableCollection<MachineAccessRowViewModel> MachineAccessRows { get; } = [];

    /// <summary>最近一次操作的结果或失败原因。</summary>
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

    public RelayCommand AddDirectoryCommand { get; }

    public RelayCommand RemoveDirectoryCommand { get; }

    public RelayCommand SetPermissionCommand { get; }

    public RelayCommand EditGrantsCommand { get; }

    /// <summary>切换某台设备的整机浏览开关。</summary>

    public SharedDirectoriesViewModel()
    {
        AddDirectoryCommand = new RelayCommand(_ => _ = AddDirectoryAsync());
        RemoveDirectoryCommand = new RelayCommand(p => _ = RemoveDirectoryAsync(p as string));
        SetPermissionCommand = new RelayCommand(p => _ = SetPermissionAsync(p as SharedDirectoryGrantChange));
        EditGrantsCommand = new RelayCommand(p => EditGrants(p as string));
    }

    /// <summary>刷新目录与设备列表。</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var directories = await _shell.Server.SharedDirectories.ListAsync().ConfigureAwait(true);
            var devices = await _shell.Server.SharedDirectories.ListAsync().ConfigureAwait(true);

            var stored = await _shell.GetDeviceNamesAsync().ConfigureAwait(true);

            Devices.Clear();
            foreach (var (deviceId, name) in stored)
            {
                Devices.Add(new DeviceGrantOption(deviceId, name, IsPaired: true));
            }

            MachineAccessRows.Clear();
            foreach (var device in await _shell.Server.Devices.ListAsync().ConfigureAwait(true))
            {
                if (!device.IsPaired || device.IsRevoked)
                {
                    continue;
                }

                MachineAccessRows.Add(new MachineAccessRowViewModel
                {
                    DeviceId = device.DeviceId,
                    DisplayName = device.DisplayName,
                    Level = MachineAccessLevels.Normalize(device.MachineAccess),
                    OnLevelChanged = (row, level) => _ = SetMachineAccessAsync(row, level),
                });
            }

            Rows.Clear();
            foreach (var directory in directories)
            {
                var grants = await _shell.Server.SharedDirectories
                    .ListGrantsAsync(directory.DirectoryId).ConfigureAwait(true);

                var names = new List<string>();
                foreach (var (deviceId, permissions) in grants)
                {
                    if (permissions == WirelessCable.Storage.Entities.SharedDirectoryPermissions.None)
                    {
                        continue;
                    }

                    var name = stored.TryGetValue(deviceId, out var known) ? known : deviceId;
                    names.Add($"{name}：{DescribePermissions(permissions)}");
                }

                Rows.Add(new SharedDirectoryRowViewModel
                {
                    DirectoryId = directory.DirectoryId,
                    DisplayName = directory.DisplayName,
                    RootPath = directory.RootPath,
                    CreatedText = directory.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm"),
                    GrantsText = names.Count == 0 ? "无设备授权" : string.Join("；", names),
                    HasGrants = names.Count > 0,
                    Tone = names.Count == 0 ? "idle" : "ok",
                    PermissionsText = names.Count == 0 ? "未授权" : $"{names.Count} 台设备",
                });
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "刷新共享目录失败");
            SetStatus("共享目录读取失败");
        }
    }

    /// <summary>改某台设备对"此电脑"的访问档位。</summary>
    private async Task SetMachineAccessAsync(MachineAccessRowViewModel row, int level)
    {
        try
        {
            await _shell.Server.Devices
                .SetMachineAccessAsync(row.DeviceId, level)
                .ConfigureAwait(true);

            Serilog.Log.Information(
                "整机访问改为 {Level} device={DeviceId}",
                MachineAccessLevels.Describe(level),
                row.DeviceId);

            // 含删除的档位要把后果说在明面上，而不是只说"可读写 + 删除"
            SetStatus(MachineAccessLevels.CanDelete(level)
                ? $"「{row.DisplayName}」对这台电脑：{MachineAccessLevels.Describe(level)}，可以删除任意文件"
                : $"「{row.DisplayName}」对这台电脑：{MachineAccessLevels.Describe(level)}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "设置整机访问档位失败");
            SetStatus("设置失败");
        }
    }

    private async Task AddDirectoryAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要共享的文件夹",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = dialog.FolderName;
        try
        {
            var repository = _shell.Server.SharedDirectories;
            var existing = await repository.FindByPathAsync(path).ConfigureAwait(true);
            if (existing is not null)
            {
                SetStatus("该目录已在共享列表中");
                return;
            }

            var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name))
            {
                name = path;
            }

            var record = new WirelessCable.Storage.Entities.SharedDirectoryRecord
            {
                DirectoryId = Guid.NewGuid().ToString("N"),
                DisplayName = name,
                RootPath = path,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            await repository.UpsertAsync(record).ConfigureAwait(true);
            Serilog.Log.Information("已添加共享目录 {@Path}", path);
            SetStatus($"已共享「{name}」，接下来给设备授权");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "添加共享目录失败");
            SetStatus("添加共享目录失败");
        }
    }

    private async Task RemoveDirectoryAsync(string? directoryId)
    {
        if (string.IsNullOrEmpty(directoryId))
        {
            return;
        }

        var row = Rows.FirstOrDefault(r => r.DirectoryId == directoryId);
        var confirmed = Dialogs.Confirm(
            "移除共享目录",
            $"移除后手机将立即无法访问「{row?.DisplayName}」。文件本身不会被删除。",
            confirmText: "移除",
            danger: true);

        if (!confirmed)
        {
            return;
        }

        try
        {
            await _shell.Server.SharedDirectories.DeleteAsync(directoryId).ConfigureAwait(true);
            Serilog.Log.Information("已移除共享目录 {DirectoryId}", directoryId);
            SetStatus("已移除共享目录");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "移除共享目录失败");
            SetStatus("移除共享目录失败");
        }
    }

    /// <summary>打开授权对话框，按设备设置权限位。</summary>
    private void EditGrants(string? directoryId)
    {
        if (string.IsNullOrEmpty(directoryId))
        {
            return;
        }

        var row = Rows.FirstOrDefault(r => r.DirectoryId == directoryId);
        if (row is null)
        {
            return;
        }

        try
        {
            var grants = _shell.Server.SharedDirectories
                .ListGrantsAsync(directoryId)
                .GetAwaiter()
                .GetResult()
                .ToDictionary(pair => pair.DeviceId, pair => pair.Permissions, StringComparer.Ordinal);

            var window = new WirelessCable.Desktop.Views.GrantPermissionsWindow(
                directoryId,
                row.DisplayName,
                row.RootPath,
                Devices.ToList(),
                grants);

            if (window.ShowDialog() == true)
            {
                var selected = window.CollectGrants();
                foreach (var (deviceId, permissions) in selected)
                {
                    _shell.Server.SharedDirectories
                        .SetGrantAsync(directoryId, deviceId, permissions)
                        .GetAwaiter()
                        .GetResult();
                }

                Serilog.Log.Information("已更新目录授权 dir={DirectoryId}", directoryId);
                SetStatus("授权已保存");
                _ = RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "保存目录授权失败");
            SetStatus("授权保存失败");
        }
    }

    private async Task SetPermissionAsync(SharedDirectoryGrantChange? change)
    {
        if (change is null)
        {
            return;
        }

        try
        {
            await _shell.Server.SharedDirectories
                .SetGrantAsync(change.DirectoryId, change.DeviceId, change.Permissions)
                .ConfigureAwait(true);

            Serilog.Log.Information(
                "已更新目录授权 dir={DirectoryId} device={DeviceId} perms={Permissions}",
                change.DirectoryId, change.DeviceId, change.Permissions);

            SetStatus("授权已保存");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "更新授权失败");
            SetStatus("授权保存失败");
        }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        HasStatus = true;
    }

    /// <summary>权限位 → 人话。</summary>
    public static string DescribePermissions(WirelessCable.Storage.Entities.SharedDirectoryPermissions permissions)
    {
        if (permissions == WirelessCable.Storage.Entities.SharedDirectoryPermissions.None)
        {
            return "无权限";
        }

        var parts = new List<string>();
        if (permissions.HasFlag(WirelessCable.Storage.Entities.SharedDirectoryPermissions.Browse))
        {
            parts.Add("浏览");
        }

        if (permissions.HasFlag(WirelessCable.Storage.Entities.SharedDirectoryPermissions.Download))
        {
            parts.Add("下载");
        }

        if (permissions.HasFlag(WirelessCable.Storage.Entities.SharedDirectoryPermissions.Upload))
        {
            parts.Add("上传");
        }

        if (permissions.HasFlag(WirelessCable.Storage.Entities.SharedDirectoryPermissions.Create))
        {
            parts.Add("新建");
        }

        if (permissions.HasFlag(WirelessCable.Storage.Entities.SharedDirectoryPermissions.Rename))
        {
            parts.Add("重命名");
        }

        if (permissions.HasFlag(WirelessCable.Storage.Entities.SharedDirectoryPermissions.Move))
        {
            parts.Add("移动");
        }

        if (permissions.HasFlag(WirelessCable.Storage.Entities.SharedDirectoryPermissions.Delete))
        {
            parts.Add("删除");
        }

        return parts.Count == 0 ? "无权限" : string.Join("/", parts);
    }
}

/// <summary>一次授权变更。</summary>
public sealed record SharedDirectoryGrantChange(
    string DirectoryId,
    string DeviceId,
    WirelessCable.Storage.Entities.SharedDirectoryPermissions Permissions);
