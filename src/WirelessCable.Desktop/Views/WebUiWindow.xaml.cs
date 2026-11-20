using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        // 句柄一就绪先按系统主题对一次：网页还没加载，否则深色模式下会先闪一条白标题栏。
        // 具体颜色等前端挂载后通过 setWindowTheme 再精确覆盖。
        SourceInitialized += (_, _) => ApplyWindowTheme(IsSystemDarkTheme(), null, null);
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
    /// 关闭语义：**叉号只收起窗口**，服务与传输继续跑；退出统一走托盘菜单的「退出程序」。
    ///
    /// 以前这里跟着 `CloseToTray` 走（默认 false），点叉号等于 `Application.Shutdown()`，
    /// 正在传的文件、后台服务一起没了。这是个托盘应用，叉号不该是退出键。
    /// 真正退出由 <see cref="Services.TrayIcon"/> 的「退出程序」触发：它会先把
    /// Dispatcher 标记为正在关闭，这里直接放行。
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (Application.Current?.Dispatcher.HasShutdownStarted == true)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
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

                case "setWindowTheme":
                {
                    // 网页切主题时同步原生标题栏，否则深色界面顶上会挂着一条白色边框
                    var theme = GetString(root, "theme");
                    var background = GetString(root, "background");
                    var text = GetString(root, "text");
                    Log.Information("标题栏配色 theme={Theme} background={Background} text={Text}", theme, background, text);
                    ApplyWindowTheme(
                        string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase),
                        background,
                        text);
                    Reply(id, true);
                    return;
                }

                case "fileIcon":
                {
                    // 列表行首显示"系统默认图标"（文件夹 / exe / jpg 各是各的）。
                    // 只按扩展名取，不需要文件真实存在——传输记录可能指向已删掉的文件。
                    var ext = GetString(root, "ext") ?? string.Empty;
                    var folder = GetString(root, "kind") == "folder";
                    Reply(id, true, data: SystemIconDataUrl(ext, folder));
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

    private void Reply(
        string? id,
        bool ok,
        string? error = null,
        string? path = null,
        string[]? files = null,
        string? data = null)
    {
        Log.Information("宿主回包 id={Id} ok={Ok} error={Error} files={Count}", id, ok, error, files?.Length ?? 0);
        View.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            id,
            ok,
            error,
            path,
            files,
            data,
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

    // ---------------------------------------------------------------- 系统图标

    private static readonly ConcurrentDictionary<string, string?> IconCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 按扩展名取 Windows 的默认文件图标，返回 `data:image/png;base64,...`。
    ///
    /// 用 `SHGFI_USEFILEATTRIBUTES`：只要给个名字，**不需要文件真实存在**，
    /// 系统就会按扩展名给出关联图标（.jpg 是照片查看器、.exe 是程序、.apk 是它的关联程序）。
    /// 传输记录可能指向已经被移走的文件，靠真实路径取图标会失败。
    /// </summary>
    private static string? SystemIconDataUrl(string ext, bool folder)
    {
        var key = folder
            ? "__folder__"
            : string.IsNullOrWhiteSpace(ext) ? "__file__" : ext.ToLowerInvariant();

        return IconCache.GetOrAdd(key, k => ExtractIcon(k, folder));
    }

    private static string? ExtractIcon(string key, bool folder)
    {
        try
        {
            // 探测名只要扩展名对得上就够了；文件夹给一个不带扩展名的路径
            var probe = folder ? "folder" : "file" + (key.StartsWith('.') ? key : string.Empty);
            var info = new SHFILEINFO();
            var flags = ShgfiIcon | ShgfiUseFileAttributes;
            var attributes = folder ? FileAttributeDirectory : FileAttributeNormal;

            var result = SHGetFileInfo(
                probe,
                attributes,
                ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                flags);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                // Icon.FromHandle 不接管句柄，用完必须自己 DestroyIcon，否则每次泄漏一个 GDI 对象
                using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
                using var bitmap = icon.ToBitmap();
                using var buffer = new MemoryStream();
                bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "提取系统图标失败 key={Key}", key);
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;

    // ---------------------------------------------------------------- 标题栏配色

    /// <summary>
    /// 让 Windows 原生标题栏和网页同色（"沉浸式"）。
    ///
    /// 两级手段：
    /// <list type="number">
    /// <item><c>DWMWA_USE_IMMERSIVE_DARK_MODE</c>（Win10 20H1+）—— 深色时系统自己把
    /// 标题栏和按钮文字调整成深色系，老系统也能用；</item>
    /// <item><c>DWMWA_CAPTION_COLOR</c> / <c>DWMWA_TEXT_COLOR</c>（Win11 22H2+）——
    /// 精确指定成网页算出来的那个颜色，浅色下才不会比侧栏白一截。
    /// 老系统上这两个属性返回 E_INVALIDARG，忽略即可。</item>
    /// </list>
    /// </summary>
    private void ApplyWindowTheme(bool dark, string? background, string? text)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var immersive = dark ? 1 : 0;
            // 20 是正式版编号，19 是 20H1 预览版的旧编号，两个都试一次
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref immersive, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmwaImmersiveDarkModeLegacy, ref immersive, sizeof(int));
            }

            SetDwmColor(hwnd, DwmwaCaptionColor, background);
            SetDwmColor(hwnd, DwmwaTextColor, text);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "设置标题栏配色失败");
        }
    }

    private static void SetDwmColor(IntPtr hwnd, int attribute, string? hex)
    {
        if (!TryParseColorRef(hex, out var colorRef))
        {
            return;
        }

        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }

    /// <summary>把 `#RRGGBB` 转成 DWM 要的 COLORREF（0x00BBGGRR）。</summary>
    private static bool TryParseColorRef(string? hex, out int colorRef)
    {
        colorRef = 0;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var text = hex.TrimStart('#');
        if (text.Length != 6
            || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return false;
        }

        var red = (rgb >> 16) & 0xFF;
        var green = (rgb >> 8) & 0xFF;
        var blue = rgb & 0xFF;
        colorRef = (blue << 16) | (green << 8) | red;
        return true;
    }

    /// <summary>读系统"应用使用浅色主题"设置，用于窗口刚出现时先对齐一次。</summary>
    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaImmersiveDarkModeLegacy = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

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
