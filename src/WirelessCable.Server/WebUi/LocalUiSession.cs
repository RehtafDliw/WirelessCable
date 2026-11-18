using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WirelessCable.Server.WebUi;

/// <summary>
/// 本机界面的会话令牌（阶段 14.1b）。
///
/// 为什么需要它：Web 界面和手机端走同一台 Kestrel。手机端靠"设备令牌"证明自己是已配对的设备，
/// 浏览器里没有那套东西；如果只按"来自 loopback"放行，同一台机器上的任何程序都能读写文件接口。
///
/// 规则：
/// <list type="number">
/// <item>令牌每次启动随机生成，只存在内存里，不落盘、不进日志；</item>
/// <item>只在 <b>loopback</b> 请求上认，局域网里的其它主机拿着也没用；</item>
/// <item>只保护 <c>/app/api/*</c>：静态资源与数据接口（<c>/api/v1/*</c>）不受影响。</item>
/// </list>
/// </summary>
public sealed class LocalUiSession
{
    public const string HeaderName = "X-WC-Local";

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>前端拿令牌的入口：只有 loopback 能读，页面启动时调一次。</summary>
    public static bool IsLoopback(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is not null && System.Net.IPAddress.IsLoopback(address);
    }
}
