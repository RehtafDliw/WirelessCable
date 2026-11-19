using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serilog;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using WirelessCable.Server.Http;
using WirelessCable.Server.Middleware;
using WirelessCable.Server.Transfer;
using WirelessCable.Storage.Entities;

namespace WirelessCable.Server.Endpoints;

/// <summary>
/// 文件传输端点。所有路径都在 DeviceAuthMiddleware 的保护前缀内，进入这里必然已认证；
/// 每个处理函数仍然显式取一次设备，避免将来路由调整时把鉴权漏掉。
/// </summary>
public static class TransferEndpoints
{
    private const string BinaryContentType = "application/octet-stream";

    public static void MapTransferEndpoints(this WebApplication app)
    {
        app.MapPost(ProtocolConstants.TransferUploadsPath, CreateUploadAsync);
        app.MapGet(ProtocolConstants.TransferTasksPath, ListAsync);
        app.MapGet(ProtocolConstants.TransferTasksPath + "/{taskId}", GetAsync);
        app.MapGet(ProtocolConstants.TransferTasksPath + "/{taskId}/chunks", ListChunksAsync);
        app.MapPut(ProtocolConstants.TransferTasksPath + "/{taskId}/chunks/{index:int}", PutChunkAsync);
        app.MapGet(ProtocolConstants.TransferTasksPath + "/{taskId}/chunks/{index:int}", GetChunkAsync);
        app.MapPost(ProtocolConstants.TransferTasksPath + "/{taskId}/chunks/{index:int}/ack", AckChunkAsync);
        app.MapPost(ProtocolConstants.TransferTasksPath + "/{taskId}/complete", CompleteAsync);
        app.MapPost(ProtocolConstants.TransferTasksPath + "/{taskId}/ack", AcknowledgeAsync);
        app.MapPost(ProtocolConstants.TransferTasksPath + "/{taskId}/pause", PauseAsync);
        app.MapPost(ProtocolConstants.TransferTasksPath + "/{taskId}/cancel", CancelAsync);
        app.MapPost(ProtocolConstants.TransferTasksPath + "/{taskId}/restart", RestartAsync);
        app.MapPost(ProtocolConstants.TransferTasksPath + "/{taskId}/resume", ResumeAsync);
        app.MapPost(ProtocolConstants.TransferTasksPath + "/delete", DeleteRecordsAsync);

        // 整批动作：整文件夹在列表里是**一行**，行上的暂停/继续/取消/重试作用到整批
        app.MapPost(ProtocolConstants.TransferTasksPath + "/batch", BatchActionAsync);
    }

