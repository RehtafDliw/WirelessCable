using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 文件管理端点的集成测试：真实 HTTP、真实鉴权、真实文件系统。
///
/// 覆盖两类最容易出事的场景：
/// - **越权**：未授权设备、未授权目录、路径穿越、绝对路径绕过；
/// - **真实操作**：建目录、重命名、移动、删除真的落到磁盘上。
/// </summary>
public sealed class FileEndpointTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public FileEndpointTests(ServerFixture fixture) => _fixture = fixture;

    private string CreateSharedDirectory()
    {
        var path = Path.Combine(_fixture.RootDirectory, "shared-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<(string DirectoryId, string Token)> SetupSharedAsync(
        string path,
        SharedDirectoryPermissions permissions)
    {
        var (_, token) = await _fixture.PairAsync("文件管理手机");

        var repository = new SharedDirectoryRepository(new WirelessCable.Storage.Sqlite.SqliteDatabaseFactory(
            _fixture.Paths.DatabasePath));

        var directoryId = Guid.NewGuid().ToString("N");
        await repository.UpsertAsync(new SharedDirectoryRecord
        {
            DirectoryId = directoryId,
            DisplayName = "测试目录",
            RootPath = path,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        var deviceId = await GetDeviceIdAsync(token);
        await repository.SetGrantAsync(directoryId, deviceId, permissions);

        return (directoryId, token);
    }

    private async Task<string> GetDeviceIdAsync(string token)
    {
        var devices = await new DeviceRepository(new WirelessCable.Storage.Sqlite.SqliteDatabaseFactory(
            _fixture.Paths.DatabasePath)).ListAsync();

        var hash = WirelessCable.Infrastructure.Security.PairingCrypto.HashToken(token);
        var matched = devices.FirstOrDefault(d =>
            string.Equals(d.TokenHash, hash, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(matched);
        return matched!.DeviceId;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Headers.Add(ProtocolConstants.VersionHeader, ProtocolConstants.CurrentVersion.ToString());

        if (body is not null)
        {
            request.Content = new StringContent(
                WcJson.Serialize(body),
                Encoding.UTF8,
                "application/json");
        }

        return request;
    }

    private async Task<(HttpStatusCode Status, JsonElement? Data, string? ErrorCode)> SendAsync(
        HttpMethod method,
        string path,
        string token,
        object? body = null)
    {
        using var request = Authorized(method, path, token, body);
        using var response = await _fixture.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(text))
        {
            return (response.StatusCode, null, null);
        }

        // JsonDocument 出了 using 就会被释放，元素随之失效——
        // 这里把文档克隆出来，调用方才能安全地继续访问。
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            return (response.StatusCode, root.GetProperty("data").Clone(), null);
        }

        var code = root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var c)
            ? c.GetString()
            : null;

        return (response.StatusCode, null, code);
    }

    // ------------------------------------------------------------ 鉴权

    [Fact]
    public async Task FsEndpoints_WithoutToken_Return401()
    {
        using var response = await _fixture.Client.GetAsync(ProtocolConstants.FileSystemPath + "/roots");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FsEndpoints_WithInvalidToken_Return401()
    {
        var (status, _, _) = await SendAsync(
            HttpMethod.Get,
            ProtocolConstants.FileSystemPath + "/roots",
            "not-a-real-token-value-xxxxxxxxxxxx");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    // ------------------------------------------------------------ 授权

    [Fact]
    public async Task UngrantedDirectory_Returns403()
    {
        var path = CreateSharedDirectory();
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.None);

        var (status, _, errorCode) = await SendAsync(
            HttpMethod.Get,
            $"{ProtocolConstants.FileSystemPath}/entries?rootId={directoryId}&path=",
            token);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(ErrorCodes.DirectoryNotAuthorized, errorCode);
    }

    [Fact]
    public async Task UnknownDirectory_Returns403()
    {
        var (_, token) = await _fixture.PairAsync("未知目录手机");

        var (status, _, errorCode) = await SendAsync(
            HttpMethod.Get,
            $"{ProtocolConstants.FileSystemPath}/entries?rootId=no-such-dir&path=",
            token);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(ErrorCodes.DirectoryNotAuthorized, errorCode);
    }

    [Fact]
    public async Task ReadOnlyDirectory_RejectsDeleteWith403()
    {
        var path = CreateSharedDirectory();
        await File.WriteAllTextAsync(Path.Combine(path, "keep.txt"), "keep");
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.ReadOnly);

        var (status, _, errorCode) = await SendAsync(
            HttpMethod.Post,
            ProtocolConstants.FileSystemPath + "/delete",
            token,
            new FileDeleteRequestDto { RootId = directoryId, Path = "keep.txt" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(ErrorCodes.OperationNotPermitted, errorCode);
        Assert.True(File.Exists(Path.Combine(path, "keep.txt")));
    }

    // ------------------------------------------------------------ 路径穿越

    [Theory]
    [InlineData("../")]
    [InlineData("../../Windows/System32")]
    [InlineData("..\\..\\secret")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows")]
    public async Task TraversalPaths_Return403(string attack)
    {
        var path = CreateSharedDirectory();
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.All);

        var (status, _, errorCode) = await SendAsync(
            HttpMethod.Get,
            $"{ProtocolConstants.FileSystemPath}/entries?rootId={directoryId}&path={Uri.EscapeDataString(attack)}",
            token);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(ErrorCodes.PathForbidden, errorCode);
    }

    [Fact]
    public async Task DeleteOutsideRoot_DoesNotTouchFile()
    {
        var path = CreateSharedDirectory();
        var outside = Path.Combine(_fixture.RootDirectory, "outside-target.txt");
        await File.WriteAllTextAsync(outside, "must survive");

        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.All);

        var (status, _, _) = await SendAsync(
            HttpMethod.Post,
            ProtocolConstants.FileSystemPath + "/delete",
            token,
            new FileDeleteRequestDto { RootId = directoryId, Path = "../outside-target.txt" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task CreateDirectory_WithTraversalInName_IsRejected()
    {
        var path = CreateSharedDirectory();
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.All);

        var (status, _, _) = await SendAsync(
            HttpMethod.Post,
            ProtocolConstants.FileSystemPath + "/mkdir",
            token,
            new FileCreateDirectoryRequestDto { RootId = directoryId, Name = "../escape" });

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.False(Directory.Exists(Path.Combine(_fixture.RootDirectory, "escape")));
    }

    // ------------------------------------------------------------ 真实操作

    [Fact]
    public async Task CreateDirectory_ReallyCreatesOnDisk()
    {
        var path = CreateSharedDirectory();
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.All);

        var (status, _, _) = await SendAsync(
            HttpMethod.Post,
            ProtocolConstants.FileSystemPath + "/mkdir",
            token,
            new FileCreateDirectoryRequestDto { RootId = directoryId, Name = "新建目录" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(Directory.Exists(Path.Combine(path, "新建目录")));
    }

    [Fact]
    public async Task Rename_ReallyRenamesOnDisk()
    {
        var path = CreateSharedDirectory();
        await File.WriteAllTextAsync(Path.Combine(path, "old.txt"), "x");
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.All);

        var (status, _, _) = await SendAsync(
            HttpMethod.Post,
            ProtocolConstants.FileSystemPath + "/rename",
            token,
            new FileRenameRequestDto { RootId = directoryId, Path = "old.txt", NewName = "new.txt" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(File.Exists(Path.Combine(path, "new.txt")));
        Assert.False(File.Exists(Path.Combine(path, "old.txt")));
    }

    [Fact]
    public async Task Move_ReallyMovesOnDisk()
    {
        var path = CreateSharedDirectory();
        Directory.CreateDirectory(Path.Combine(path, "target"));
        await File.WriteAllTextAsync(Path.Combine(path, "m.txt"), "x");
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.All);

        var (status, _, _) = await SendAsync(
            HttpMethod.Post,
            ProtocolConstants.FileSystemPath + "/move",
            token,
            new FileMoveRequestDto
            {
                RootId = directoryId,
                SourcePath = "m.txt",
                DestinationPath = "target/m.txt",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(File.Exists(Path.Combine(path, "target", "m.txt")));
    }

    [Fact]
    public async Task Delete_ReallyDeletesOnDisk()
    {
        var path = CreateSharedDirectory();
        await File.WriteAllTextAsync(Path.Combine(path, "gone.txt"), "x");
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.All);

        var (status, _, _) = await SendAsync(
            HttpMethod.Post,
            ProtocolConstants.FileSystemPath + "/delete",
            token,
            new FileDeleteRequestDto { RootId = directoryId, Path = "gone.txt" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(File.Exists(Path.Combine(path, "gone.txt")));
    }

    [Fact]
    public async Task List_ReturnsEntriesWithCorrectShape()
    {
        var path = CreateSharedDirectory();
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(path, "sub"));
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.All);

        var (status, data, _) = await SendAsync(
            HttpMethod.Get,
            $"{ProtocolConstants.FileSystemPath}/entries?rootId={directoryId}&path=",
            token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(data);

        var entries = data!.Value.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());

        var names = entries.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(["a.txt", "sub"], names);
    }

    [Fact]
    public async Task BatchDelete_ReportsPerItemResult()
    {
        var path = CreateSharedDirectory();
        await File.WriteAllTextAsync(Path.Combine(path, "ok1.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(path, "ok2.txt"), "x");
        var (directoryId, token) = await SetupSharedAsync(path, SharedDirectoryPermissions.All);

        var (status, data, _) = await SendAsync(
            HttpMethod.Post,
            ProtocolConstants.FileSystemPath + "/batch-delete",
            token,
            new FileBatchRequestDto
            {
                RootId = directoryId,
                Paths = ["ok1.txt", "ok2.txt", "not-exist.txt"],
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(data);

        var results = data!.Value.EnumerateArray().ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal(2, results.Count(r => r.GetProperty("success").GetBoolean()));
        Assert.False(File.Exists(Path.Combine(path, "ok1.txt")));
        Assert.False(File.Exists(Path.Combine(path, "ok2.txt")));
    }
}
