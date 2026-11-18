using System.IO;
using System.Text.Json;
using WirelessCable.Infrastructure.Paths;
using System.Text.Json.Serialization;

namespace WirelessCable.Desktop.Services;

/// <summary>用户可改的设置。为空表示用默认值。</summary>
public sealed class DesktopSettings
{
    /// <summary>数据根目录：数据库、证书、接收文件、传输临时文件都在它下面。</summary>
    [JsonPropertyName("dataRoot")]
    public string? DataRoot { get; set; }

    /// <summary>日志目录。独立于数据根，方便放到别处集中收集。</summary>
    [JsonPropertyName("logDirectory")]
    public string? LogDirectory { get; set; }

    /// <summary>主题：system / light / dark。</summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    /// <summary>首次使用引导是否已看过。</summary>
    [JsonPropertyName("introSeen")]
    public bool IntroSeen { get; set; }

    /// <summary>关闭窗口时收进托盘而不是退出。</summary>
    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; set; }

    /// <summary>开机自启（写 HKCU Run 项）。</summary>
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// 电脑热点的 SSID。为空时按机器名派生（WirelessCable-XXXX）。
    ///
    /// 之所以把 SSID/密码存在这里：Windows 开完热点读不回密码，
    /// 应用必须自己记住才能把凭据放进二维码。
    /// </summary>
    [JsonPropertyName("hotspotSsid")]
    public string? HotspotSsid { get; set; }

    /// <summary>电脑热点密码（8 位数字）。</summary>
    [JsonPropertyName("hotspotPassphrase")]
    public string? HotspotPassphrase { get; set; }
}

/// <summary>
/// settings.json 读写。
///
/// 安装版固定放 %LOCALAPPDATA%\WirelessCable\settings.json——不能跟着数据目录走，
/// 否则换目录后下一次启动就找不到这份设置了；
/// 绿色版放在程序目录（RootDirectory），整包迁移时配置跟着走。
/// </summary>
public static class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultRoot => PortableLayout.IsPortable
        ? PortableLayout.Root
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WirelessCable");

    public static string FilePath => Path.Combine(DefaultRoot, "settings.json");

    public static DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new DesktopSettings();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<DesktopSettings>(json, Options) ?? new DesktopSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "读取 settings.json 失败，按默认目录启动");
            return new DesktopSettings();
        }
    }

    public static bool Save(DesktopSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DefaultRoot);

            // 先写临时文件再替换：直接覆盖时断电会留下半截 JSON，下次启动就丢配置
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "写入 settings.json 失败");
            return false;
        }
    }

    /// <summary>
    /// 改一个字段再存回。不要"加载 → new 一个只带部分字段的对象 → Save"：
    /// 那样会把没写的字段（主题、引导标记）重置成默认值。
    /// </summary>
    public static bool Update(Action<DesktopSettings> mutate)
    {
        var settings = Load();
        mutate(settings);
        return Save(settings);
    }

    public static void Reset()    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "删除 settings.json 失败");
        }
    }
}
