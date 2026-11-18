namespace WirelessCable.Core.Domain;

/// <summary>文件列表排序字段。字符串值用于界面绑定与日志，保持稳定。</summary>
public static class FileSortFields
{
    public const string Name = "name";
    public const string Size = "size";
    public const string Modified = "time";

    /// <summary>把界面传进来的任意字符串收敛到三个合法值，默认按名称。</summary>
    public static string Normalize(string? field) => field switch
    {
        Size => Size,
        Modified => Modified,
        _ => Name,
    };
}

/// <summary>排序需要的最小信息。界面行模型实现它即可复用同一套比较规则。</summary>
public interface ISortableFileEntry
{
    string Name { get; }

    bool IsDirectory { get; }

    long SizeBytes { get; }

    long ModifiedUnixMs { get; }
}

/// <summary>
/// 文件列表的排序与分页。
///
/// 抽出来放在 Core 的原因：这是纯函数逻辑，两端（桌面列表、将来的手机列表）
/// 都要用同一套规则，而且要能在没有 WPF / Android 环境的地方直接测。
///
/// 规则：
/// <list type="bullet">
/// <item>目录恒在最前（与资源管理器一致，升序降序都不改变这一点）；</item>
/// <item>同类型内按所选字段比较，相等时退回名称，保证顺序稳定；</item>
/// <item>分页越界一律收敛到合法范围，不抛异常——界面传什么都不会崩。</item>
/// </list>
/// </summary>
public static class FileListOrder
{
    public static int Compare(ISortableFileEntry left, ISortableFileEntry right, string? field, bool descending)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        var normalized = FileSortFields.Normalize(field);

        if (normalized == FileSortFields.Name)
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
            return descending ? -byName : byName;
        }

        var primary = normalized == FileSortFields.Size
            ? left.SizeBytes.CompareTo(right.SizeBytes)
            : left.ModifiedUnixMs.CompareTo(right.ModifiedUnixMs);

        if (descending)
        {
            primary = -primary;
        }

        // 同大小/同时间再用名称兜底，且兜底恒为升序：
        // 目录大小都是 0，若连兜底一起反转，"按大小降序"会看到文件夹倒着排。
        return primary != 0
            ? primary
            : string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>总页数；空列表也算 1 页，界面不必特判。</summary>
    public static int PageCount(int itemCount, int pageSize)
        => pageSize <= 0 ? 1 : Math.Max(1, (itemCount + pageSize - 1) / pageSize);

    /// <summary>把页码收敛到 [0, pageCount-1]。</summary>
    public static int ClampPage(int pageIndex, int itemCount, int pageSize)
        => Math.Clamp(pageIndex, 0, PageCount(itemCount, pageSize) - 1);

    /// <summary>取当前页的切片。参数非法时返回空数组而不是抛异常。</summary>
    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int pageIndex, int pageSize)
    {
        if (items.Count == 0 || pageSize <= 0)
        {
            return [];
        }

        var index = ClampPage(pageIndex, items.Count, pageSize);
        var skip = index * pageSize;
        var take = Math.Min(pageSize, items.Count - skip);

        var page = new T[take];
        for (var i = 0; i < take; i++)
        {
            page[i] = items[skip + i];
        }

        return page;
    }
}
