using System.Text;

namespace WirelessCable.Core.Security;

/// <summary>路径校验结果状态。</summary>
public enum PathCheckStatus
{
    Ok = 0,
    EmptyPath,
    RootNotAbsolute,
    InvalidCharacter,
    AbsolutePathNotAllowed,
    Traversal,
    AlternateDataStream,
    ReservedDeviceName,
    TrailingDotOrSpace,
    SegmentTooLong,
    PathTooLong,
    OutsideRoot,
}

/// <summary>路径校验结果。</summary>
/// <param name="Status">校验状态。</param>
/// <param name="FullPath">状态为 <see cref="PathCheckStatus.Ok"/> 时的绝对路径，否则为空串。</param>
public readonly record struct PathCheckResult(PathCheckStatus Status, string FullPath)
{
    public bool IsAllowed => Status == PathCheckStatus.Ok;

    /// <summary>稳定英文原因码，可直接回给客户端。</summary>
    public string ReasonCode => Status switch
    {
        PathCheckStatus.Ok => "ok",
        PathCheckStatus.EmptyPath => "path_empty",
        PathCheckStatus.RootNotAbsolute => "root_not_absolute",
        PathCheckStatus.InvalidCharacter => "path_invalid_character",
        PathCheckStatus.AbsolutePathNotAllowed => "path_absolute_not_allowed",
        PathCheckStatus.Traversal => "path_traversal",
        PathCheckStatus.AlternateDataStream => "path_ads",
        PathCheckStatus.ReservedDeviceName => "path_reserved_name",
        PathCheckStatus.TrailingDotOrSpace => "path_trailing_dot_or_space",
        PathCheckStatus.SegmentTooLong => "path_segment_too_long",
        PathCheckStatus.PathTooLong => "path_too_long",
        PathCheckStatus.OutsideRoot => "path_outside_root",
        _ => "path_invalid",
    };
}

/// <summary>
/// 把客户端给的相对路径解析成授权根内的绝对路径。
///
/// 纯字符串实现，不触碰文件系统，因此可完整单测；符号链接 / Junction 检查需要 IO，
/// 由上层（Infrastructure 的 ReparsePointGuard）在拿到绝对路径后补充。
///
/// 拒绝：相对穿越（..）、绝对路径、盘符、备用数据流（:）、通配符、控制字符、
/// Windows 保留设备名、结尾点或空格、超长段。
/// </summary>
public static class PathGuard
{
    public const int MaxPathLength = 32767;
    public const int MaxSegmentLength = 255;

    private static readonly char[] InvalidChars = ['<', '>', '"', '|', '?', '*'];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 解析相对路径到授权根下。传空字符串表示根目录自身。
    /// </summary>
    public static PathCheckResult Resolve(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return new PathCheckResult(PathCheckStatus.RootNotAbsolute, string.Empty);
        }

        var root = NormalizeRoot(rootDirectory);
        if (root.Length == 0)
        {
            return new PathCheckResult(PathCheckStatus.RootNotAbsolute, string.Empty);
        }

        if (string.IsNullOrEmpty(relativePath))
        {
            return new PathCheckResult(PathCheckStatus.Ok, root);
        }

        // 绝对路径与 UNC 一律拒绝（含 / 与 \ 两种写法）
        var first = relativePath[0];
        if (first is '\\' or '/')
        {
            return new PathCheckResult(PathCheckStatus.AbsolutePathNotAllowed, string.Empty);
        }

        // 盘符与盘符相对路径（C:\、C:/、C:file）同样视为绝对路径，先于 ':' 判定
        if (relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':')
        {
            return new PathCheckResult(PathCheckStatus.AbsolutePathNotAllowed, string.Empty);
        }

