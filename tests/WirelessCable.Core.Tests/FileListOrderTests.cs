using WirelessCable.Core.Domain;
using Xunit;

namespace WirelessCable.Core.Tests;

/// <summary>
/// 文件列表排序与分页规则。桌面端「我的手机」页直接用它，
/// 因此这里锁死三件事：目录恒在前、升降序语义、越界不崩。
/// </summary>
public sealed class FileListOrderTests
{
    private sealed record Entry(string Name, bool IsDirectory, long SizeBytes, long ModifiedUnixMs)
        : ISortableFileEntry;

    private static readonly Entry DirA = new("alpha", true, 0, 300);
    private static readonly Entry DirB = new("beta", true, 0, 100);
    private static readonly Entry FileBig = new("b.bin", false, 900, 200);
    private static readonly Entry FileSmall = new("a.txt", false, 10, 500);
    private static readonly Entry FileMid = new("c.dat", false, 100, 400);

    private static Entry[] All => [FileBig, DirA, FileSmall, DirB, FileMid];

    private static string[] Names(IEnumerable<Entry> entries) => entries.Select(e => e.Name).ToArray();

    private static Entry[] Sort(string field, bool descending)
    {
        var list = All.ToList();
        list.Sort((l, r) => FileListOrder.Compare(l, r, field, descending));
        return [.. list];
    }

    [Fact]
    public void NameAscending_PutsDirectoriesFirstThenAlphabetical()
    {
        Assert.Equal(
            ["alpha", "beta", "a.txt", "b.bin", "c.dat"],
            Names(Sort(FileSortFields.Name, descending: false)));
    }

    [Fact]
    public void NameDescending_ReversesWithinGroupButKeepsDirectoriesFirst()
    {
        Assert.Equal(
            ["beta", "alpha", "c.dat", "b.bin", "a.txt"],
            Names(Sort(FileSortFields.Name, descending: true)));
    }

    [Fact]
    public void SizeDescending_OrdersFilesByBytes()
    {
        // 目录大小为 0，彼此只按名称兜底（恒升序）；文件按字节数从大到小
        Assert.Equal(
            ["alpha", "beta", "b.bin", "c.dat", "a.txt"],
            Names(Sort(FileSortFields.Size, descending: true)));
    }

    [Fact]
    public void ModifiedAscending_OrdersFilesByTimestamp()
    {
        Assert.Equal(
            ["beta", "alpha", "b.bin", "c.dat", "a.txt"],
            Names(Sort(FileSortFields.Modified, descending: false)));
    }

    [Fact]
    public void UnknownField_FallsBackToName()
    {
        var weird = Sort("nonsense", descending: false);
        var byName = Sort(FileSortFields.Name, descending: false);

        Assert.Equal(Names(byName), Names(weird));
    }

    [Fact]
    public void EqualKeys_FallBackToNameSoOrderIsStable()
    {
        var a = new Entry("same-b", false, 42, 7);
        var b = new Entry("same-a", false, 42, 7);
        var c = new Entry("same-c", false, 42, 7);

        var list = new List<Entry> { a, c, b };
        list.Sort((l, r) => FileListOrder.Compare(l, r, FileSortFields.Size, descending: false));

        Assert.Equal(["same-a", "same-b", "same-c"], Names(list));
    }

    [Fact]
    public void PageCount_EmptyListIsStillOnePage()
    {
        Assert.Equal(1, FileListOrder.PageCount(0, 200));
        Assert.Equal(1, FileListOrder.PageCount(1, 200));
        Assert.Equal(1, FileListOrder.PageCount(200, 200));
        Assert.Equal(2, FileListOrder.PageCount(201, 200));
        Assert.Equal(5, FileListOrder.PageCount(801, 200));
    }

    [Fact]
    public void ClampPage_NeverEscapesRange()
    {
        Assert.Equal(0, FileListOrder.ClampPage(-5, 0, 200));
        Assert.Equal(0, FileListOrder.ClampPage(-5, 500, 200));
        Assert.Equal(2, FileListOrder.ClampPage(99, 500, 200));
        Assert.Equal(1, FileListOrder.ClampPage(1, 500, 200));
    }

    [Fact]
    public void Page_ReturnsRequestedSliceAndLastPageIsShort()
    {
        var items = Enumerable.Range(0, 250).ToArray();

        Assert.Equal(200, FileListOrder.Page(items, 0, 200).Count);
        Assert.Equal(50, FileListOrder.Page(items, 1, 200).Count);
        Assert.Equal(200, FileListOrder.Page(items, 1, 200)[0]);
        Assert.Equal(249, FileListOrder.Page(items, 1, 200)[^1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Page_WithNonPositivePageSize_ReturnsEmpty(int pageSize)
    {
        Assert.Empty(FileListOrder.Page(new[] { 1, 2, 3 }, 0, pageSize));
    }

    [Fact]
    public void Page_OutOfRangeIndexIsClampedNotThrown()
    {
        var items = Enumerable.Range(0, 10).ToArray();

        Assert.Equal(10, FileListOrder.Page(items, 7, 200).Count);
        Assert.Empty(FileListOrder.Page(Array.Empty<int>(), 3, 200));
    }
}
