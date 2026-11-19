using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Serilog;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace WirelessCable.Desktop.Views;

/// <summary>
/// 新版界面宿主（阶段 14.2 / 14.5）。
///
/// 三条硬规矩，缺一条都别放行：
/// <list type="number">
/// <item>只加载自己的 <c>https://127.0.0.1:&lt;port&gt;/app</c>：<c>NavigationStarting</c> 里拦下任何外链；</item>
/// <item>新窗口一律拒绝（<c>NewWindowRequested</c>），需要打开的外链交给系统浏览器；</item>
/// <item>证书错误不做"无条件忽略"：只放行本机地址且指纹匹配的那张自签证书。</item>
/// </list>
///
/// 页面要"动手"（选文件、开目录、复制、最小化）时不直接调系统 API —— 浏览器里没有这些权限，
/// 统一走 <c>window.chrome.webview.postMessage</c>，由这里的 <see cref="OnWebMessage"/> 执行并回包。
/// 旧界面并存：这个窗口只是侧栏里的一个入口，关掉不影响主窗口。
/// </summary>
public partial class WebUiWindow : Window
{
    private readonly string _origin;
    private readonly string _fingerprint;

    /// <param name="origin">形如 <c>https://127.0.0.1:47110</c>，不带尾斜杠。</param>
    /// <param name="fingerprint">服务端证书指纹（SHA-256(DER) 十六进制，可带冒号）；空表示不校验。</param>
    public WebUiWindow(string origin, string fingerprint, bool maximized = false)
    {
        _origin = origin.TrimEnd('/');
        _fingerprint = Normalize(fingerprint);

        InitializeComponent();

        if (maximized)
        {
            WindowState = WindowState.Maximized;
        }

        Log.Information("新版界面窗口创建 origin={Origin}", _origin);
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => Log.Information("新版界面窗口已关闭");
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        // 用户数据目录与主程序数据放在一起：卸载/绿色版迁移时一起走
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WirelessCable",
            "WebView2");
        Directory.CreateDirectory(dataDir);

        try
        {
            // 排障：WC_WEBUI_DEBUG_PORT=9333 让 WebView2 开 CDP 端口，
            // 自动化验证（点右键、按快捷键）靠它驱动页面。正常启动不设这个变量。
            CoreWebView2EnvironmentOptions? options = null;
            if (int.TryParse(Environment.GetEnvironmentVariable("WC_WEBUI_DEBUG_PORT"), out var debugPort)
                && debugPort is > 0 and < 65536)
            {
                options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = $"--remote-debugging-port={debugPort}",
                };
                Log.Information("新版界面开启调试端口 {Port}", debugPort);
            }

            var environment = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
            await View.EnsureCoreWebView2Async(environment);
            Log.Information("WebView2 初始化完成");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 初始化失败");
            MessageBox.Show(
                $"无法初始化 WebView2（新版界面需要系统自带的 WebView2 运行时）：\n{ex.Message}",
                "新版界面",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Close();
            return;
        }

        var core = View.CoreWebView2;

        // 右键菜单由页面自己画（传输中心的"删除记录"等）。这里必须关掉 Edge 的默认菜单，
        // 否则真实鼠标右键会弹出"刷新/另存为/检查"，把页面菜单盖住甚至完全拦住。
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ServerCertificateErrorDetected += OnServerCertificateError;
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += (_, e) => Log.Warning("WebView2 进程失败：{Kind}", e.ProcessFailedKind);
        core.NavigationCompleted += (_, e) => Log.Information(
            "新版界面加载完成 success={Ok} status={Status}", e.IsSuccess, e.HttpStatusCode);

        // 排障/截图用：WC_WEBUI_PAGE=devices 直接定位到某个页面（正常使用不带这个变量）
        var page = Environment.GetEnvironmentVariable("WC_WEBUI_PAGE");
        var query = string.IsNullOrWhiteSpace(page) ? string.Empty : $"?page={Uri.EscapeDataString(page)}";

