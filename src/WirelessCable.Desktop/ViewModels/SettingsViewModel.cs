using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using WirelessCable.Desktop.Services;

namespace WirelessCable.Desktop.ViewModels;

/// <summary>
/// 设置页视图模型：网络、目录、安全、外观、日志、启动。
///
/// 目录改动只在下次启动生效（数据库、证书、日志在启动时就打开了），
/// 其余设置（主题、托盘、自启）都是即时生效。
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _shell;

    private string? _dataRoot;
    private string? _logDirectory;
    private bool _restartRequired;
    private bool _startWithWindows;
    private bool _closeToTray;
    private AppTheme _theme = AppTheme.System;

    public SettingsViewModel(MainViewModel shell)
    {
        _shell = shell;
        Shell = shell;

        var settings = DesktopSettingsStore.Load();
        _dataRoot = settings.DataRoot;
        _logDirectory = settings.LogDirectory;
        _startWithWindows = AutostartService.IsEnabled();
        _closeToTray = settings.CloseToTray;
        _theme = ThemeService.Parse(settings.Theme);

        ChangeDataDirectoryCommand = new RelayCommand(_ => ChangeDataDirectory());
        ChangeLogDirectoryCommand = new RelayCommand(_ => ChangeLogDirectory());
        RestartCommand = new RelayCommand(_ => Restart());
        SetThemeCommand = new RelayCommand(p => SetTheme(p as string));
    }

    public MainViewModel Shell { get; }

    public RelayCommand ChangeDataDirectoryCommand { get; }
    public RelayCommand ChangeLogDirectoryCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand SetThemeCommand { get; }

    // ------------------------------------------------------------ 目录

    /// <summary>数据根目录（下次启动生效的目标位置）。</summary>
    public string DataDirectoryText => string.IsNullOrWhiteSpace(_dataRoot)
        ? DesktopSettingsStore.DefaultRoot
        : _dataRoot!;

    /// <summary>日志目录（下次启动生效的目标位置）。</summary>
    public string LogDirectoryText => string.IsNullOrWhiteSpace(_logDirectory)
        ? Path.Combine(DesktopSettingsStore.DefaultRoot, "logs")
        : _logDirectory!;

    /// <summary>是否有仅在重启后才生效的改动。</summary>
    public bool RestartRequired
    {
        get => _restartRequired;
        private set => SetProperty(ref _restartRequired, value);
    }

    // ------------------------------------------------------------ 外观

    public bool IsThemeSystem => _theme == AppTheme.System;

    public bool IsThemeLight => _theme == AppTheme.Light;

    public bool IsThemeDark => _theme == AppTheme.Dark;

    private void SetTheme(string? value)
    {
        var theme = value switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.System,
        };

        if (theme == _theme)
        {
            return;
        }

        _theme = theme;
        ThemeService.Apply(theme);
        DesktopSettingsStore.Update(s => s.Theme = ThemeService.ToSetting(theme));

        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));

        Serilog.Log.Information("主题切换为 {Theme}", ThemeService.ToSetting(theme));
    }

    // ------------------------------------------------------------ 启动

    /// <summary>开机自启。写注册表失败会回滚并使开关弹回原位。</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value)
            {
                return;
            }

            if (!AutostartService.SetEnabled(value))
            {
                Dialogs.Alert("开机自启", "无法修改开机自启（可能被安全软件拦截）。");

                OnPropertyChanged(nameof(StartWithWindows));
                return;
            }

            _startWithWindows = value;
            DesktopSettingsStore.Update(s => s.StartWithWindows = value);
            OnPropertyChanged();
            Serilog.Log.Information("开机自启：{Enabled}", value);
        }
    }

    /// <summary>关闭窗口时收进托盘，而不是退出程序。</summary>
    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (!SetProperty(ref _closeToTray, value))
            {
                return;
            }

            DesktopSettingsStore.Update(s => s.CloseToTray = value);
            Serilog.Log.Information("关闭到托盘：{Enabled}", value);
        }
    }

    // ------------------------------------------------------------ 目录

    private void ChangeDataDirectory()
    {
        var picked = PickFolder("选择数据目录", DataDirectoryText);
        if (picked is null)
        {
            return;
        }

        // 选回默认位置就等于取消自定义，避免 settings.json 里留一条"看起来像自定义"的死值
        var value = string.Equals(
            Path.GetFullPath(picked).TrimEnd('\\'),
            Path.GetFullPath(DesktopSettingsStore.DefaultRoot).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : picked;

        if (string.Equals(value, _dataRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsInsideDefaultSubfolder(picked))
        {
            Dialogs.Alert("目录不可用", "这个位置在应用的默认数据目录里。请选一个独立文件夹（例如 D:\\WirelessCable）。");
            return;
        }

        if (!TryPrepare(value, out var error))
        {
            Dialogs.Alert("目录不可用", error);
            return;
        }

        if (!SaveSettings(s => s.DataRoot = value))
        {
            return;
        }

        _dataRoot = value;
        OnPropertyChanged(nameof(DataDirectoryText));
        RestartRequired = true;
        Serilog.Log.Information("数据目录已改为 {@Path}，等待重启生效", DataDirectoryText);
    }

    private void ChangeLogDirectory()
    {
        var picked = PickFolder("选择日志目录", LogDirectoryText);
        if (picked is null)
        {
            return;
        }

        var defaultLogs = Path.Combine(DesktopSettingsStore.DefaultRoot, "logs");
        var value = string.Equals(
            Path.GetFullPath(picked).TrimEnd('\\'),
            Path.GetFullPath(defaultLogs).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : picked;

        if (string.Equals(value, _logDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryPrepare(value, out var error))
        {
            Dialogs.Alert("目录不可用", error);
            return;
        }

        if (!SaveSettings(s => s.LogDirectory = value))
        {
            return;
        }

        _logDirectory = value;
        OnPropertyChanged(nameof(LogDirectoryText));
        RestartRequired = true;
        Serilog.Log.Information("日志目录已改为 {@Path}，等待重启生效", LogDirectoryText);
    }

    /// <summary>选到默认目录的 data/certs 子树里会让迁移边读边往自己里面拷，必须拦掉。</summary>
    private static bool IsInsideDefaultSubfolder(string path)
    {
        foreach (var name in new[] { "data", "certs" })
        {
            var parent = Path.Combine(DesktopSettingsStore.DefaultRoot, name);
            var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (fullPath.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? PickFolder(string title, string current)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(current) ? current : string.Empty,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>提前建目录并试写一个文件：路径不可写现在就告诉用户，不要等重启后起不来。</summary>
    private static bool TryPrepare(string? path, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".wc-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = $"该目录不可写：{ex.Message}";
            return false;
        }
    }

    private static bool SaveSettings(Action<DesktopSettings> mutate)
    {
        if (DesktopSettingsStore.Update(mutate))
        {
            return true;
        }

        Dialogs.Alert("无法保存设置", $"无法写入设置文件：{DesktopSettingsStore.FilePath}");
        return false;
    }

    private void Restart()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                Dialogs.Alert("需要重启", "请手动关闭并重新打开 WirelessCable。");
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "重启应用失败");
            Dialogs.Alert("重启失败", "请手动关闭并重新打开 WirelessCable。");
        }
    }
}
