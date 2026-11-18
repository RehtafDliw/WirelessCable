using System.Windows;
using WirelessCable.Desktop.ViewModels;
using WirelessCable.Storage.Entities;

namespace WirelessCable.Desktop.Views;

/// <summary>目录授权对话框。按设备设置权限位。</summary>
public partial class GrantPermissionsWindow : Window
{
    private readonly List<DeviceGrantOption> _devices;
    private readonly string _directoryId;
    private readonly Dictionary<string, SharedDirectoryPermissions> _initial;

    public GrantPermissionsWindow(
        string directoryId,
        string directoryName,
        string directoryPath,
        List<DeviceGrantOption> devices,
        Dictionary<string, SharedDirectoryPermissions> initial)
    {
        InitializeComponent();

        _devices = devices;
        _directoryId = directoryId;
        _initial = initial;

        TitleText.Text = $"「{directoryName}」的设备授权";
        PathText.Text = directoryPath;

        foreach (var device in devices)
        {
            var current = initial.TryGetValue(device.DeviceId, out var permissions)
                ? permissions
                : SharedDirectoryPermissions.None;

            var row = new GrantRow(device, current);
            Rows.Items.Add(row);
        }

        if (Rows.Items.Count == 0)
        {
            PathText.Text = $"{directoryPath}（暂无已配对设备）";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // 这里不再为"授予删除"多弹一次确认：
        // 档位标签（读写 + 删除）与行内副标题（· 可以删除文件）已经把话说完了，
        // 真正的提示只留给动手的那一端——删除的那一刻。
        DialogResult = true;
        Close();
    }

    /// <summary>取回用户在对话框里选定的权限。</summary>
    public IReadOnlyDictionary<string, SharedDirectoryPermissions> CollectGrants()
    {
        var result = new Dictionary<string, SharedDirectoryPermissions>(StringComparer.Ordinal);
        foreach (var row in Rows.Items.OfType<GrantRow>())
        {
            result[row.Device.DeviceId] = row.SelectedValue;
        }

        return result;
    }

    /// <summary>一行：一个设备 + 一个权限选项。</summary>
    public sealed class GrantRow : ViewModelBase
    {
        private SharedDirectoryPermissions _selectedValue;

        public GrantRow(DeviceGrantOption device, SharedDirectoryPermissions current)
        {
            Device = device;
            _selectedValue = current;
        }

        public DeviceGrantOption Device { get; }

        public string DeviceName => Device.Display;

        /// <summary>副标题：配对状态 + 是否允许删除。删除是这四档里唯一的不可恢复动作，单独写出来。</summary>
        public string Detail
        {
            get
            {
                var paired = Device.IsPaired ? "已配对" : "未配对";
                return _selectedValue.HasFlag(SharedDirectoryPermissions.Delete)
                    ? $"{paired} · 可以删除文件"
                    : paired;
            }
        }

        /// <summary>
        /// 权限档位。第三档与第四档的唯一区别就是"删除"——
        /// 只写"读写/完全"时用户看不出差别，所以直接在标签和副标题里说清楚。
        /// </summary>
        public IReadOnlyList<PermissionOption> Options { get; } =
        [
            new PermissionOption("无权限", SharedDirectoryPermissions.None),
            new PermissionOption("只读", SharedDirectoryPermissions.ReadOnly),
            new PermissionOption("读写（不能删）", SharedDirectoryPermissions.ReadWrite),
            new PermissionOption("读写 + 删除", SharedDirectoryPermissions.All),
        ];

        public SharedDirectoryPermissions SelectedValue
        {
            get => _selectedValue;
            set
            {
                if (SetProperty(ref _selectedValue, value))
                {
                    OnPropertyChanged(nameof(Detail));
                }
            }
        }
    }

    /// <summary>一个权限选项。</summary>
    public sealed record PermissionOption(string Label, SharedDirectoryPermissions Value);
}
