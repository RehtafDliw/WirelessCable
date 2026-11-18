using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WirelessCable.Core.Domain;
using WirelessCable.Storage.Entities;

namespace WirelessCable.Desktop.Views;

/// <summary>
/// 按设备授权：先选模式（整机 / 目录），再选档位。
///
/// 与「共享目录 → 授权」的区别是**入口方向**：
/// 那边是"这个目录给哪些手机"，这里是"这台手机能看什么"——多设备时后者更符合直觉。
/// </summary>
public partial class DeviceGrantWindow : Window
{
    private readonly IReadOnlyList<SharedDirectoryOption> _directories;

    public DeviceGrantWindow(
        string deviceName,
        IReadOnlyList<SharedDirectoryOption> directories,
        int machineLevel,
        IReadOnlyDictionary<string, SharedDirectoryPermissions> folderGrants)
    {
        InitializeComponent();

        _directories = directories;
        TitleText.Text = $"授权给「{deviceName}」";

        MachineLevel.ItemsSource = MachineOptions;
        MachineLevel.DisplayMemberPath = "Label";
        MachineLevel.SelectedValuePath = "Value";
        MachineLevel.SelectedValue = machineLevel;

        FolderLevel.ItemsSource = PermissionOptions;
        FolderLevel.DisplayMemberPath = "Label";
        FolderLevel.SelectedValuePath = "Value";
        FolderLevel.SelectedValue = SharedDirectoryPermissions.ReadWrite;

        DirectoryBox.ItemsSource = _directories;
        DirectoryBox.SelectedIndex = _directories.Count > 0 ? 0 : -1;

        if (_directories.Count == 0)
        {
            FolderMode.IsEnabled = false;
            HintText.Text = "这台电脑还没有共享目录。先到「共享目录」页添加。";
        }

        ApplyFolderSelection(folderGrants);
    }

    /// <summary>用户最终选的是整机模式还是目录模式。</summary>
    public bool IsMachineMode => MachineMode.IsChecked == true;

    public int SelectedMachineLevel => MachineLevel.SelectedValue is int level ? level : MachineAccessLevels.Default;

    public SharedDirectoryOption? SelectedDirectory => DirectoryBox.SelectedItem as SharedDirectoryOption;

    public SharedDirectoryPermissions SelectedFolderPermissions
        => FolderLevel.SelectedValue is SharedDirectoryPermissions permissions
            ? permissions
            : SharedDirectoryPermissions.None;

    /// <summary>整机四档。与「共享目录」页同一套文案。</summary>
    public static IReadOnlyList<LevelOption> MachineOptions { get; } =
    [
        new LevelOption(MachineAccessLevels.Describe(MachineAccessLevels.ReadWriteDelete), MachineAccessLevels.ReadWriteDelete),
        new LevelOption(MachineAccessLevels.Describe(MachineAccessLevels.ReadWrite), MachineAccessLevels.ReadWrite),
        new LevelOption(MachineAccessLevels.Describe(MachineAccessLevels.ReadOnly), MachineAccessLevels.ReadOnly),
        new LevelOption(MachineAccessLevels.Describe(MachineAccessLevels.None), MachineAccessLevels.None),
    ];

    /// <summary>目录四档。删除单独一档，标签必须写出来。</summary>
    public static IReadOnlyList<PermissionOption> PermissionOptions { get; } =
    [
        new PermissionOption("无权限", SharedDirectoryPermissions.None),
        new PermissionOption("只读", SharedDirectoryPermissions.ReadOnly),
        new PermissionOption("读写（不能删）", SharedDirectoryPermissions.ReadWrite),
        new PermissionOption("读写 + 删除", SharedDirectoryPermissions.All),
    ];

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (MachinePanel is null || FolderPanel is null)
        {
            return;
        }

        var machine = MachineMode.IsChecked == true;
        MachinePanel.Visibility = machine ? Visibility.Visible : Visibility.Collapsed;
        FolderPanel.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = machine
            ? "整机访问让这台手机看到电脑的所有磁盘，权限档位决定能不能改、能不能删。"
            : "只放开选中的这一个目录；这台手机看不到别的目录。";
    }

    /// <summary>切换目录时把已有的授权带出来，避免"看着是无权限，保存却把原授权覆盖成无权限"。</summary>
    private void OnDirectoryChanged(object sender, SelectionChangedEventArgs e) => ApplyFolderSelection(null);

    private void ApplyFolderSelection(IReadOnlyDictionary<string, SharedDirectoryPermissions>? grants)
    {
        if (SelectedDirectory is null)
        {
            return;
        }

        if (grants is not null && grants.TryGetValue(SelectedDirectory.DirectoryId, out var current))
        {
            FolderLevel.SelectedValue = current;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!IsMachineMode && SelectedDirectory is null)
        {
            HintText.Text = "请选择一个共享目录。";
            return;
        }

        DialogResult = true;
        Close();
    }

    /// <summary>一个档位选项。</summary>
    public sealed record LevelOption(string Label, int Value);

    /// <summary>一个权限档位选项。</summary>
    public sealed record PermissionOption(string Label, SharedDirectoryPermissions Value);

    /// <summary>可授权的共享目录。</summary>
    public sealed record SharedDirectoryOption(
        string DirectoryId,
        string DisplayName,
        string RootPath);
}
