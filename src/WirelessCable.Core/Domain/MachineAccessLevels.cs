namespace WirelessCable.Core.Domain;

/// <summary>
/// 一台已配对设备能对"此电脑"（电脑整机）做什么。
///
/// 0.6.x 时这里是布尔值：要么看不到整机，要么只读。1.1.0 起改成三档——
/// 产品上改为"默认开放全部文件"，所以必须有"可读写"这一档；
/// 用户不想全开时可以在「共享目录」页降成只读或关闭，关掉后只剩共享目录。
///
/// 1.3.1 起拆出第四档：**删除是唯一不可恢复的动作**，和"新建/改名/移动"混在同一个
/// "可读写"里，用户看不出自己到底授权了什么。现在：
/// <list type="bullet">
/// <item>只读 —— 能看、能下载；</item>
/// <item>可读写 —— 能上传/新建/改名/移动，<b>不能删</b>；</item>
/// <item>可读写 + 删除 —— 上面全部，再加重删除。</item>
/// </list>
/// 老库里的 2 是"旧的可读写（含删除）"，升级时整体抬到 3，免得用户升级后突然删不了。
/// </summary>
public static class MachineAccessLevels
{
    /// <summary>看不到"此电脑"，只能看共享目录。</summary>
    public const int None = 0;

    /// <summary>能看到全部盘符，但不能改。</summary>
    public const int ReadOnly = 1;

    /// <summary>能看到并修改全部盘符（新建、上传、改名、移动），但不能删除。</summary>
    public const int ReadWrite = 2;

    /// <summary>可读写再加删除。删除不可恢复，所以单独一档。</summary>
    public const int ReadWriteDelete = 3;

    /// <summary>
    /// 新配对设备的默认值。保持 1.1.0 的产品决定：「默认开放全部文件」，即包含删除；
    /// 不想全开的用户自己在桌面端降档。
    /// </summary>
    public const int Default = ReadWriteDelete;

    public static bool CanBrowse(int level) => level >= ReadOnly;

    /// <summary>新建 / 上传 / 改名 / 移动。不含删除。</summary>
    public static bool CanWrite(int level) => level >= ReadWrite;

    /// <summary>删除。只有最高档才给。</summary>
    public static bool CanDelete(int level) => level >= ReadWriteDelete;

    /// <summary>界面上的短标签。两个写档位的差别必须写出来，否则用户分不清。</summary>
    public static string Describe(int level) => level switch
    {
        ReadWriteDelete => "可读写 + 删除",
        ReadWrite => "可读写（不能删）",
        ReadOnly => "只读",
        _ => "关闭",
    };

    /// <summary>把任意整数收敛到合法档位，避免脏数据让权限判断出现空洞。</summary>
    public static int Normalize(int level) => level switch
    {
        >= ReadWriteDelete => ReadWriteDelete,
        ReadWrite => ReadWrite,
        ReadOnly => ReadOnly,
        _ => None,
    };
}
