using Makaretu.Dns;

namespace WirelessCable.Infrastructure.Networking;

/// <summary>
/// 进程内唯一的 mDNS 通道。
///
/// 为什么必须共享：<see cref="MulticastService"/> 固定绑 UDP 5353，并且假设自己是
/// 该端口唯一的读者。同进程内建两个实例时，第二个虽然 Start() 不报错，却收不到
/// 任何组播——表现为"服务广播正常、浏览端永远搜不到设备"（真机实测）。
/// 桌面端既要广播自己、又要浏览手机，所以只能共用一条通道。
///
/// 用引用计数管理启停：广播方与浏览方各自 Acquire/Release，最后一个释放时才真正 Stop。
/// </summary>
internal static class MdnsBus
{
    private static readonly MulticastService Mdns = new();
    private static readonly Lock Gate = new();

    private static int _refCount;

    /// <summary>共享实例。仅用于构造 <see cref="ServiceDiscovery"/>；启停必须走 Acquire/Release。</summary>
    public static MulticastService Instance => Mdns;

    /// <summary>收到任意应答报文。与 <see cref="MulticastService.AnswerReceived"/> 等价。</summary>
    public static event EventHandler<MessageEventArgs>? AnswerReceived
    {
        add { lock (Gate) { Mdns.AnswerReceived += value; } }
        remove { lock (Gate) { Mdns.AnswerReceived -= value; } }
    }

    public static void Acquire()
    {
        lock (Gate)
        {
            if (_refCount == 0)
            {
                Mdns.Start();
            }

            _refCount++;
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (_refCount == 0)
            {
                return;
            }

            _refCount--;

            if (_refCount == 0)
            {
                Mdns.Stop();
            }
        }
    }

    public static void SendQuery(DomainName name, DnsClass @class, DnsType type)
    {
        lock (Gate)
        {
            // 未 Acquire 时发送会抛「未启动」，交给调用方的 try/catch 处理
            Mdns.SendQuery(name, @class, type);
        }
    }
}
