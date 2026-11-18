using System.Net;
using System.Net.Sockets;

namespace WirelessCable.Infrastructure.Networking;

/// <summary>
/// 端口占用探测。
///
/// 注意：探测与真正绑定之间存在竞态窗口，因此服务端启动仍必须处理
/// "绑定失败 → 换端口重试"，探测只用于提前给出更友好的错误信息。
/// </summary>
public static class PortAllocator
{
    /// <summary>尝试在指定地址上占用端口，成功即视为空闲。</summary>
    public static bool IsPortAvailable(int port, IPAddress? address = null)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        var listener = new TcpListener(address ?? IPAddress.Any, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>从 <paramref name="preferred"/> 起向后找可用端口；全被占用返回 null。</summary>
    public static int? FindAvailablePort(int preferred, int probeCount)
    {
        for (var offset = 0; offset <= probeCount; offset++)
        {
            var candidate = preferred + offset;
            if (candidate > 65535)
            {
                break;
            }

            if (IsPortAvailable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
