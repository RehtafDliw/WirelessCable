using Serilog;
using Serilog.Events;
using WirelessCable.Core.Abstractions;

namespace WirelessCable.Infrastructure.Logging;

/// <summary>
/// Serilog 统一配置：按天滚动、单文件上限 16MB、保留 7 天。
/// 日志中禁止出现设备令牌、私钥、完整接收路径（见 <see cref="LogSafe"/>）。
/// </summary>
public static class LoggingSetup
{
    public static LoggerConfiguration Create(IAppPaths paths, LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        Directory.CreateDirectory(paths.LogsDirectory);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(paths.LogsDirectory, "wirelesscable-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 16 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        // 只写文件：宿主是 WinExe，没有控制台；多一个 Console sink 只是多一个依赖
        return configuration;
    }
}
