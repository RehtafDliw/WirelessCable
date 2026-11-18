using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using WirelessCable.Storage.Entities;
using Xunit;

namespace WirelessCable.Server.Tests;

/// <summary>
/// 传输端点集成测试：真实 HTTPS 请求、真实文件落盘。
/// 鉴权、分块顺序、哈希校验与跨设备越权都在这里覆盖。
/// </summary>
public sealed class TransferEndpointTests : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _fixture;

    public TransferEndpointTests(ServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Upload_EndToEnd_CreatesByteIdenticalFile()
    {
        var (deviceId, token) = await _fixture.PairAsync("上传手机");
        var content = RandomBytes(1024 * 1024 + 321);
        var sha = Sha256(content);

        var created = await CreateUploadAsync(token, "upload.bin", content.Length);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal(TransferStates.Preparing, created.Task!.State);

        await PutAllChunksAsync(token, created.Task, content);
        var completed = await CompleteAsync(token, created.Task.TaskId, sha);

        Assert.Equal(HttpStatusCode.OK, completed.Status);
        Assert.Equal(TransferStates.Completed, completed.Response!.State);

        var path = Path.Combine(_fixture.Paths.ReceivedDirectory, completed.Response.RelativePath.Replace('/', '\\'));
        Assert.True(File.Exists(path));
        Assert.Equal(sha, Sha256File(path));

        var status = await GetTaskAsync(token, created.Task.TaskId);
        Assert.Equal(TransferStates.Completed, status.State);
        Assert.Equal(content.Length, status.TransferredBytes);
        Assert.Equal(sha, status.Sha256);
        Assert.NotEqual(0, deviceId.Length);
    }

    [Fact]
    public async Task Transfer_WithoutToken_Returns401()
    {
        using var response = await _fixture.Client.GetAsync(ProtocolConstants.TransferTasksPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(ErrorCodes.Unauthorized, body);
    }

    [Fact]
    public async Task Upload_ChecksumMismatch_Returns409AndNoFile()
    {
        var (_, token) = await _fixture.PairAsync("坏哈希手机");
        var content = RandomBytes(64 * 1024);

        var created = await CreateUploadAsync(token, "bad-checksum.bin", content.Length);
        await PutAllChunksAsync(token, created.Task!, content);

        var completed = await CompleteAsync(token, created.Task!.TaskId, Sha256(RandomBytes(16)));

        Assert.Equal(HttpStatusCode.Conflict, completed.Status);
        Assert.Equal(ErrorCodes.ChecksumMismatch, completed.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_fixture.Paths.ReceivedDirectory, "bad-checksum.bin")));
    }

    [Fact]
    public async Task Chunk_FromAnotherDevice_Returns403()
    {
        var (_, ownerToken) = await _fixture.PairAsync("所有者手机");
        var (_, otherToken) = await _fixture.PairAsync("旁观手机");

        var content = RandomBytes(32 * 1024);
        var created = await CreateUploadAsync(ownerToken, "owned.bin", content.Length);

        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{ProtocolConstants.TransferTasksPath}/{created.Task!.TaskId}/chunks/0")
        {
            Content = new ByteArrayContent(content),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);

        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Chunk_MissingHashHeader_Returns400()
    {
        var (_, token) = await _fixture.PairAsync("缺头手机");
        var content = RandomBytes(32 * 1024);

        var created = await CreateUploadAsync(token, "nohash.bin", content.Length);

        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{ProtocolConstants.TransferTasksPath}/{created.Task!.TaskId}/chunks/0")
        {
            Content = new ByteArrayContent(content),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Chunk_OutOfOrder_AcceptedWithCorrectHash()
    {
        // 阶段 4 起任意顺序：客户端按服务端位图补差集，不再要求严格顺序
        var (_, token) = await _fixture.PairAsync("乱序手机");
        var content = RandomBytes(200 * 1024);

        var created = await CreateUploadAsync(token, "order.bin", content.Length, requestedChunkSize: 64 * 1024);
        Assert.True(created.Task!.ChunkCount > 1);

        var payload = content.Skip(64 * 1024).Take(64 * 1024).ToArray();
        var result = await PutChunkAsync(token, created.Task.TaskId, 1, payload);
        Assert.Equal(HttpStatusCode.OK, result.Status);

        // 补齐后整文件可完成
        await PutChunkAsync(token, created.Task.TaskId, 0, content.Take(64 * 1024).ToArray());
        await PutChunkAsync(token, created.Task.TaskId, 2, content.Skip(128 * 1024).Take(64 * 1024).ToArray());
        await PutChunkAsync(token, created.Task.TaskId, 3, content.Skip(192 * 1024).ToArray());

        var completed = await CompleteAsync(token, created.Task.TaskId, Sha256(content));
        Assert.Equal(HttpStatusCode.OK, completed.Status);
    }

    [Fact]
    public async Task Resume_IsIdempotentAndReturnsServerBitmap()
    {
        // 重复恢复请求：返回同一份服务端状态，绝不产生重复任务
        var (_, token) = await _fixture.PairAsync("恢复手机");
        var content = RandomBytes(128 * 1024);

        var created = await CreateUploadAsync(token, "resume.bin", content.Length, requestedChunkSize: 64 * 1024);
        await PutChunkAsync(token, created.Task!.TaskId, 0, content.Take(64 * 1024).ToArray());

        var first = await SendAsync(HttpMethod.Post,
            $"{ProtocolConstants.TransferTasksPath}/{created.Task.TaskId}/resume", token);
        var second = await SendAsync(HttpMethod.Post,
            $"{ProtocolConstants.TransferTasksPath}/{created.Task.TaskId}/resume", token);

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);

        var state = await GetTaskAsync(token, created.Task.TaskId);
        Assert.Equal(new[] { 0 }, state.CompletedChunks);

        var list = await _fixture.Client.SendAsync(new HttpRequestMessage(
            HttpMethod.Get, ProtocolConstants.TransferTasksPath)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
        });
        var body = await list.Content.ReadAsStringAsync();
        var tasks = WcJson.DeserializeRequired<ApiResult<List<TransferTaskDto>>>(body).Data!;
        Assert.Equal(1, tasks.Count(t => t.FileName == "resume.bin"));
    }

    [Fact]
    public async Task ChunksList_ReturnsPersistedBitmap()
    {
        var (_, token) = await _fixture.PairAsync("位图手机");
        var content = RandomBytes(200 * 1024);

        var created = await CreateUploadAsync(token, "bitmap.bin", content.Length, requestedChunkSize: 64 * 1024);
        await PutChunkAsync(token, created.Task!.TaskId, 0, content.Take(64 * 1024).ToArray());

        var request = new HttpRequestMessage(
            HttpMethod.Get, $"{ProtocolConstants.TransferTasksPath}/{created.Task.TaskId}/chunks");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var chunks = WcJson.DeserializeRequired<ApiResult<List<ChunkInfoDto>>>(body).Data!;

        Assert.Equal(4, chunks.Count);
        Assert.Equal("completed", chunks[0].Status);
        Assert.Equal(64 * 1024, chunks[0].Offset + chunks[0].Length - chunks[0].Offset);
        Assert.All(chunks.Skip(1), c => Assert.Equal("pending", c.Status));
        Assert.All(chunks, c => Assert.Equal(c.Index * 64 * 1024, c.Offset));
    }

    [Fact]
    public async Task Cancel_ThenComplete_Returns409()
    {
        var (_, token) = await _fixture.PairAsync("取消手机");
        var content = RandomBytes(1024);

        var created = await CreateUploadAsync(token, "cancel.bin", content.Length);
        await PutAllChunksAsync(token, created.Task!, content);

        var cancel = await SendAsync(HttpMethod.Post,
            $"{ProtocolConstants.TransferTasksPath}/{created.Task!.TaskId}/cancel", token);
        Assert.Equal(HttpStatusCode.OK, cancel.Status);

        var completed = await CompleteAsync(token, created.Task.TaskId, Sha256(content));
        Assert.Equal(HttpStatusCode.Conflict, completed.Status);
        Assert.Equal(ErrorCodes.TransferStateInvalid, completed.ErrorCode);
    }

    [Fact]
    public async Task List_ContainsCreatedTask()
    {
        var (_, token) = await _fixture.PairAsync("列表手机");
        var created = await CreateUploadAsync(token, "listed.bin", 100);

        var request = new HttpRequestMessage(HttpMethod.Get, ProtocolConstants.TransferTasksPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var result = WcJson.DeserializeRequired<ApiResult<List<TransferTaskDto>>>(body);

        Assert.True(result.Ok);
        Assert.Contains(result.Data!, t => t.TaskId == created.Task!.TaskId);
    }

    [Fact]
    public async Task Download_MultiChunkFile_EachResponseBodyMatchesContentLength()
    {
        // 回归：整块（而非末块）的响应体必须恰好等于声明的 Content-Length。
        // 阶段 4 真机跑 5 GB 下载时出现过
        // "Response Content-Length mismatch: too many bytes written (8519680 of 8388608)"——
        // 写出比声明多 131072 字节（= 2 × 64 KiB，FileStream 的缓冲大小）。
        var (deviceId, token) = await _fixture.PairAsync("多块下载手机");

        // 3 个块：8 MiB + 8 MiB + 4 MiB + 余数，覆盖"完整块"与"末块"两种情况
        var size = (int)ProtocolConstants.ChunkSizeBytes * 2 + 4 * 1024 * 1024 + 12345;
        var source = Path.Combine(_fixture.RootDirectory, "multi-chunk-source.bin");
        await File.WriteAllBytesAsync(source, RandomBytes(size));

        var device = new StoredDevice
        {
            DeviceId = deviceId,
            DisplayName = "多块下载手机",
            Platform = "android",
            IsPaired = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var created = await _fixture.Server.Transfers.CreateDownloadAsync(device, source);
        Assert.True(created.Success, created.Error?.Message);
        var task = created.Value!;
        Assert.True(task.ChunkCount >= 3, $"chunkCount={task.ChunkCount}");

        for (var index = 0; index < task.ChunkCount; index++)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ProtocolConstants.TransferTasksPath}/{task.TaskId}/chunks/{index}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var declared = response.Content.Headers.ContentLength;
            Assert.NotNull(declared);

            var body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(declared!.Value, body.Length);
        }
    }

    [Fact]
    public async Task Download_ChunksCarryMetadataAndReassemble()
    {
        var (deviceId, token) = await _fixture.PairAsync("下载手机");
        var content = RandomBytes(3 * 1024 * 1024 + 17);
        var source = Path.Combine(_fixture.RootDirectory, "download-source.bin");
        await File.WriteAllBytesAsync(source, content);

        var device = new StoredDevice
        {
            DeviceId = deviceId,
            DisplayName = "下载手机",
            Platform = "android",
            IsPaired = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var created = await _fixture.Server.Transfers.CreateDownloadAsync(device, source);
        Assert.True(created.Success, created.Error?.Message);
        var task = created.Value!;

        await using var assembled = new MemoryStream();

        for (var index = 0; index < task.ChunkCount; index++)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ProtocolConstants.TransferTasksPath}/{task.TaskId}/chunks/{index}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(task.Sha256, response.Headers.GetValues("X-WC-Sha256").Single());
            Assert.Equal(content.Length, long.Parse(response.Headers.GetValues("X-WC-Total-Bytes").Single()));

            var buffer = new MemoryStream();
            await response.Content.CopyToAsync(buffer);
            assembled.Write(buffer.ToArray());

            // 分块哈希随响应下发，落盘自检后逐块回执
            var chunkSha = response.Headers.GetValues(ProtocolConstants.ChunkSha256Header).Single();
            Assert.Equal(Sha256(buffer.ToArray()), chunkSha);

            var chunkAck = await SendJsonAsync(
                HttpMethod.Post,
                $"{ProtocolConstants.TransferTasksPath}/{task.TaskId}/chunks/{index}/ack",
                token,
                WcJson.Serialize(new DownloadChunkAckRequestDto { Sha256 = chunkSha }));
            Assert.Equal(HttpStatusCode.OK, chunkAck.Status);
        }

        var received = assembled.ToArray();
        Assert.Equal(content.Length, received.Length);
        Assert.Equal(Sha256(content), Sha256(received));

        var ack = await SendJsonAsync(
            HttpMethod.Post,
            $"{ProtocolConstants.TransferTasksPath}/{task.TaskId}/ack",
            token,
            WcJson.Serialize(new TransferCompleteRequestDto { Sha256 = Sha256(received) }));

        Assert.Equal(HttpStatusCode.OK, ack.Status);

        var status = await GetTaskAsync(token, task.TaskId);
        Assert.Equal(TransferStates.Completed, status.State);
    }

    [Fact]
    public async Task Download_MultiChunk_ReassemblesWithoutOverflow()
    {
        // 真机暴露过的问题：单块文件看不出边界错误，多块时服务端把整份文件剩余内容
        // 都写进一块，触发 "Response Content-Length mismatch: too many bytes written"。
        // 这个用例用跨 3 块的文件覆盖真实路径。
        var (deviceId, token) = await _fixture.PairAsync("大文件手机");
        var chunkSize = ProtocolConstants.ChunkSizeBytes;
        var content = RandomBytes(chunkSize * 2 + 4321);
        var source = Path.Combine(_fixture.RootDirectory, "multi-chunk-source.bin");
        await File.WriteAllBytesAsync(source, content);

        var device = new StoredDevice
        {
            DeviceId = deviceId,
            DisplayName = "大文件手机",
            Platform = "android",
            IsPaired = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var task = (await _fixture.Server.Transfers.CreateDownloadAsync(device, source)).Value!;
        Assert.Equal(3, task.ChunkCount);

        await using var assembled = new MemoryStream();

        for (var index = 0; index < task.ChunkCount; index++)
        {
            var expected = Math.Min(chunkSize, content.Length - (long)index * chunkSize);
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ProtocolConstants.TransferTasksPath}/{task.TaskId}/chunks/{index}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected, response.Content.Headers.ContentLength);
            Assert.Equal(index.ToString(), response.Headers.GetValues("X-WC-Chunk-Index").Single());

            await response.Content.CopyToAsync(assembled);
        }

        Assert.Equal(content.Length, assembled.Length);
        Assert.Equal(Sha256(content), Sha256(assembled.ToArray()));
    }

    [Fact]
    public async Task Upload_InvalidFileName_Returns400()
    {
        var (_, token) = await _fixture.PairAsync("非法名手机");

        var result = await CreateUploadRawAsync(token,
            WcJson.Serialize(new TransferStartRequestDto { FileName = "../escape.txt", TotalBytes = 10 }));

        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal(ErrorCodes.InvalidFileName, result.ErrorCode);
    }

    [Fact]
    public async Task Upload_PathTraversalDirectory_Returns403()
    {
        var (_, token) = await _fixture.PairAsync("穿越手机");

        var result = await CreateUploadRawAsync(token,
            WcJson.Serialize(new TransferStartRequestDto
            {
                FileName = "ok.txt",
                RelativeDirectory = "../../Windows",
                TotalBytes = 10,
            }));

        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Equal(ErrorCodes.PathForbidden, result.ErrorCode);
    }

    // ------------------------------------------------------------ 辅助

    private sealed record UploadStart(HttpStatusCode Status, TransferTaskDto? Task, string? ErrorCode);

    private sealed record Completion(HttpStatusCode Status, TransferCompleteResponseDto? Response, string? ErrorCode);

    private sealed record SimpleResult(HttpStatusCode Status, string? ErrorCode, string? Details);

    private async Task<UploadStart> CreateUploadAsync(
        string token,
        string fileName,
        long totalBytes,
        int? requestedChunkSize = null)
    {
        var body = WcJson.Serialize(new TransferStartRequestDto
        {
            FileName = fileName,
            TotalBytes = totalBytes,
            RequestedChunkSize = requestedChunkSize,
        });

        var (status, json) = await SendJsonRawAsync(HttpMethod.Post, ProtocolConstants.TransferUploadsPath, token, body);
        var parsed = WcJson.DeserializeRequired<ApiResult<TransferTaskDto>>(json);

        return new UploadStart(status, parsed.Data, parsed.Error?.Code);
    }

    private async Task<UploadStart> CreateUploadRawAsync(string token, string body)
    {
        var (status, json) = await SendJsonRawAsync(HttpMethod.Post, ProtocolConstants.TransferUploadsPath, token, body);
        var parsed = WcJson.DeserializeRequired<ApiResult<TransferTaskDto>>(json);

        return new UploadStart(status, parsed.Data, parsed.Error?.Code);
    }

    private async Task PutAllChunksAsync(string token, TransferTaskDto task, byte[] content)
    {
        var offset = 0;
        for (var index = 0; index < task.ChunkCount; index++)
        {
            var length = (int)Math.Min(task.ChunkSize, content.Length - offset);
            var payload = new byte[length];
            Array.Copy(content, offset, payload, 0, length);

            var result = await PutChunkAsync(token, task.TaskId, index, payload);
            Assert.Equal(HttpStatusCode.OK, result.Status);

            offset += length;
        }
    }

    private async Task<SimpleResult> PutChunkAsync(string token, string taskId, int index, byte[] payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{ProtocolConstants.TransferTasksPath}/{taskId}/chunks/{index}")
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation(ProtocolConstants.ChunkSha256Header, Sha256(payload));

        using var response = await _fixture.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var parsed = WcJson.DeserializeRequired<ApiResult<ChunkAckDto>>(json);

        return new SimpleResult(response.StatusCode, parsed.Error?.Code, parsed.Error?.Details);
    }

    private async Task<Completion> CompleteAsync(string token, string taskId, string sha)
    {
        var body = WcJson.Serialize(new TransferCompleteRequestDto { Sha256 = sha });
        var (status, json) = await SendJsonRawAsync(
            HttpMethod.Post,
            $"{ProtocolConstants.TransferTasksPath}/{taskId}/complete",
            token,
            body);

        var parsed = WcJson.DeserializeRequired<ApiResult<TransferCompleteResponseDto>>(json);
        return new Completion(status, parsed.Data, parsed.Error?.Code);
    }

    private async Task<TransferTaskDto> GetTaskAsync(string token, string taskId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{ProtocolConstants.TransferTasksPath}/{taskId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _fixture.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        return WcJson.DeserializeRequired<ApiResult<TransferTaskDto>>(json).Data!;
    }

    private async Task<SimpleResult> SendAsync(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _fixture.Client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var parsed = WcJson.DeserializeRequired<ApiResult<TransferTaskDto>>(json);
        return new SimpleResult(response.StatusCode, parsed.Error?.Code, parsed.Error?.Details);
    }

    private async Task<SimpleResult> SendJsonAsync(HttpMethod method, string path, string token, string body)
    {
        var (status, json) = await SendJsonRawAsync(method, path, token, body);
        var parsed = WcJson.DeserializeRequired<ApiResult<TransferCompleteResponseDto>>(json);
        return new SimpleResult(status, parsed.Error?.Code, parsed.Error?.Details);
    }

    private async Task<(HttpStatusCode Status, string Json)> SendJsonRawAsync(
        HttpMethod method,
        string path,
        string token,
        string body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _fixture.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static byte[] RandomBytes(int size)
    {
        var buffer = new byte[size];
        Random.Shared.NextBytes(buffer);
        return buffer;
    }

    private static string Sha256(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
