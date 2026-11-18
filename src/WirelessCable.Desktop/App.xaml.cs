using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using WirelessCable.Core.Diagnostics;
using WirelessCable.Desktop.Services;
using WirelessCable.Desktop.ViewModels;
using WirelessCable.Infrastructure.Certificates;
using WirelessCable.Infrastructure.Logging;
using WirelessCable.Infrastructure.Paths;
using WirelessCable.Infrastructure.Networking;
using WirelessCable.Infrastructure.Security;
using WirelessCable.Protocol;
using WirelessCable.Server;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;

namespace WirelessCable.Desktop;

/// <summary>
/// 组合根：建目录、起日志、装依赖、启动服务、显示窗口。
/// 退出时确保服务停止、端口释放、日志落盘。
/// </summary>
public partial class App : Application
{
    /// <summary>单实例互斥名。不用 Global\ 前缀：多用户会话各自独立更安全。</summary>
    private const string InstanceMutexName = "WirelessCable.Desktop.SingleInstance";

    private static Mutex? _instanceMutex;
    private readonly WindowsHotspotService _hotspot = new();
    private WirelessCableServer? _server;
    private MainViewModel? _viewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 提权子模式：只配防火墙，不开界面、不起服务
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length >= 2 && string.Equals(arguments[1], "--configure-firewall", StringComparison.Ordinal))
        {
            var port = arguments.Length >= 3 && int.TryParse(arguments[2], out var parsed)
                ? parsed
                : ProtocolConstants.DefaultHttpsPort;

            Shutdown(FirewallConfigurator.ApplyAndReport(port));
            return;
        }

        // 单实例：第二个进程若另起服务，会退到 47111 并广播同名 mDNS，
        // 用户对着 A 窗口的二维码扫码却连到 B —— 现场踩过，必须拦住。
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            ActivateExistingWindow();

            // 上一个实例可能正在退出（关窗 → 停服务有几秒），此时接管互斥继续启动，
            // 否则会出现"刚关掉就双击打不开"的错觉。
            var tookOver = false;
            try
            {
                tookOver = _instanceMutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                tookOver = true;
            }

            if (!tookOver)
            {
                Shutdown(0);
                return;
            }
        }

        RegisterGlobalExceptionHandlers();

        AppPaths paths;
        string? directoryWarning = null;
        int? migratedFiles = null;
        var settings = WirelessCable.Desktop.Services.DesktopSettingsStore.Load();

        // 主题在建窗口之前就要定好，否则会先闪一下浅色再切深色
        WirelessCable.Desktop.Services.ThemeService.Apply(
            WirelessCable.Desktop.Services.ThemeService.Parse(settings.Theme));

        var portable = WirelessCable.Infrastructure.Paths.PortableLayout.IsPortable;

        try
        {
            // 绿色版的数据位置由程序目录决定：忽略"自定义数据目录"设置，
            // 否则整包迁移时数据还留在原来那台机器的路径上，搬过去就空了。
            if (portable)
            {
                // 换成便携版的第一次启动：把原来的库与证书搬进来，不让用户重新配对
                migratedFiles = WirelessCable.Desktop.Services.PathMigration.CopyInstalledDataToPortable();
            }
            else if (!string.IsNullOrWhiteSpace(settings.DataRoot))
            {
                // 第一次在新目录启动：把库与证书搬过去，否则配对关系全丢
                migratedFiles = WirelessCable.Desktop.Services.PathMigration
                    .CopyDefaultDataIfNeeded(settings.DataRoot!);
            }

            paths = new AppPaths(
                dataRootOverride: portable ? null : settings.DataRoot,
                logsOverride: portable ? null : settings.LogDirectory);
            paths.EnsureCreated();

            if (portable)
            {
                Log.Information("绿色版模式：数据目录 {Root}", paths.RootDirectory);
            }
        }
        catch (Exception ex)
        {
            // 自定义目录不可用（移动硬盘拔了、权限不足）不能让程序起不来：
            // 退回默认目录，界面照常可用
            try
            {
                paths = new AppPaths();
                paths.EnsureCreated();
                directoryWarning = ex.Message;
            }
            catch (Exception fatal)
            {
                Dialogs.Alert("无法启动", $"无法创建应用目录：{fatal.Message}");
                Shutdown(1);
                return;
            }
        }

        Log.Logger = LoggingSetup.Create(paths).CreateLogger();
        Log.Information("WirelessCable 启动，版本 {Version}", DisplayVersion);

        if (migratedFiles is not null)
        {
            Log.Information("已把默认目录的数据迁到 {@Data}，文件 {Count} 个", paths.DataDirectory, migratedFiles);
        }

        if (directoryWarning is not null)
        {
            Log.Warning("自定义数据目录不可用，已退回默认目录：{Error}", directoryWarning);
            Dialogs.Alert("数据目录不可用", $"无法使用设置里的数据目录，已改用默认目录。\n\n{directoryWarning}");
        }

        var database = new SqliteDatabaseFactory(paths.DatabasePath);
        var appConfig = new AppConfigRepository(database);
        var devices = new DeviceRepository(database);
        var certificates = new ServerCertificateManager(paths, new DpapiSecretProtector());

        _server = new WirelessCableServer(
            new ServerOptions
            {
                AppVersion = DisplayVersion,
                DisplayName = Environment.MachineName,
            },
            paths,
            database,
            appConfig,
            devices,
            certificates);

        _viewModel = new MainViewModel(_server, paths, appConfig, devices, _hotspot);

        // 新版界面出码要用热点：能力在桌面端，实现挂到服务端的桥上。
        // 走 ViewModel 是为了让旧界面的热点状态同步更新。
        _server.HotspotBridge.EnsureAsync = async cancellationToken =>
        {
            var offer = await _viewModel.EnsureHotspotForQrAsync(cancellationToken);
            return offer is null
                ? null
                : new WirelessCable.Server.WebUi.LocalHotspotOffer(offer.Ssid, offer.Passphrase, offer.Address);
        };
        _server.HotspotBridge.UnavailableReason = () =>
            _hotspot.QueryState() == HotspotState.Unsupported
                ? "无法开热点：电脑需要一个可共享的网络（网线或 Wi-Fi）"
                : "电脑热点开启失败，手机可连同一 Wi-Fi";

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;

        // 新版界面是默认入口。旧窗口先不显示，但必须构造出来：
        // 托盘、配对提示、退出收尾都挂在它身上。
        await _viewModel.StartAsync();

        if (!_viewModel.OpenNewUiWindow())
        {
            // WebView2 起不来（运行时缺失/端口没起来）：退回旧界面，保证能用
            window.Show();
        }

        // 老版本删设备时不清传输记录，这里收一次尾；失败不影响启动。
        // 清完必须让传输列表重新加载，否则界面上仍挂着刚删掉的任务。
        try
        {
            var purged = await _server.Transfers.PurgeOrphanDeviceRecordsAsync();
            if (purged > 0)
            {
                await _viewModel.Transfers.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "清理无主传输记录失败");
        }

        EnsureFirewall();

        if (_server.Snapshot().State == ServiceState.Failed)
        {
            Dialogs.Alert("服务启动失败", $"本地服务启动失败：{_server.Snapshot().LastError}");
        }
    }

    /// <summary>
    /// 放行入站端口。用户不该被要求自己去配防火墙，所以：
    /// 已有规则就跳过；没有就尝试自动添加；权限不够时弹一次 UAC，
    /// 由提权后的自身完成配置。全程只需要用户点一次「是」。
    /// </summary>
    private void EnsureFirewall()
    {
        var port = _server?.Snapshot().Port ?? ProtocolConstants.DefaultHttpsPort;
        var rules = FirewallConfigurator.BuildRules(port);

        switch (FirewallConfigurator.Ensure(rules, out var detail))
        {
            case FirewallStatus.AlreadyConfigured:
                Log.Information("防火墙规则已存在，无需处理");
                break;

            case FirewallStatus.Applied:
                Log.Information("已自动放行入站端口 port={Port}（TCP）与 5353（UDP）", port);
                break;

            case FirewallStatus.ElevationRequired:
                var confirm = Dialogs.Confirm(
                    "放行本机端口",
                    $"需要放行本机端口 {port}，手机才能连接。\n\n点「继续」将请求管理员权限完成配置，不需要你手动设置防火墙。",
                    confirmText: "继续");

                if (confirm)
                {
                    var executable = Environment.ProcessPath;
                    string? error = null;
                    if (executable is not null && FirewallConfigurator.TryElevateAndApply(executable, port, out error))
                    {
                        Log.Information("提权后已完成防火墙配置");
                    }
                    else
                    {
                        Log.Warning("防火墙配置未完成：{Error}", error);
                    }
                }
                else
                {
                    Log.Warning("用户暂未授权放行端口，局域网可能无法连接");
                }

                break;

            default:
                Log.Warning("防火墙配置失败：{Detail}", detail);
                break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.Detach();

            // 关键：必须在线程池上停止。若在 UI 线程直接 await/Wait，
            // 宿主内部未 ConfigureAwait(false) 的续体会被投回已阻塞的 UI 调度器，
            // 形成互锁——表现为"关窗后进程永远不退出、也不报超时"（K16）。
            var stopping = Task.Run(async () =>
            {
                // 先关热点再停服务：热点是应用开的，退出后留着会一直占用无线网卡
                if (_viewModel is not null)
                {
                    await _viewModel.ShutdownHotspotAsync().ConfigureAwait(false);
                }

                if (_server is not null)
                {
                    await _server.StopAsync().ConfigureAwait(false);
                }
            });

            if (!stopping.Wait(TimeSpan.FromSeconds(8)))
            {
                Log.Warning("停止服务超过 8 秒，强制结束进程");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "退出时停止服务失败");
        }
        finally
        {
            Log.Information("WirelessCable 退出");
            Log.CloseAndFlush();
        }

        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "释放单实例互斥时出现异常（可忽略）");
        }

        base.OnExit(e);

        // 兜底：只要窗口关闭，进程就必须走，不允许有前台线程把它拖住
        Environment.Exit(e.ApplicationExitCode);
    }

    /// <summary>把已在运行的窗口置前，让"重复双击"表现得像激活而不是失败。</summary>
    private static void ActivateExistingWindow()
    {
        var handle = FindWindow(null, "WirelessCable");
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(handle, SW_RESTORE);
        SetForegroundWindow(handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    private const int SW_RESTORE = 9;

    private static string DisplayVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private bool _handlingUiException;

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI 线程未处理异常");

            // 布局阶段的异常会被每一帧重复抛出。若无条件弹 MessageBox，
            // 就会在同一帧里反复进入"弹窗→关闭→再抛"，最终栈溢出（0.5.4 踩过）。
            if (_handlingUiException)
            {
                args.Handled = true;
                return;
            }

            _handlingUiException = true;
            try
            {
                Dialogs.Alert("出现错误", $"发生未处理的错误，已记录到日志：{args.Exception.Message}");
                args.Handled = true;
            }
            finally
            {
                _handlingUiException = false;
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "非 UI 线程未处理异常");
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "未观察的任务异常");
            args.SetObserved();
        };
    }
}
