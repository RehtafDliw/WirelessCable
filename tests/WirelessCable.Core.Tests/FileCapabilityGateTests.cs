using WirelessCable.Core.Domain;
using Xunit;

namespace WirelessCable.Core.Tests;

/// <summary>
/// 能力位到界面可用性的换算。桌面「我的手机」页每一个按钮都按它决定灰不灰，
/// 所以这里锁死两件事：缺位要判不支持、0（未知）不能判成不支持。
/// </summary>
public sealed class FileCapabilityGateTests
{
    private static int Caps(FileProviderCapabilities caps) => (int)caps;

    [Fact]
    public void 能力位齐全时允许()
    {
        var caps = Caps(FileProviderCapabilities.Rename | FileProviderCapabilities.Delete);

        Assert.True(FileCapabilityGate.Allows(caps, Caps(FileProviderCapabilities.Rename)));
        Assert.True(FileCapabilityGate.Allows(caps, Caps(FileProviderCapabilities.Delete)));
    }

    [Fact]
    public void 缺少能力位时拒绝()
    {
        var caps = Caps(FileProviderCapabilities.ListDirectory | FileProviderCapabilities.ReadFile);

        Assert.False(FileCapabilityGate.Allows(caps, Caps(FileProviderCapabilities.Rename)));
        Assert.False(FileCapabilityGate.Allows(caps, Caps(FileProviderCapabilities.Delete)));
        Assert.False(FileCapabilityGate.Allows(caps, Caps(FileProviderCapabilities.CreateDirectory)));
    }

    [Fact]
    public void 未知能力位放行由服务端定性()
    {
        // 老版本手机不上报能力位：不能因此把按钮全禁掉
        Assert.True(FileCapabilityGate.Allows(0, Caps(FileProviderCapabilities.Delete)));
        Assert.True(FileCapabilityGate.Allows(0, Caps(FileProviderCapabilities.ReadFile)));
    }

    [Fact]
    public void 不需要能力位时恒允许()
    {
        Assert.True(FileCapabilityGate.Allows(0, 0));
        Assert.True(FileCapabilityGate.Allows(Caps(FileProviderCapabilities.None), 0));
    }

    [Fact]
    public void 要求多项时必须全有()
    {
        var caps = Caps(FileProviderCapabilities.Rename | FileProviderCapabilities.Delete);
        var both = Caps(FileProviderCapabilities.Rename | FileProviderCapabilities.Delete);
        var more = both | Caps(FileProviderCapabilities.Move);

        Assert.True(FileCapabilityGate.Allows(caps, both));
        Assert.False(FileCapabilityGate.Allows(caps, more));
    }
}