    private static async Task CreateUploadAsync(
        HttpContext context,
        TransferEngine engine,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<TransferStartRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400)
                .ConfigureAwait(false);
            return;
        }

        var result = await engine.CreateUploadAsync(device, request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status201Created;
        await ApiResponseWriter
            .WriteAsync(context, StatusCodes.Status201Created,
                ApiResult<TransferTaskDto>.Success(result.Value!, context.TraceIdentifier, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ListAsync(
        HttpContext context,
        TransferEngine engine,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        // 整文件夹下载会一次产生几百条任务，客户端要能把它们取全才能收成一行。
        // 以前这里写死 50：桌面端传 ?limit=1000 被忽略，一个 330 文件的批次只能看到 50 条。
        var requested = context.Request.Query["limit"].ToString();
        var limit = int.TryParse(requested, out var parsed) ? Math.Clamp(parsed, 1, 1000) : 50;

        var tasks = await engine.ListAsync(device, limit, cancellationToken).ConfigureAwait(false);
        await ApiResponseWriter.WriteSuccessAsync(context, tasks, cancellationToken).ConfigureAwait(false);
    }

    private static async Task GetAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var result = await engine.GetAsync(device, taskId, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PutChunkAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        int index,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var contentLength = context.Request.ContentLength;
        if (contentLength is null or <= 0)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "分块请求必须带 Content-Length。", 400)
                .ConfigureAwait(false);
            return;
        }

        var result = await engine
            .WriteChunkAsync(
                device,
                taskId,
                index,
                context.Request.Body,
                contentLength.Value,
                context.Request.Headers[ProtocolConstants.ChunkSha256Header].FirstOrDefault(),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task GetChunkAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        int index,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var result = await engine.ReadChunkAsync(device, taskId, index, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        var chunk = result.Value!;
        try
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = BinaryContentType;
            context.Response.ContentLength = chunk.Length;
            context.Response.Headers["X-WC-Task-Id"] = taskId;
            context.Response.Headers["X-WC-Chunk-Index"] = index.ToString();
            context.Response.Headers["X-WC-Total-Bytes"] = chunk.TotalBytes.ToString();
            context.Response.Headers[ProtocolConstants.ChunkSha256Header] = chunk.ChunkSha256;
            if (!string.IsNullOrEmpty(chunk.Sha256))
            {
                context.Response.Headers["X-WC-Sha256"] = chunk.Sha256;
            }

            // 显式定长拷贝，不用 CopyToAsync：
            // Content-Length 已经声明了 chunk.Length，一旦实际写出的字节数与之不符，
            // Kestrel 会抛 "Response Content-Length mismatch" 并中断整个响应。
            // 阶段 4 真机跑大文件时出现过一次（写出 8519680 而声明 8388608，多了 131072 字节），
            // 根因未复现，但"越界就中断响应"的代价太高——
            // 这里改成：最多写 chunk.Length 字节，多出来的只记日志不写。
            var written = await WriteChunkBodyAsync(
                chunk.Stream, context.Response.Body, chunk.Length, cancellationToken).ConfigureAwait(false);

            if (written != chunk.Length)
            {
                Log.Error(
                    "分块响应长度不符 task={TaskId} index={Index} 声明={Declared} 实写={Written}",
                    taskId, index, chunk.Length, written);
            }

            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 客户端中途断开：任务保持 transferring，由空闲回收兜底
            Log.Debug("下载分块被客户端中断 task={TaskId} index={Index}", taskId, index);
        }
        finally
        {
            chunk.Dispose();
        }
    }

    private static async Task CompleteAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<TransferCompleteRequestDto>(context, cancellationToken)
            .ConfigureAwait(false);

        var result = await engine
            .CompleteUploadAsync(device, taskId, request?.Sha256, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AcknowledgeAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<TransferCompleteRequestDto>(context, cancellationToken)
            .ConfigureAwait(false);

        var result = await engine
            .AcknowledgeDownloadAsync(device, taskId, request?.Sha256, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 一批任务上的同一个动作（暂停 / 继续 / 取消 / 重试）。
    ///
    /// 逐条调用引擎，但只要一次请求：手机侧 200 个文件逐个发请求时，
    /// 光往返就要十几秒，而且中途断网会只做了一半。
    /// 单条失败不影响其余——返回里给出成功/失败计数与第一条错误。
    /// </summary>
    private static async Task BatchActionAsync(
        HttpContext context,
        TransferEngine engine,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<TransferBatchActionRequestDto>(context, cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400)
                .ConfigureAwait(false);
            return;
        }

        if (request.TaskIds.Count == 0)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "没有指定任务。", 400)
                .ConfigureAwait(false);
            return;
        }

        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("pause" or "resume" or "cancel" or "restart"))
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "不认识的动作。", 400)
                .ConfigureAwait(false);
            return;
        }

        var result = new TransferBatchActionResultDto();

        foreach (var taskId in request.TaskIds.Distinct(StringComparer.Ordinal))
        {
            var one = action switch
            {
                "pause" => await engine.PauseAsync(device, taskId, cancellationToken).ConfigureAwait(false),
                "resume" => await engine.ResumeAsync(device, taskId, cancellationToken).ConfigureAwait(false),
                "cancel" => await engine.CancelAsync(device, taskId, cancellationToken).ConfigureAwait(false),
                _ => await engine.RestartDownloadAsync(device, taskId, cancellationToken).ConfigureAwait(false),
            };

            if (one.Success)
            {
                result.Ok++;
                if (one.Value is not null)
                {
                    result.Tasks.Add(one.Value);
                }
            }
            else
            {
                result.Failed++;
                result.FirstError ??= one.Error?.Message ?? one.Error?.Code;
            }
        }

        // 一条都没成功才算整体失败：批里本来就有已完成的任务时，
        // 暂停它们会失败，但用户要的是"剩下的都停下"，不该整单报错。
        if (result.Ok == 0)
        {
            await WriteErrorAsync(
                context,
                ErrorCodes.TransferStateInvalid,
                result.FirstError ?? "这一批任务都处理不了。",
                409).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>暂停进行中的任务。保留临时文件与分块位图，继续时从已有偏移接上。</summary>
    /// <summary>
    /// 删除传输记录。两端「清理列表」都走这里：可按需同时删掉手机传来的文件。
    /// </summary>
    private static async Task DeleteRecordsAsync(
        HttpContext context,
        TransferEngine engine,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<TransferDeleteRecordsRequestDto>(context, cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400)
                .ConfigureAwait(false);
            return;
        }

        var result = await engine
            .DeleteRecordsAsync(device, request.TaskIds, request.DeleteFiles, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PauseAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var result = await engine.PauseAsync(device, taskId, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CancelAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var result = await engine.CancelAsync(device, taskId, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重新排队一个中断的下载任务。只重置进度，不接受任何来自客户端的路径。</summary>
    private static async Task RestartAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var result = await engine.RestartDownloadAsync(device, taskId, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>任务的分块位图。续传时客户端据此只补未完成的分块。</summary>
    private static async Task ListChunksAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var result = await engine.ListChunksAsync(device, taskId, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>下载分块回执：客户端确认该块已落盘并通过本地校验。</summary>
    private static async Task AckChunkAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        int index,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<DownloadChunkAckRequestDto>(context, cancellationToken)
            .ConfigureAwait(false);

        var result = await engine
            .AcknowledgeChunkAsync(device, taskId, index, request?.Sha256, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>恢复一个中断的任务。幂等：重复调用返回同一份服务端状态。</summary>
    private static async Task ResumeAsync(
        HttpContext context,
        TransferEngine engine,
        string taskId,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var result = await engine.ResumeAsync(device, taskId, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 定长拷贝：最多写 <paramref name="length"/> 字节。
    ///
    /// 返回实际写出的字节数。源流若给出超出声明长度的数据，只丢弃不写——
    /// 多写会让 Kestrel 判定 Content-Length 不符并中断响应，客户端拿到的分块就是残缺的。
    /// </summary>
    private static async Task<long> WriteChunkBodyAsync(
        Stream source,
        Stream target,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long written = 0;

        try
        {
            while (written < length)
            {
                var want = (int)Math.Min(buffer.Length, length - written);
                var read = await source.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    // 源提前结束：少写，由 Kestrel 报告 Content-Length 不符
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return written;
    }

    private static async Task<StoredDevice?> RequireDeviceAsync(HttpContext context)    {
        var device = DeviceAuthMiddleware.GetDevice(context);
        if (device is null)
        {
            await WriteErrorAsync(context, ErrorCodes.Unauthorized, "需要有效的设备凭据。", 401).ConfigureAwait(false);
        }

        return device;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return WcJson.Deserialize<T>(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static Task WriteFailureAsync(HttpContext context, TransferFailure failure)
        => WriteErrorAsync(context, failure.Code, failure.Message, failure.StatusCode, failure.Retryable, failure.Details);

    private static Task WriteErrorAsync(
        HttpContext context,
        string code,
        string message,
        int statusCode,
        bool retryable = false,
        string? details = null)
        => ApiResponseWriter.WriteErrorAsync(context, statusCode, code, message, retryable, details);
}
