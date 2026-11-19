using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using WirelessCable.Core.Abstractions;
using WirelessCable.Infrastructure.Certificates;
using WirelessCable.Infrastructure.Files;
using WirelessCable.Infrastructure.Logging;
using WirelessCable.Infrastructure.Networking;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Endpoints;
using WirelessCable.Server.Files;
using WirelessCable.Server.Identity;
using WirelessCable.Server.Middleware;
using WirelessCable.Server.Pairing;
using WirelessCable.Server.Runtime;
using WirelessCable.Server.Security;
using WirelessCable.Server.Transfer;
using WirelessCable.Server.WebUi;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;

namespace WirelessCable.Server;

/// <summary>
/// 进程内 Kestrel 宿主。只负责：建库、取身份、取证书、绑端口、挂管线、启停。
/// 业务能力一律通过端点注册，宿主本身不含业务逻辑。
/// </summary>
public sealed class WirelessCableServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly IAppPaths _paths;
    private readonly SqliteDatabaseFactory _databaseFactory;
    private readonly AppConfigRepository _appConfig;
    private readonly DeviceRepository _devices;
    private readonly ServerCertificateManager _certificates;

    /// <summary>本机 TLS 证书（含私钥）。连接手机反向服务时作为客户端证书出示。</summary>
    private X509Certificate2? _localCertificate;
    private readonly ServerRuntime _runtime;
    private readonly QrPairingService _qr = new();
    private readonly PairingService _pairing;
    private readonly DeviceAuthenticator _authenticator;
    private readonly TransferEngine _transfers;
    private readonly SharedDirectoryRepository _sharedDirectories;
    private readonly SharedDirectoryResolver _directoryResolver;
    private readonly FileService _files;
    private readonly MdnsAdvertiser _mdns = new();
    private readonly LocalHotspotBridge _hotspotBridge = new();
    private readonly Lock _lifecycleGate = new();

    private WebApplication? _application;
    private bool _disposed;

    public WirelessCableServer(
        ServerOptions options,
        IAppPaths paths,
        SqliteDatabaseFactory databaseFactory,
        AppConfigRepository appConfig,
        DeviceRepository devices,
        ServerCertificateManager certificates)
    {
        _options = options;
        _paths = paths;
        _databaseFactory = databaseFactory;
        _appConfig = appConfig;
        _devices = devices;
        _certificates = certificates;
        _runtime = new ServerRuntime(options);
        _authenticator = new DeviceAuthenticator(devices);
        _pairing = new PairingService(devices, _qr, _runtime);

        // 共享目录与临时目录必须分开：临时文件绝不能出现在用户的接收目录里
        var sharedDirectory = string.IsNullOrWhiteSpace(options.SharedDirectory)
            ? paths.ReceivedDirectory
            : options.SharedDirectory;

        _sharedDirectories = new SharedDirectoryRepository(databaseFactory);
        _directoryResolver = new SharedDirectoryResolver(_sharedDirectories, sharedDirectory);

        _transfers = new TransferEngine(
            new TransferRepository(databaseFactory),
            new TransferOptions
            {
                SharedDirectory = sharedDirectory,
                TempDirectory = paths.TransferTempDirectory,
                DefaultChunkSize = ProtocolConstants.ChunkSizeBytes,
            },
            _directoryResolver);

        _files = new FileService(
            _sharedDirectories,
            new DeviceFileRepository(databaseFactory),
            root => new LocalFileProvider(root));
    }

    /// <summary>传输引擎。桌面端界面从这里取任务、发起发送。</summary>
    public TransferEngine Transfers => _transfers;

    /// <summary>共享目录（手机上传落点）。</summary>
    public string SharedDirectory => _transfers.SharedDirectory;

    /// <summary>热点能力桥：桌面端启动时挂上实现，新版界面出码时调用。</summary>
    public LocalHotspotBridge HotspotBridge => _hotspotBridge;

    /// <summary>接收目录在 app_config 里的键。</summary>
    private const string ReceivedDirectoryKey = "received_directory";

    /// <summary>
    /// 改接收目录（设置页「更改」）。
    ///
    /// 只影响之后接收的文件：已在传的任务继续写它创建时算好的路径，
    /// 已经收到的文件不搬家（可能是几十 GB，搬一半失败比不改更糟）。
    /// </summary>
    public async Task<(bool Ok, string? Error)> SetReceivedDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var error = ValidateReceivedDirectory(path, out var full);
        if (error is not null)
        {
            return (false, error);
        }

        _transfers.UpdateSharedDirectory(full);
        _directoryResolver.UpdateDefaultRoot(full);

        try
        {
            await _appConfig.SetAsync(ReceivedDirectoryKey, full, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 已经生效，只是重启后会回到旧目录：如实说，不装作成功
            Log.Warning(ex, "保存接收目录设置失败 path={Path}", full);
            return (false, "目录已切换，但保存设置失败，重启后会恢复原目录。");
        }

        Log.Information("接收目录已改为 {Path}", full);
        return (true, null);
    }

    private string? ValidateReceivedDirectory(string path, out string full)
    {
        full = string.Empty;

        var trimmed = (path ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return "目录不能为空。";
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return "需要一个完整的目录路径。";
        }

        try
        {
            full = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "路径不合法。";
        }

        // 临时文件目录绝不能当接收目录：半截 .wcpart 会直接出现在用户眼前
        if (IsSameOrInside(full, _paths.TransferTempDirectory))
        {
            return "不能用传输临时目录作为接收目录。";
        }

        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return "不能用磁盘根目录作为接收目录。";
        }

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warning(ex, "创建接收目录失败 path={Path}", full);
            return $"无法创建或访问该目录：{ex.Message}";
        }

        return null;
    }

    private static bool IsSameOrInside(string path, string root)
    {
        static string Normalize(string value) =>
            Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return Normalize(path).StartsWith(Normalize(root), StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyStoredReceivedDirectoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _appConfig.GetAsync(ReceivedDirectoryKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stored))
            {
                return;
            }

            if (!Directory.Exists(stored))
            {
                Log.Warning("设置里的接收目录不存在，沿用默认目录 path={Path}", stored);
                return;
            }

            var full = Path.GetFullPath(stored);
            _transfers.UpdateSharedDirectory(full);
            _directoryResolver.UpdateDefaultRoot(full);
            Log.Information("接收目录取自设置 {Path}", full);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取接收目录设置失败");
        }
    }

    /// <summary>共享目录与设备授权仓储，供桌面端管理界面使用。</summary>
    public SharedDirectoryRepository SharedDirectories => _sharedDirectories;

    /// <summary>文件管理服务（手机浏览/管理共享目录、电脑浏览/管理手机目录）。</summary>
    public FileService Files => _files;

    /// <summary>设备仓储，供桌面端管理整机浏览开关。</summary>
    public DeviceRepository Devices => _devices;

    /// <summary>配对队列，供本机界面展示与批准。</summary>
    public PairingService Pairing => _pairing;

    /// <summary>
    /// 本机 TLS 证书（含私钥）。服务启动后才有值。
    ///
    /// 手机反向服务要求客户端出示证书并按配对时固定的指纹校验，
    /// 电脑直接复用自己的这张证书即可，不需要另一套密钥。
    /// </summary>
    public X509Certificate2? LocalCertificate => _localCertificate;

    /// <summary>mDNS 是否在广播。</summary>
    public bool MdnsRunning => _mdns.IsRunning;

    /// <summary>mDNS 最近一次错误（正常为 null）。</summary>
    public string? MdnsError => _mdns.LastError;

    /// <summary>
    /// 为界面生成一次性的配对二维码：含 deviceId、地址、端口、证书指纹与一次性随机数，
    /// 不含任何长期密钥。服务未就绪时返回 null。
    /// </summary>
    /// <param name="hotspot">
    /// 电脑热点（可选）。手机不在同一网络时由桌面端先开热点，再把凭据放进二维码，
    /// 手机扫码后先连热点再配对。为 null 时二维码与旧版本完全一致。
    /// </param>
    public PairingQr? CreatePairingQr(QrHotspotDto? hotspot = null)
    {
        var snapshot = _runtime.Snapshot();
        var identity = _runtime.Identity;

        if (snapshot.State != Core.Diagnostics.ServiceState.Running
            || identity is null
            || snapshot.Port is null
            || string.IsNullOrEmpty(snapshot.Fingerprint))
        {
            return null;
        }

        var payload = _qr.CreatePayload(
            identity.DeviceId,
            identity.UniqueName,
            snapshot.Addresses,
            snapshot.Port.Value,
            snapshot.Fingerprint,
            hotspot);

        var text = QrPairingService.Serialize(payload);
        return new PairingQr(text, QrPairingService.CreatePng(text), payload.ExpiresAtUnixMs);
    }

    /// <summary>当前运行态快照。</summary>
    public ServerSnapshot Snapshot() => _runtime.Snapshot();

    /// <summary>实际监听端口，未运行为 null。</summary>
    public int? Port => _runtime.Snapshot().Port;

    /// <summary>服务端证书指纹（SHA-256/DER 小写十六进制）。</summary>
    public string? Fingerprint => _runtime.Snapshot().Fingerprint;

    /// <summary>启动服务。返回 false 表示启动失败，原因见 <see cref="ServerSnapshot.LastError"/>。</summary>
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate)
        {
            if (_runtime.Snapshot().State is Core.Diagnostics.ServiceState.Running)
            {
                return true;
            }
        }

        _runtime.SetCapabilities(Capabilities.Stage3);

        try
        {
            _paths.EnsureCreated();

            var databaseReady = SchemaInitializer.Initialize(_databaseFactory);
            if (!databaseReady)
            {
                throw new InvalidOperationException("数据库初始化失败。");
            }

            var identity = await DeviceIdentityService
                .ResolveAsync(_appConfig, _options.DisplayName, cancellationToken)
                .ConfigureAwait(false);

            _runtime.SetIdentity(identity);

            var addresses = LanAddressProvider.GetLocalIPv4Addresses();
            var certificate = _certificates.GetOrCreate(BuildDnsNames(), addresses);
            _localCertificate = certificate.Certificate;

            _runtime.MarkStarting(addresses, certificate.Fingerprint, databaseReady);

            // 接收目录必须在回收任务之前定下来：残留任务要按它的根去清理
            await ApplyStoredReceivedDirectoryAsync(cancellationToken).ConfigureAwait(false);

            // 阶段 3 不支持续传：启动时把上次残留的未完成任务作废并清理临时文件
            await _transfers.RecoverAsync(cancellationToken).ConfigureAwait(false);

            var (application, port) = await StartKestrelWithFallbackAsync(certificate, cancellationToken).ConfigureAwait(false);

            _application = application;
            _runtime.MarkRunning(port);

            // mDNS 必须在拿到真实端口后广播，否则二维码与 SRV 记录会指向错误端口
            _mdns.Start(
                identity.DeviceId,
                identity.UniqueName,
                port,
                certificate.Fingerprint,
                ProtocolConstants.CurrentVersion,
                ProtocolConstants.ServiceType,
                ProtocolConstants.PairingSuite);

            Log.Information(
                "服务已启动 port={Port} addresses={Addresses} fingerprint={Fingerprint} deviceId={DeviceId}",
                port,
                string.Join(",", addresses),
                LogSafe.Fingerprint(certificate.Fingerprint),
                identity.DeviceId);

            return true;
        }
        catch (Exception ex)
        {
            _runtime.MarkFailed(ex.Message);
            Log.Error(ex, "服务启动失败");
            await StopInternalAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>停止服务并释放 Kestrel 与监听端口。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _runtime.MarkStopping();

        var application = Interlocked.Exchange(ref _application, null);
        if (application is null)
        {
            _runtime.MarkStopped();
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await application.StopAsync(timeout.Token).ConfigureAwait(false);
            Log.Debug("宿主已停止，准备释放资源");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("停止服务超时，强制释放");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "停止服务时出现异常");
        }
        finally
        {
            await DisposeHostAsync(application).ConfigureAwait(false);
            await StopMdnsAsync().ConfigureAwait(false);
            _runtime.MarkStopped();
            Log.Information("服务已停止");
        }
    }

    /// <summary>
    /// 宿主释放限时。Kestrel 在还有 keep-alive 连接时 DisposeAsync 可能一直不返回，
    /// 关窗后会表现为"进程残留、端口不放"。释放不掉就放弃——进程退出时系统会回收端口。
    /// </summary>
    private static async Task DisposeHostAsync(WebApplication application)
    {
        try
        {
            var disposing = Task.Run(async () => await application.DisposeAsync().ConfigureAwait(false));
            var completed = await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

            if (completed != disposing)
            {
                Log.Warning("宿主释放超过 2 秒，跳过等待");
                return;
            }

            await disposing.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "释放宿主时出现异常");
        }
    }

    /// <summary>
    /// mDNS 停止单独限时：Makaretu 的接收线程在某些网络状态下会卡在 Dispose 里，
    /// 曾导致关窗后进程残留（实测残留进程累计跑了 1.6 小时 CPU）。停不掉就放弃，交给退出兜底。
    /// </summary>
    private async Task StopMdnsAsync()
    {
        try
        {
            var stopping = Task.Run(_mdns.Stop);
            var completed = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);

            if (completed != stopping)
            {
                Log.Warning("mDNS 停止超过 3 秒，跳过等待");
                return;
            }

            await stopping.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "停止 mDNS 时出现异常");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _mdns.Dispose();
        _pairing.Dispose();
        _transfers.Dispose();
    }

    private async Task StopInternalAsync()
    {
        var application = Interlocked.Exchange(ref _application, null);
        if (application is null)
        {
            return;
        }

        try
        {
            await application.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "释放启动失败的宿主时出现异常");
        }
    }

    private IReadOnlyList<string> BuildDnsNames()
    {
        var names = new List<string> { Environment.MachineName, Environment.MachineName.ToLowerInvariant() };

        if (!string.IsNullOrWhiteSpace(_options.DisplayName))
        {
            names.Add(_options.DisplayName);
        }

        return names;
    }

    private async Task<(WebApplication Application, int Port)> StartKestrelWithFallbackAsync(
        ServerCertificate certificate,
        CancellationToken cancellationToken)
    {
        var preferred = PortAllocator.FindAvailablePort(_options.PreferredPort, _options.PortProbeCount)
                        ?? _options.PreferredPort;

        Exception? lastError = null;

        for (var offset = 0; offset <= _options.PortProbeCount; offset++)
        {
            var port = preferred + offset;
            if (port > 65535)
            {
                break;
            }

            var application = BuildApplication(port, certificate.Certificate);

            try
            {
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                return (application, port);
            }
            catch (Exception ex) when (IsAddressInUse(ex))
            {
                lastError = ex;
                Log.Warning("端口 {Port} 被占用，尝试下一个", port);
                await application.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                await application.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        throw new InvalidOperationException(
            $"端口 {preferred}-{preferred + _options.PortProbeCount} 全部不可用，服务无法启动。", lastError);
    }

    private WebApplication BuildApplication(int port, X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "WirelessCable.Server",
            EnvironmentName = Environments.Production,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // JSON 请求体限制在 1 MiB；分块请求要容纳一个完整分块
            kestrel.Limits.MaxRequestBodySize =
                ProtocolConstants.ChunkSizeBytes + ProtocolConstants.MaxJsonBodyBytes;

            kestrel.ListenAnyIP(port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    // TLS 1.3 优先；Win10 早期版本的 SChannel 不支持服务端 TLS 1.3，保留 1.2 兜底
                    https.SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;
                });
            });
        });

        builder.Services.AddSingleton(_options);
        builder.Services.AddSingleton(_runtime);
        builder.Services.AddSingleton(_databaseFactory);
        builder.Services.AddSingleton(_devices);
        builder.Services.AddSingleton(_appConfig);
        builder.Services.AddSingleton(new FixedWindowRateLimiter(_options.RateLimitPerMinute));
        builder.Services.AddSingleton(_qr);
        builder.Services.AddSingleton(_pairing);
        builder.Services.AddSingleton(_authenticator);
        builder.Services.AddSingleton(_transfers);
        builder.Services.AddSingleton(_sharedDirectories);
        builder.Services.AddSingleton(_directoryResolver);
        builder.Services.AddSingleton(_files);
        builder.Services.AddSingleton<LocalUiSession>();
        builder.Services.AddSingleton(_hotspotBridge);
        // 端点要调 CreatePairingQr / SetReceivedDirectory，把宿主自己注册进去
        builder.Services.AddSingleton(this);

        // 默认 30 秒的优雅停机对桌面端太长：手机上挂着 keep-alive 连接时，
        // StopAsync 会一直等对端关闭，表现为"关窗后进程残留"。3 秒足够，
        // 超出直接放弃排空——进程退出后端口仍由系统回收。
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));

        var application = builder.Build();

        /*
         * 阶段 14.1 / 14.6：Web 界面静态资源。
         *
         * 必须挂在设备鉴权**之前**——浏览器里没有设备令牌，让 /app 走 DeviceAuthMiddleware
         * 等于永远打不开。这里只放静态资源，任何数据接口仍在 /api/v1 下、照旧鉴权。
         *
         * 产物优先读 wwwroot（开发时直接丢 vite build 结果），否则读嵌进程序集的资源 ——
         * 发布出去仍然是一个 exe，不用在旁边摆目录。
         */
        WirelessCable.Server.WebUi.LocalUiStatic.Map(application);

        // 14.1b：/app/api/* 只认本机 + 一次性令牌（局域网设备拿不到，也带不进来）
        application.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/app/api"))
            {
                var session = context.RequestServices.GetRequiredService<WirelessCable.Server.WebUi.LocalUiSession>();
                var provided = context.Request.Headers[WirelessCable.Server.WebUi.LocalUiSession.HeaderName].ToString();
                var authorized = WirelessCable.Server.WebUi.LocalUiSession.IsLoopback(context)
                    && !string.IsNullOrEmpty(provided)
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(provided),
                        System.Text.Encoding.UTF8.GetBytes(session.Token));

                if (!authorized)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { code = "local_ui_unauthorized" });
                    return;
                }
            }

            await next();
        });

        LocalUiEndpoints.MapLocalUiApi(application);
        application.UseMiddleware<ExceptionHandlingMiddleware>();
        application.UseMiddleware<RequestLoggingMiddleware>();
        application.UseMiddleware<ProtocolVersionMiddleware>();
        application.UseMiddleware<RateLimitMiddleware>();
        application.UseMiddleware<DeviceAuthMiddleware>();
        application.MapWirelessCableEndpoints();

        return application;
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }

            if (current is IOException && current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
