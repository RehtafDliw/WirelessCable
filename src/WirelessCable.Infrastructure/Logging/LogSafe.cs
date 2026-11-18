namespace WirelessCable.Infrastructure.Logging;

/// <summary>日志脱敏助手。凡是要写进日志的外部输入都应先过这里。</summary>
public static class LogSafe
{
    private const int FingerprintHead = 12;
    private const int FingerprintTail = 6;

    /// <summary>指纹只保留首尾，避免完整凭据落盘。</summary>
    public static string Fingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return "<none>";
        }

        return fingerprint.Length <= FingerprintHead + FingerprintTail + 1
            ? "<short>"
            : $"{fingerprint[..FingerprintHead]}…{fingerprint[^FingerprintTail..]}";
    }

    /// <summary>路径只保留文件名，避免暴露用户目录结构。</summary>
    public static string Path(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return "<none>";
        }

        return System.IO.Path.GetFileName(fullPath) is { Length: > 0 } name ? name : "<dir>";
    }
}
