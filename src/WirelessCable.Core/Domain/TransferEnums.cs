namespace WirelessCable.Core.Domain;

/// <summary>传输方向（以 Windows 主服务端为视角）。</summary>
public enum TransferDirection
{
    /// <summary>手机 → 电脑。</summary>
    Upload = 0,

    /// <summary>电脑 → 手机。</summary>
    Download = 1,
}

/// <summary>
/// 传输任务状态。持久化到数据库，数值与名称必须稳定（数据库存字符串，改名前先迁移）。
/// 阶段 3 只用到前七个；<see cref="Paused"/> / <see cref="Interrupted"/> 为阶段 4 断点续传预留。
/// </summary>
public enum TransferState
{
    /// <summary>已创建，等待开始。</summary>
    Waiting = 0,

    /// <summary>临时文件已建立，尚未收到第一块数据。</summary>
    Preparing = 1,

    /// <summary>正在传输。</summary>
    Transferring = 2,

    /// <summary>字节已到齐，正在计算哈希并落盘。</summary>
    Verifying = 3,

    /// <summary>哈希校验通过，已提交到目标目录。</summary>
    Completed = 4,

    /// <summary>失败（校验不一致、IO 错误、超时等）。</summary>
    Failed = 5,

    /// <summary>用户取消。</summary>
    Cancelled = 6,

    /// <summary>用户暂停（阶段 4）。</summary>
    Paused = 7,

    /// <summary>网络中断，等待恢复（阶段 4）。</summary>
    Interrupted = 8,
}