        foreach (var ch in relativePath)
        {
            if (ch < 0x20)
            {
                return new PathCheckResult(PathCheckStatus.InvalidCharacter, string.Empty);
            }

            if (Array.IndexOf(InvalidChars, ch) >= 0)
            {
                return new PathCheckResult(PathCheckStatus.InvalidCharacter, string.Empty);
            }
        }

        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return BuildUnderRoot(root, segments);
    }

    /// <summary>
    /// 解析整机路径（"此电脑"视图）：首段是盘符，如 <c>C:</c> / <c>C:/Users/foo</c>。
    ///
    /// 与 <see cref="Resolve"/> 的区别只在第一段：这里允许一个字母 + 冒号的盘符前缀，
    /// 其余规则完全一致——拒绝 <c>..</c> 穿越、备用数据流、保留设备名、结尾点或空格、超长段。
    /// 盘符之后的路径不再能逃出该盘符，因为根就是盘符根本身。
    /// </summary>
    public static PathCheckResult ResolveMachinePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return new PathCheckResult(PathCheckStatus.EmptyPath, string.Empty);
        }

        if (relativePath.Length < 2
            || !char.IsAsciiLetter(relativePath[0])
            || relativePath[1] != ':')
        {
            return new PathCheckResult(PathCheckStatus.AbsolutePathNotAllowed, string.Empty);
        }

        var drive = char.ToUpperInvariant(relativePath[0]);

        // 盘符之后的部分：允许前导分隔符，其余按普通规则校验
        var rest = relativePath[2..].TrimStart('\\', '/');
        if (rest.Length == 0)
        {
            return new PathCheckResult(PathCheckStatus.Ok, drive + ":\\");
        }

        foreach (var ch in rest)
        {
            if (ch < 0x20)
            {
                return new PathCheckResult(PathCheckStatus.InvalidCharacter, string.Empty);
            }

            if (Array.IndexOf(InvalidChars, ch) >= 0)
            {
                return new PathCheckResult(PathCheckStatus.InvalidCharacter, string.Empty);
            }
        }

        var segments = rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return BuildUnderRoot(drive + ":\\", segments);
    }

    /// <summary>
    /// 在给定根下逐段拼装路径并做段级校验。
    /// 段里出现 ':' 判为备用数据流（盘符前缀已在调用方剥离）。
    /// </summary>
    private static PathCheckResult BuildUnderRoot(string root, string[] segments)
    {
        var builder = new StringBuilder(root, root.Length + 64);

        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return new PathCheckResult(PathCheckStatus.Traversal, string.Empty);
            }

            if (segment.IndexOf(':') >= 0)
            {
                // 备用数据流（file.txt:stream）
                return new PathCheckResult(PathCheckStatus.AlternateDataStream, string.Empty);
            }

            if (segment.Length > MaxSegmentLength)
            {
                return new PathCheckResult(PathCheckStatus.SegmentTooLong, string.Empty);
            }

            if (segment[^1] is ' ' or '.')
            {
                return new PathCheckResult(PathCheckStatus.TrailingDotOrSpace, string.Empty);
            }

            if (IsReservedName(segment))
            {
                return new PathCheckResult(PathCheckStatus.ReservedDeviceName, string.Empty);
            }

            if (builder.Length > 0 && builder[^1] != '\\')
            {
                builder.Append('\\');
            }

            builder.Append(segment);
            if (builder.Length > MaxPathLength)
            {
                return new PathCheckResult(PathCheckStatus.PathTooLong, string.Empty);
            }
        }

        var fullPath = builder.ToString();
        if (!IsWithinRoot(root, fullPath))
        {
            return new PathCheckResult(PathCheckStatus.OutsideRoot, string.Empty);
        }

        return new PathCheckResult(PathCheckStatus.Ok, fullPath);
    }

    /// <summary>判断 <paramref name="fullPath"/> 是否位于 <paramref name="root"/> 之内（含根本身）。</summary>
    public static bool IsWithinRoot(string root, string fullPath)
    {
        var normalizedRoot = NormalizeRoot(root);
        if (normalizedRoot.Length == 0 || string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        if (fullPath.Length == normalizedRoot.Length)
        {
            return string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        if (fullPath.Length < normalizedRoot.Length + 1)
        {
            return false;
        }

        // 根以分隔符结尾（如 C:\）时直接比前缀，避免出现 "\\" 双分隔
        var prefix = normalizedRoot.EndsWith('\\') ? normalizedRoot : normalizedRoot + "\\";
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>清洗客户端提供的文件名，去掉非法字符并规避保留名。仅用于接收落盘。</summary>
    public static string SanitizeFileName(string? name, string fallback = "file")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            var rejected = ch < 0x20
                || ch is '\\' or '/' or ':' or '<' or '>' or '"' or '|' or '?' or '*';
            builder.Append(rejected ? '_' : ch);
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        if (IsReservedName(cleaned))
        {
            cleaned = "_" + cleaned;
        }

        if (cleaned.Length > 120)
        {
            var ext = Path.GetExtension(cleaned);
            var keep = Math.Max(1, 120 - ext.Length);
            cleaned = string.Concat(cleaned.AsSpan(0, keep), ext);
        }

        return cleaned;
    }

    private static bool IsReservedName(string segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot >= 0 ? segment[..dot] : segment;
        return ReservedNames.Contains(stem.TrimEnd(' '));
    }

    private static string NormalizeRoot(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return string.Empty;
        }

        var root = rootDirectory.Replace('/', '\\').Trim();
        if (root.Length < 3)
        {
            return string.Empty;
        }

        var isDrive = root.Length >= 3
            && char.IsAsciiLetter(root[0])
            && root[1] == ':'
            && root[2] == '\\';

        var isUnc = root.StartsWith(@"\\", StringComparison.Ordinal) && root.IndexOf('\\', 2) > 2;

        if (!isDrive && !isUnc)
        {
            return string.Empty;
        }

        // 去掉重复分隔符与尾部分隔符（保留 C:\ 这种根形态）
        var builder = new StringBuilder(root.Length);
        foreach (var ch in root)
        {
            if (ch == '\\' && builder.Length > 0 && builder[^1] == '\\')
            {
                continue;
            }
            builder.Append(ch);
        }

        var normalized = builder.ToString();
        if (normalized.Length > 3 && normalized[^1] == '\\')
        {
            normalized = normalized.TrimEnd('\\');
        }

        return normalized;
    }
}
