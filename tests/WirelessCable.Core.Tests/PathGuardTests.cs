using WirelessCable.Core.Security;
using Xunit;

namespace WirelessCable.Core.Tests;

public sealed class PathGuardTests
{
    private const string Root = @"C:\Users\tester\WirelessCable\received";

    [Fact]
    public void EmptyPath_ResolvesToRoot()
    {
        var result = PathGuard.Resolve(Root, string.Empty);

        Assert.Equal(PathCheckStatus.Ok, result.Status);
        Assert.Equal(Root, result.FullPath);
    }

    [Fact]
    public void SimpleRelativePath_IsAllowed()
    {
        var result = PathGuard.Resolve(Root, "photos/2026/a.jpg");

        Assert.True(result.IsAllowed);
        Assert.Equal(@"C:\Users\tester\WirelessCable\received\photos\2026\a.jpg", result.FullPath);
    }

    [Fact]
    public void SingleSegment_IsAllowed()
    {
        var result = PathGuard.Resolve(Root, "report.pdf");

        Assert.True(result.IsAllowed);
        Assert.Equal(Root + @"\report.pdf", result.FullPath);
    }

    [Theory]
    [InlineData(@"..\secret.txt")]
    [InlineData(@"a\..\..\secret.txt")]
    [InlineData(@"a/b/../../../etc/passwd")]
    public void Traversal_IsRejected(string candidate)
    {
        var result = PathGuard.Resolve(Root, candidate);

        Assert.Equal(PathCheckStatus.Traversal, result.Status);
        Assert.False(result.IsAllowed);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData(@"/etc/passwd")]
    [InlineData("C:relative.txt")]
    public void AbsolutePath_IsRejected(string candidate)
    {
        var result = PathGuard.Resolve(Root, candidate);

        Assert.Equal(PathCheckStatus.AbsolutePathNotAllowed, result.Status);
    }

    [Theory]
    [InlineData("file.txt:hidden")]
    [InlineData("dir/notes.txt:Zone.Identifier")]
    public void AlternateDataStream_IsRejected(string candidate)
    {
        var result = PathGuard.Resolve(Root, candidate);

        Assert.Equal(PathCheckStatus.AlternateDataStream, result.Status);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData(@"dir\NUL.log")]
    [InlineData("COM1")]
    public void ReservedDeviceName_IsRejected(string candidate)
    {
        var result = PathGuard.Resolve(Root, candidate);

        Assert.Equal(PathCheckStatus.ReservedDeviceName, result.Status);
    }

    [Theory]
    [InlineData("file.")]
    [InlineData("file ")]
    public void TrailingDotOrSpace_IsRejected(string candidate)
    {
        var result = PathGuard.Resolve(Root, candidate);

        Assert.Equal(PathCheckStatus.TrailingDotOrSpace, result.Status);
    }

    [Theory]
    [InlineData("a|b.txt")]
    [InlineData("a*b.txt")]
    [InlineData("a?b.txt")]
    public void InvalidCharacter_IsRejected(string candidate)
    {
        var result = PathGuard.Resolve(Root, candidate);

        Assert.Equal(PathCheckStatus.InvalidCharacter, result.Status);
    }

    [Fact]
    public void RelativeRoot_IsRejected()
    {
        var result = PathGuard.Resolve(@"relative\path", "file.txt");

        Assert.Equal(PathCheckStatus.RootNotAbsolute, result.Status);
    }

    [Fact]
    public void SiblingDirectoryWithSharedPrefix_IsOutsideRoot()
    {
        Assert.False(PathGuard.IsWithinRoot(@"C:\data", @"C:\data2\file.txt"));
        Assert.True(PathGuard.IsWithinRoot(@"C:\data", @"C:\data\file.txt"));
        Assert.True(PathGuard.IsWithinRoot(@"C:\data", @"C:\data"));
    }

    [Fact]
    public void DriveRoot_IsSupported()
    {
        var result = PathGuard.Resolve(@"C:\", "temp/file.txt");

        Assert.True(result.IsAllowed);
        Assert.Equal(@"C:\temp\file.txt", result.FullPath);
    }

    [Fact]
    public void ReasonCode_IsStableForClient()
    {
        Assert.Equal("path_traversal", PathGuard.Resolve(Root, "..").ReasonCode);
        Assert.Equal("ok", PathGuard.Resolve(Root, "a.txt").ReasonCode);
    }

    [Theory]
    [InlineData("normal.txt", "normal.txt")]
    [InlineData("a/b.txt", "a_b.txt")]
    [InlineData("pad?.txt", "pad_.txt")]
    [InlineData("  ", "file")]
    public void SanitizeFileName_RemovesIllegalParts(string input, string expected)
    {
        Assert.Equal(expected, PathGuard.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_AvoidsReservedName()
    {
        Assert.StartsWith("_", PathGuard.SanitizeFileName("CON.txt"));
    }
    // ------------------------------------------------------------ 整机路径（"此电脑"视图）

    [Fact]
    public void MachinePath_DriveOnly_ResolvesToDriveRoot()
    {
        var result = PathGuard.ResolveMachinePath("C:");

        Assert.True(result.IsAllowed);
        Assert.Equal("C:\\", result.FullPath);
    }

    [Fact]
    public void MachinePath_DriveWithSlash_ResolvesToDriveRoot()
    {
        var result = PathGuard.ResolveMachinePath("C:/");

        Assert.True(result.IsAllowed);
        Assert.Equal("C:\\", result.FullPath);
    }

    [Fact]
    public void MachinePath_NestedPath_IsAllowed()
    {
        var result = PathGuard.ResolveMachinePath("C:/Users/tester/Documents/report.pdf");

        Assert.True(result.IsAllowed);
        Assert.Equal("C:\\Users\\tester\\Documents\\report.pdf", result.FullPath);
    }

    [Fact]
    public void MachinePath_LowercaseDrive_IsNormalized()
    {
        var result = PathGuard.ResolveMachinePath("d:/data");

        Assert.True(result.IsAllowed);
        Assert.Equal("D:\\data", result.FullPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C")]
    [InlineData("1:")]
    [InlineData("CC:/x")]
    [InlineData("/C:/x")]
    [InlineData(@"\\server\share")]
    public void MachinePath_InvalidDrivePrefix_IsRejected(string path)
    {
        var result = PathGuard.ResolveMachinePath(path);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void MachinePath_Traversal_IsRejected()
    {
        var result = PathGuard.ResolveMachinePath("C:/Users/../../Windows/System32");

        Assert.Equal(PathCheckStatus.Traversal, result.Status);
    }

    [Fact]
    public void MachinePath_AlternateDataStream_IsRejected()
    {
        var result = PathGuard.ResolveMachinePath("C:/Users/tester/file.txt:secret");

        Assert.Equal(PathCheckStatus.AlternateDataStream, result.Status);
    }

    [Fact]
    public void MachinePath_ReservedDeviceName_IsRejected()
    {
        var result = PathGuard.ResolveMachinePath("C:/Users/tester/CON");

        Assert.Equal(PathCheckStatus.ReservedDeviceName, result.Status);
    }

    [Fact]
    public void MachinePath_TrailingDot_IsRejected()
    {
        var result = PathGuard.ResolveMachinePath("C:/Users/tester/name.");

        Assert.Equal(PathCheckStatus.TrailingDotOrSpace, result.Status);
    }

    [Fact]
    public void MachinePath_CannotEscapeDriveRoot()
    {
        // 盘符就是根，'..' 永远出不去
        var result = PathGuard.ResolveMachinePath("C:/../D:/secret");

        Assert.Equal(PathCheckStatus.Traversal, result.Status);
    }

}
