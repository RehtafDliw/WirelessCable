using WirelessCable.Core.Security;

namespace WirelessCable.Infrastructure.Security;

/// <summary>
/// 符号链接 / Junction 检查。路径字符串合法（通过 <see cref="PathGuard"/>）不代表物理路径安全：
/// 攻击者可以在授权目录里放一个指向 C:\Windows 的 Junction。
///
/// 因此每次文件操作前，必须从授权根逐段走到目标，任一段是重解析点即拒绝。
/// </summary>
public static class ReparsePointGuard
{
    /// <summary>目标路径（或其任一祖先，不含授权根本身）是否为重解析点。异常一律按不安全处理。</summary>
    public static bool ContainsReparsePoint(string root, string fullPath)
    {
        if (!PathGuard.IsWithinRoot(root, fullPath))
        {
            return true;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var remainder = fullPath.Length > normalizedRoot.Length
            ? fullPath[(normalizedRoot.Length + 1)..]
            : string.Empty;

        if (remainder.Length == 0)
        {
            return false;
        }

        var current = normalizedRoot;
        foreach (var segment in remainder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            try
            {
                if (!Directory.Exists(current) && !File.Exists(current))
                {
                    // 还未创建的目标：其祖先已经检查过，后续段不可能存在链接
                    return false;
                }

                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}
