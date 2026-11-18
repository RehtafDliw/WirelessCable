using System.IO;

namespace WirelessCable.Infrastructure.Paths;

/// <summary>
/// 绿色版（便携版）判定。
///
/// 规则：程序目录下存在 <c>Data</c> 文件夹或 <c>portable.marker</c> 文件即视为便携版。
/// 发布包里的 Portable 目录天然带 <c>Data</c>，用户把整个文件夹拷到 U 盘/另一台机器
/// 就能直接接着用；单文件版（SingleFile）目录里没有这些，照旧用 %LOCALAPPDATA%。
///
/// 为什么用"目录存在"而不是只认标记文件：用户把便携目录里的 Data 删掉想重置时，
/// 不该顺手把程序变成"安装版"——发布包会带上 <c>portable.marker</c> 兜底。
/// </summary>
public static class PortableLayout
{
    private const string MarkerFile = "portable.marker";
    private const string DataFolder = "Data";

    /// <summary>程序所在目录（单文件发布时就是 exe 所在目录）。</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static bool IsPortable
    {
        get
        {
            // 自动化测试与排障用：显式覆盖判定结果
            var forced = Environment.GetEnvironmentVariable("WIRLESSCABLE_PORTABLE");
            if (!string.IsNullOrEmpty(forced))
            {
                return forced is "1" or "true" or "TRUE";
            }

            var baseDir = BaseDirectory;
            return File.Exists(Path.Combine(baseDir, MarkerFile))
                || Directory.Exists(Path.Combine(baseDir, DataFolder));
        }
    }

    public static string Root => BaseDirectory;
}