        // 额外的查询串（排障用，例如 selfcheck=1）；正常启动时两者都为空
        var extra = Environment.GetEnvironmentVariable("WC_WEBUI_QUERY");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            query = query.Length == 0 ? "?" + extra : query + "&" + extra;
        }
        View.Source = new Uri(_origin + "/app/" + query);
    }

    /// <summary>
    /// 关闭语义，与旧窗口保持一致：
    /// <list type="bullet">
    /// <item>设置里勾了「收进托盘」→ 只隐藏，服务与传输继续跑；</item>
    /// <item>否则真正退出。新版界面是默认入口，关掉它就该结束进程；</item>
    /// <item>如果用户同时开着旧界面（回退），关掉新的不牵连旧的。</item>
    /// </list>
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current?.Dispatcher.HasShutdownStarted == true)
        {
            base.OnClosing(e);
            return;
        }

        var oldWindowVisible = Application.Current?.MainWindow?.IsVisible == true;

        if (!oldWindowVisible && WirelessCable.Desktop.Services.DesktopSettingsStore.Load().CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);

        if (!oldWindowVisible)
        {
            Application.Current?.Shutdown();
        }
    }

    // ---------------------------------------------------------------- 安全边界

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith(_origin + "/", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            OpenExternally(e.Uri);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternally(e.Uri);
    }

    private void OnServerCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        // 只有"本机地址 + 我们自己的指纹"才放行；其它一律保持失败，绝不整体忽略证书错误
        var isLocal = e.RequestUri.StartsWith(_origin, StringComparison.OrdinalIgnoreCase);
        var matches = _fingerprint.Length > 0
            && string.Equals(ThumbprintOf(e.ServerCertificate), _fingerprint, StringComparison.OrdinalIgnoreCase);

        e.Action = isLocal && matches
            ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
            : CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    /// <summary>
    /// 证书指纹（SHA-256，十六进制大写）。
    ///
    /// 服务端存的是 SHA-256(DER)，见 <c>ServerCertificateManager.ComputeFingerprint</c>；
    /// 这里必须用同一个算法，否则自签证书永远校验失败、页面白屏。
    /// WebView2 的 <c>CoreWebView2Certificate</c> 不给现成指纹，只能从 PEM 取回 DER 自己算。
    /// </summary>
    private static string ThumbprintOf(CoreWebView2Certificate? certificate)
    {
        if (certificate is null)
        {
            return string.Empty;
        }

        try
        {
            var pem = certificate.ToPemEncoding();
            var body = string.Join(
                string.Empty,
                pem.Split('\n', '\r')
                    .Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal)));
            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(body)));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Normalize(string fingerprint) =>
        (fingerprint ?? string.Empty).Replace(":", string.Empty).Replace(" ", string.Empty).Trim();

    // ---------------------------------------------------------------- 宿主桥

    /// <summary>
    /// 页面 → 桌面。请求形如 <c>{ id, action, ...参数 }</c>，回包 <c>{ id, ok, error?, path?, files? }</c>。
    /// 只认白名单动作；不认识的直接回错误，不猜。
    /// </summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            var action = root.TryGetProperty("action", out var actionValue) ? actionValue.GetString() : null;
            Log.Information("宿主动作 {Action} id={Id}", action, id);

            switch (action)
            {
                case "openPath":
                {
                    var path = GetString(root, "path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        Reply(id, false, "missing_path");
                        return;
                    }

                    OpenPath(path);
                    Reply(id, true);
                    return;
                }

                case "openFile":
                {
                    // 直接用系统默认程序打开（等于在资源管理器里双击）。
                    // 和 openPath 分开：那个是"定位"，这个是"打开"，
                    // 用户要的是"下载完就能看"，不是先弹个文件夹再自己双击。
                    var path = GetString(root, "path");
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        Reply(id, false, "missing_path");
                        return;
                    }

                    OpenWithShell(path);
                    Reply(id, true);
                    return;
                }

                case "pickFiles":
                {
                    // 不能在 WebMessageReceived 里同步弹模态框：WebView2 的回调还没返回，
                    // 消息循环就被对话框接管，第二次起对话框干脆不出现
                    //（真机现象：选了文件点「打开」没反应，日志只有"宿主动作"没有"回包"）。
                    // 丢到调度队列，等这条消息处理完再弹。
                    var title = GetString(root, "title") ?? "选择要发送的文件";
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => ShowPickFiles(id, title)));
                    return;
                }

                case "pickFolder":
                {
                    var title = GetString(root, "title") ?? "选择目录";
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => ShowPickFolder(id, title)));
                    return;
                }

                case "copyText":
                {
                    var text = GetString(root, "text") ?? string.Empty;
                    if (text.Length > 0)
                    {
                        Clipboard.SetText(text);
                    }

                    Reply(id, true);
                    return;
                }

                case "window":
                {
                    switch (GetString(root, "value"))
                    {
                        case "minimize":
                            WindowState = WindowState.Minimized;
                            break;
                        case "maximize":
                            WindowState = WindowState == WindowState.Maximized
                                ? WindowState.Normal
                                : WindowState.Maximized;
                            break;
                        case "close":
                            Close();
                            break;
                        default:
                            Reply(id, false, "unknown_window_action");
                            return;
                    }

                    Reply(id, true);
                    return;
                }

                case "openExternal":
                {
                    var url = GetString(root, "url");
                    if (url is null ||
                        !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                    {
                        Reply(id, false, "scheme_not_allowed");
                        return;
                    }

                    OpenExternally(url);
                    Reply(id, true);
                    return;
                }

                default:
                    Log.Warning("未知宿主动作 {Action}", action);
                    Reply(id, false, "unknown_action");
                    return;
            }
        }
        catch (Exception ex)
        {
            Reply(id, false, ex.Message);
        }
    }

    /// <summary>
    /// 选文件 / 选目录。
    ///
    /// 不用 UI 线程直接 <c>ShowDialog</c>：在这台机器上它会卡住 UI 线程、对话框根本不出现
    /// （日志停在"弹出选文件框"，窗口列表里也没有 #32770），界面表现为"点了没反应"。
    /// 改成**独立 STA 线程**跑对话框：UI 线程继续响应，对话框有自己的消息循环，
    /// 结果再回到 UI 线程回包。代价是对话框不再属于主窗口（可能被压到后面），换稳定值得。
    /// </summary>
    private void ShowPickFiles(string? id, string title)
    {
        RunOnStaThread(
            () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = title,
                    Multiselect = true,
                    CheckFileExists = true,
                };

                var ok = dialog.ShowDialog();
                Log.Information("选文件框返回 {Result}", ok);
                return ok == true
                    ? (true, null, null, dialog.FileNames)
                    : (false, "cancelled", null, null);
            },
            (ok, error, path, files) => Reply(id, ok, error, path, files));
    }

    private void ShowPickFolder(string? id, string title)
    {
        RunOnStaThread(
            () =>
            {
                var dialog = new OpenFolderDialog
                {
                    Title = title,
                    Multiselect = false,
                };

                var ok = dialog.ShowDialog();
                Log.Information("选目录框返回 {Result}", ok);
                return ok == true
                    ? (true, null, dialog.FolderName, null)
                    : (false, "cancelled", null, null);
            },
            (ok, error, path, files) => Reply(id, ok, error, path, files));
    }

    /// <summary>在独立 STA 线程上跑一个返回对话框结果的函数，再回 UI 线程交付。</summary>
    private void RunOnStaThread(
        Func<(bool Ok, string? Error, string? Path, string[]? Files)> work,
        Action<bool, string?, string?, string[]> deliver)
    {
        var thread = new System.Threading.Thread(() =>
        {
            (bool Ok, string? Error, string? Path, string[]? Files) result;
            try
            {
                Log.Information("弹出文件对话框（独立 STA 线程）");
                result = work();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "文件对话框失败");
                result = (false, ex.Message, null, null);
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    deliver(result.Ok, result.Error, result.Path, result.Files ?? []);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "回传对话框结果失败");
                }
            }));
        })
        {
            IsBackground = true,
            Name = "wc-file-dialog",
        };

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    private void Reply(string? id, bool ok, string? error = null, string? path = null, string[]? files = null)
    {
        Log.Information("宿主回包 id={Id} ok={Ok} error={Error} files={Count}", id, ok, error, files?.Length ?? 0);
        View.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            id,
            ok,
            error,
            path,
            files,
        }));
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>用系统默认程序打开文件。文件不存在时抛，由调用方回错误。</summary>
    private static void OpenWithShell(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("文件不存在，可能已被移走", path);
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>打开目录或选中文件：文件用 /select 定位，目录直接打开。</summary>
    private static void OpenPath(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return;
        }

        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return;
        }

        throw new FileNotFoundException("路径不存在", path);
    }

    private static void OpenExternally(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，界面不该因为一个链接崩掉
        }
    }
}
