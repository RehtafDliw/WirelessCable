using System.Diagnostics;

namespace WirelessCable.Infrastructure.Networking;

/// <summary>一条入站放行规则。</summary>
/// <param name="Name">规则名（Windows 防火墙里显示的名字）。</param>
/// <param name="Protocol">TCP 或 UDP。</param>
/// <param name="Port">本地端口。</param>
public sealed record FirewallRule(string Name, string Protocol, int Port);

/// <summary>防火墙自动配置结果。</summary>
public enum FirewallStatus
{
    /// <summary>规则已存在，无需处理。</summary>
    AlreadyConfigured = 0,

    /// <summary>刚刚自动添加成功。</summary>
    Applied = 1,

    /// <summary>需要管理员权限，调用方应请求提权。</summary>
    ElevationRequired = 2,

    /// <summary>配置失败（非权限原因）。</summary>
    Failed = 3,
}

/// <summary>
/// 内网可达所需的 Windows 防火墙放行。
///
/// 背景：Windows 默认「阻止入站」，局域网里的手机连不上 47110，mDNS 的组播也进不来。
/// 用户不应该被要求去"手动加规则"，所以这里做三件事：
/// <list type="number">
/// <item>检测规则是否存在；</item>
/// <item>能加就直接加（进程已有管理员权限时）；</item>
/// <item>加不了就返回 <see cref="FirewallStatus.ElevationRequired"/>，由界面发起一次 UAC 提权后重试。</item>
/// </list>
/// 只动自己命名的规则，不改任何全局策略，也不碰其他程序。
/// </summary>
public static class FirewallConfigurator
{
    /// <summary>主程序所需的两条规则。端口变化时 <see cref="BuildRules"/> 会重建。</summary>
    public static IReadOnlyList<FirewallRule> BuildRules(int httpsPort) =>
    [
        new FirewallRule($"WirelessCable HTTPS {httpsPort}", "TCP", httpsPort),
        new FirewallRule("WirelessCable mDNS 5353", "UDP", 5353),
    ];

    /// <summary>检查并尝试配置。不做提权，提权由调用方决定。</summary>
    public static FirewallStatus Ensure(IReadOnlyList<FirewallRule> rules, out string? detail)
    {
        detail = null;

        var missing = rules.Where(rule => !RuleExists(rule.Name)).ToArray();
        if (missing.Length == 0)
        {
            return FirewallStatus.AlreadyConfigured;
        }

        foreach (var rule in missing)
        {
            var result = RunNetsh(
                $"advfirewall firewall add rule name=\"{rule.Name}\" dir=in action=allow protocol={rule.Protocol} localport={rule.Port} profile=any",
                out var output);

            if (!result)
            {
                detail = output;
                return IsPermissionFailure(output)
                    ? FirewallStatus.ElevationRequired
                    : FirewallStatus.Failed;
            }
        }

        // 加完再验一次，避免 netsh 静默失败
        if (rules.Any(rule => !RuleExists(rule.Name)))
        {
            detail = "规则写入后未生效。";
            return FirewallStatus.Failed;
        }

        return FirewallStatus.Applied;
    }

    /// <summary>
    /// 以管理员身份重新启动自身并执行 <c>--configure-firewall</c>。
    /// 会弹出 UAC，用户点「是」才会继续——这是唯一的交互，且不需要用户填任何配置。
    /// </summary>
    public static bool TryElevateAndApply(string executablePath, int port, out string? error)
    {
        error = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"--configure-firewall {port}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "无法启动提权进程。";
                return false;
            }

            if (!process.WaitForExit(60_000))
            {
                error = "提权进程超时。";
                return false;
            }

            if (process.ExitCode != 0)
            {
                error = $"提权进程返回 {process.ExitCode}。";
                return false;
            }

            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户取消 UAC
            error = "已取消授权。";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>供 <c>--configure-firewall</c> 子模式调用。</summary>
    public static int ApplyAndReport(int port)
    {
        var status = Ensure(BuildRules(port), out var detail);
        return status is FirewallStatus.Applied or FirewallStatus.AlreadyConfigured ? 0 : 1;
    }

    private static bool RuleExists(string name)
    {
        // 找不到匹配规则时 netsh 返回 1
        return RunNetsh($"advfirewall firewall show rule name=\"{name}\"", out _);
    }

    private static bool RunNetsh(string arguments, out string output)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                output = "无法启动 netsh。";
                return false;
            }

            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return false;
        }
    }

    private static bool IsPermissionFailure(string output)
        => output.Contains("需要提升", StringComparison.OrdinalIgnoreCase)
           || output.Contains("elevation", StringComparison.OrdinalIgnoreCase)
           || output.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
           || output.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
           || output.Contains("Administrator", StringComparison.OrdinalIgnoreCase);
}
