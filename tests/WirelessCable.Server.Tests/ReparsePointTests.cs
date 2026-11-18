using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Json;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;
using WirelessCable.Storage.Sqlite;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 重解析点（Junction）逃逸。
///
/// 攻击模型：设备能往共享目录里写东西之后，放一个指向 <c>C:\Windows</c> 的 Junction，
/// 再让电脑"顺着链接"把外面的文件列出来或删掉。
/// 防护是 PathGuard（相对路径）+ ReparsePointGuard（逐段解析重解析点）。
///
/// 用 Junction 而不是符号链接：Junction 创建不需要管理员权限，测试在任何机器上都能跑，
/// 而两者的文件属性都是 <see cref="FileAttributes.ReparsePoint"/>，走同一条检查。
/// </summary>
public sealed class ReparsePointTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public ReparsePointTests(ServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task JunctionInsideSharedRoot_CannotEscape()
    {
        var shared = Path.Combine(_fixture.RootDirectory, "shared-link-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shared);

        // 共享目录之外的"机密"目录，链接指向它
        var outside = Path.Combine(_fixture.RootDirectory, "outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secret, "top secret");

        var link = Path.Combine(shared, "link");
        if (!TryCreateJunction(link, outside))
        {
            // 极少数环境（无 cmd 权限）创建不了：跳过，但要留痕而不是假装通过
            Console.WriteLine("[skip] 无法创建 Junction，跳过逃逸用例");
            return;
        }

        try
        {
            var (deviceId, token) = await _fixture.PairAsync("链接测试手机");
            var repository = new SharedDirectoryRepository(new SqliteDatabaseFactory(_fixture.Paths.DatabasePath));

            var directoryId = Guid.NewGuid().ToString("N");
            await repository.UpsertAsync(new SharedDirectoryRecord
            {
                DirectoryId = directoryId,
                DisplayName = "带链接的目录",
                RootPath = shared,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            await repository.SetGrantAsync(directoryId, deviceId, SharedDirectoryPermissions.All);

            // 1) 顺着链接列目录必须被拒
            var listing = await SendAsync(
                HttpMethod.Get,
                $"{ProtocolConstants.FileSystemPath}/entries?rootId={directoryId}&path=link",
                token);
            Assert.False(listing.Ok, "通过 Junction 列目录应当被拒绝");
            Assert.NotEqual(HttpStatusCode.OK, listing.Status);

            // 2) 顺着链接读文件必须被拒
            var info = await SendAsync(
                HttpMethod.Get,
                $"{ProtocolConstants.FileSystemPath}/info?rootId={directoryId}&path=link/secret.txt",
                token);
            Assert.False(info.Ok, "通过 Junction 读文件应当被拒绝");

            // 3) 顺着链接删文件必须被拒，且外面的文件要完好
            var delete = await SendAsync(
                HttpMethod.Post,
                $"{ProtocolConstants.FileSystemPath}/delete",
                token,
                new { rootId = directoryId, path = "link/secret.txt", recursive = false });
            Assert.False(delete.Ok, "通过 Junction 删文件应当被拒绝");

            // 4) 连链接本身一起递归删：只能删掉链接，不能动目标
            await SendAsync(
                HttpMethod.Post,
                $"{ProtocolConstants.FileSystemPath}/delete",
                token,
                new { rootId = directoryId, path = "link", recursive = true });

            Assert.True(File.Exists(secret), "共享目录外的文件被牵连删除");
            Assert.Equal("top secret", await File.ReadAllTextAsync(secret));
        }
        finally
        {
            TryDeleteDirectory(link);
            TryDeleteDirectory(outside);
            TryDeleteDirectory(shared);
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return Directory.Exists(link);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响用例结论
        }
    }

    private async Task<(bool Ok, HttpStatusCode Status)> SendAsync(
        HttpMethod method,
        string path,
        string token,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Headers.Add(ProtocolConstants.VersionHeader, ProtocolConstants.CurrentVersion.ToString());

        if (body is not null)
        {
            request.Content = new StringContent(WcJson.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await _fixture.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, response.StatusCode);
        }

        using var document = JsonDocument.Parse(text);
        var ok = document.RootElement.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
        return (ok, response.StatusCode);
    }
}
