using System.Reflection;

namespace WirelessCable.Server;

/// <summary>
/// 程序集版本。
///
/// 版本号唯一来源是根目录的 <c>Directory.Build.props</c>——不要在代码里再写死一份，
/// 否则发布时总有一处忘改（阶段 5 收尾时就出现过界面上还显示旧版本号）。
/// </summary>
internal static class AppVersionInfo
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AppVersionInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink 会在 informational version 后追加 "+<commit>"，去掉这一段
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
