namespace WirelessCable.Core.Domain;

/// <summary>
/// 能力位 → 界面上的「这个按钮现在能不能点」。
///
/// 与 <see cref="FileProviderCapabilities"/> 配合使用，规则只有一条但很关键：
/// **0 表示"不知道"，不是"什么都不支持"**。老版本手机不上报能力位时索引里全是 0，
/// 若按位与判成不支持，整页按钮会一起灰掉，用户会以为手机什么都不让做。
/// 这种情况放行，由服务端在下发作业时用准确的能力位拒绝并给出原因。
/// </summary>
public static class FileCapabilityGate
{
    /// <summary>已知能力位是否包含所需项；能力位为 0（未知）时放行。</summary>
    public static bool Allows(int capabilities, int required)
        => required == 0 || capabilities == 0 || (capabilities & required) == required;
}
