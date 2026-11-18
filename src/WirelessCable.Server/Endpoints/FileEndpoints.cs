using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using WirelessCable.Server.Files;
using WirelessCable.Server.Http;
using WirelessCable.Server.Middleware;
using WirelessCable.Server.Transfer;
using WirelessCable.Storage.Entities;

namespace WirelessCable.Server.Endpoints;

/// <summary>
/// 文件管理端点。手机浏览/管理电脑共享目录，以及手机上报授权目录索引、取作业、回报作业结果。
///
/// 全部落在 <c>/api/v1/fs</c> 前缀内，继承 <see cref="DeviceAuthMiddleware"/> 的令牌保护。
/// 每个处理函数仍显式取一次设备——路由将来调整也不至于把鉴权漏掉。
/// </summary>
public static class FileEndpoints
{
    public static void MapFileEndpoints(this WebApplication app)
    {
        var root = ProtocolConstants.FileSystemPath;

        // 电脑共享目录（手机侧视角）
        app.MapGet(root + "/roots", ListRootsAsync);
        app.MapGet(root + "/entries", ListEntriesAsync);
        app.MapGet(root + "/info", GetInfoAsync);
        app.MapPost(root + "/mkdir", CreateDirectoryAsync);
        app.MapPost(root + "/rename", RenameAsync);
        app.MapPost(root + "/delete", DeleteAsync);
        app.MapPost(root + "/move", MoveAsync);
        app.MapPost(root + "/copy", CopyAsync);
        app.MapPost(root + "/batch-delete", BatchDeleteAsync);
        app.MapPost(root + "/jobs", CreateJobAsync);
        app.MapGet(root + "/jobs", ListJobsAsync);

        // 手机在浏览器里点文件 → 走阶段 3/4 已验证的分块通道下载
        app.MapPost(root + "/download", DownloadAsync);

        // 整个文件夹：服务端展开成一批任务（同一个 batchId），手机一次性收
        app.MapPost(root + "/download-folder", DownloadFolderAsync);

        // 手机授权目录（手机侧视角）
        app.MapPost(ProtocolConstants.FileDeviceIndexPath, PublishIndexAsync);
        app.MapPost(ProtocolConstants.FileDeviceIndexSyncPath, SyncIndexesAsync);
        app.MapGet(ProtocolConstants.FileDeviceJobsPath, TakeJobsAsync);
        app.MapPost(ProtocolConstants.FileDeviceJobsPath + "/{jobId}/result", CompleteJobAsync);
    }

    private static async Task ListRootsAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var roots = await service.ListRootsAsync(device, cancellationToken).ConfigureAwait(false);
        await ApiResponseWriter.WriteSuccessAsync(context, roots, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ListEntriesAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var rootId = context.Request.Query["rootId"].ToString();
        var path = context.Request.Query["path"].ToString();

        var result = await service.ListAsync(device, rootId, path, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task GetInfoAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var rootId = context.Request.Query["rootId"].ToString();
        var path = context.Request.Query["path"].ToString();

        var result = await service.GetInfoAsync(device, rootId, path, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateDirectoryAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<FileCreateDirectoryRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var result = await service.CreateDirectoryAsync(device, request, cancellationToken).ConfigureAwait(false);
        await WriteOperationResultAsync(context, result).ConfigureAwait(false);
    }

    private static async Task RenameAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<FileRenameRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var result = await service.RenameAsync(device, request, cancellationToken).ConfigureAwait(false);
        await WriteOperationResultAsync(context, result).ConfigureAwait(false);
    }

    private static async Task DeleteAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<FileDeleteRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var result = await service.DeleteAsync(device, request, cancellationToken).ConfigureAwait(false);
        await WriteOperationResultAsync(context, result).ConfigureAwait(false);
    }

    private static async Task MoveAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<FileMoveRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var result = await service.MoveAsync(device, request, cancellationToken).ConfigureAwait(false);
        await WriteOperationResultAsync(context, result).ConfigureAwait(false);
    }

    /// <summary>复制一个条目（文件流拷贝 / 目录递归）。源保持不动。</summary>
    private static async Task CopyAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<FileCopyRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var result = await service.CopyAsync(device, request, cancellationToken).ConfigureAwait(false);
        await WriteOperationResultAsync(context, result).ConfigureAwait(false);
    }

    private static async Task BatchDeleteAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<FileBatchRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var result = await service.DeleteBatchAsync(device, request, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateJobAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<FileJobRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var result = await service.EnqueueJobAsync(
            device,
            request.RootId,
            request.Operation,
            request.SourcePath,
            request.DestinationPath,
            request.DisplayName,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ListJobsAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var jobs = await service.ListJobsAsync(device, 50, cancellationToken).ConfigureAwait(false);
        await ApiResponseWriter.WriteSuccessAsync(context, jobs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PublishIndexAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var index = await ReadJsonAsync<DeviceFileIndexDto>(context, cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var result = await service.PublishIndexAsync(device, index, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只同步根的集合：手机撤销全部授权后，靠它把服务端的旧快照清掉。</summary>
    private static async Task SyncIndexesAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var sync = await ReadJsonAsync<DeviceFileSyncDto>(context, cancellationToken).ConfigureAwait(false);
        if (sync is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var result = await service.SyncIndexesAsync(device, sync, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await WriteFailureAsync(context, result.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TakeJobsAsync(
        HttpContext context,
        FileService service,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        // ?wait=毫秒：挂起式轮询。没有作业时把请求挂住，一有作业立刻返回，
        // 是"电脑点一下 → 手机开始做"这段延迟的主要来源。
        var waitMs = 0;
        if (int.TryParse(context.Request.Query["wait"], out var requested))
        {
            waitMs = Math.Clamp(requested, 0, 5_000);
        }

        var jobs = await service
            .TakePendingJobsAsync(device, cancellationToken, TimeSpan.FromMilliseconds(waitMs))
            .ConfigureAwait(false);
        await ApiResponseWriter.WriteSuccessAsync(context, jobs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CompleteJobAsync(
        HttpContext context,
        FileService service,
        string jobId,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var result = await ReadJsonAsync<FileJobResultDto>(context, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        // 路径里的 jobId 是权威，body 里的不允许覆盖
        result.JobId = jobId;

        var completed = await service.CompleteJobAsync(device, result, cancellationToken).ConfigureAwait(false);
        if (!completed.Success)
        {
            await WriteFailureAsync(context, completed.Error!).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, completed.Value!, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadAsync(
        HttpContext context,
        FileService service,
        TransferEngine engine,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<FileDownloadRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        // 路径解析与权限校验都在服务端：客户端只给"根 id + 相对路径"
        var resolved = await service
            .ResolveFilePathAsync(device, request.RootId ?? string.Empty, request.Path ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (!resolved.Success)
        {
            await WriteFailureAsync(context, resolved.Error!).ConfigureAwait(false);
            return;
        }

        var created = await engine
            // 手机浏览共享目录时点的下载：落点由手机自己选（SAF），
            // 不能进自动接收队列——那样文件会先跑到收件目录去
            .CreateDownloadAsync(device, resolved.Value!, TransferPurposes.Directory, cancellationToken)
            .ConfigureAwait(false);

        if (!created.Success)
        {
            await WriteErrorAsync(
                context,
                created.Error!.Code,
                created.Error.Message,
                created.Error.StatusCode).ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, created.Value!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一次"下载整个文件夹"最多展开多少个文件。超过的部分不去枚举，直接告诉客户端。</summary>
    private const int MaxFolderBatchFiles = 2000;

    /// <summary>递归深度上限。共享目录里偶尔会有符号链接套出来的环，靠它兜底。</summary>
    private const int MaxFolderDepth = 12;

    /// <summary>
    /// 整文件夹下载：把目录递归展开成一批下载任务。
    ///
    /// 为什么在服务端展开而不是手机一个个要：手机端每文件一次 HTTP 太慢，
    /// 而且只有服务端才知道目录里到底有什么（手机只能一层层列目录）。
    /// 展开后这批任务共用一个 `batchId`，双端列表据此把它们收成一行。
    /// </summary>
    private static async Task DownloadFolderAsync(
        HttpContext context,
        FileService service,
        TransferEngine engine,
        CancellationToken cancellationToken)
    {
        var device = await RequireDeviceAsync(context).ConfigureAwait(false);
        if (device is null)
        {
            return;
        }

        var request = await ReadJsonAsync<FileDownloadRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "请求体不是合法的 JSON。", 400).ConfigureAwait(false);
            return;
        }

        var resolved = await service
            .ResolveFilePathAsync(
                device,
                request.RootId ?? string.Empty,
                request.Path ?? string.Empty,
                cancellationToken,
                allowDirectory: true)
            .ConfigureAwait(false);

        if (!resolved.Success)
        {
            await WriteFailureAsync(context, resolved.Error!).ConfigureAwait(false);
            return;
        }

        var folder = resolved.Value!;
        if (!Directory.Exists(folder))
        {
            await WriteErrorAsync(context, ErrorCodes.InvalidRequest, "这个路径不是文件夹。", 400).ConfigureAwait(false);
            return;
        }

        var batchName = new DirectoryInfo(folder).Name;
        if (string.IsNullOrWhiteSpace(batchName))
        {
            batchName = folder;
        }

        List<(string Full, string Relative)> files;
        int skipped;
        try
        {
            files = [];
            skipped = 0;
            CollectFolderFiles(folder, batchName, files, 0, ref skipped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await WriteErrorAsync(context, ErrorCodes.FileInaccessible, "读取文件夹失败。", 403).ConfigureAwait(false);
            return;
        }

        if (files.Count == 0)
        {
            await ApiResponseWriter.WriteSuccessAsync(
                context,
                new { batchId = (string?)null, batchName, created = 0, failed = 0, skipped },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var batchId = Guid.NewGuid().ToString("N");
        var created = 0;
        var failed = 0;
        string? firstError = null;
        // 把建好的任务一并回给手机：手机不用再查一次列表，也就不会出现
        // "刚建完列表还没刷新到 → 少收一个文件"的竞态
        var tasks = new List<TransferTaskDto>();

        foreach (var (full, relative) in files)
        {
            var result = await engine
                .CreateDownloadAsync(
                    device,
                    full,
                    TransferPurposes.Directory,
                    cancellationToken,
                    batchId,
                    batchName,
                    relative)
                .ConfigureAwait(false);

            if (result.Success)
            {
                created++;
                if (result.Value is not null)
                {
                    tasks.Add(result.Value);
                }
            }
            else
            {
                failed++;
                firstError ??= $"{Path.GetFileName(full)}：{result.Error?.Message ?? result.Error?.Code}";
            }
        }

        await ApiResponseWriter.WriteSuccessAsync(
            context,
            new { batchId, batchName, created, failed, skipped, error = firstError, tasks },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>递归收集文件，跳过符号链接/联接点，带深度与数量上限。</summary>
    private static void CollectFolderFiles(
        string directory,
        string relativePrefix,
        List<(string Full, string Relative)> into,
        int depth,
        ref int skipped)
    {
        if (depth > MaxFolderDepth)
        {
            skipped++;
            return;
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped++;
            return;
        }

        foreach (var entry in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                skipped++;
                continue;
            }

            var name = Path.GetFileName(entry);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                CollectFolderFiles(entry, relativePrefix + "/" + name, into, depth + 1, ref skipped);
                continue;
            }

            if (into.Count >= MaxFolderBatchFiles)
            {
                skipped++;
                continue;
            }

            into.Add((entry, relativePrefix + "/" + name));
        }
    }

    // ------------------------------------------------------------ 辅助

    private static async Task<StoredDevice?> RequireDeviceAsync(HttpContext context)
    {
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

    private static Task WriteOperationResultAsync(HttpContext context, FileServiceResult<bool> result)
        => result.Success
            ? ApiResponseWriter.WriteSuccessAsync(context, true)
            : WriteFailureAsync(context, result.Error!);

    private static Task WriteFailureAsync(HttpContext context, FileServiceFailure failure)
        => WriteErrorAsync(context, failure.Code, failure.Message, failure.StatusCode, failure.Retryable, failure.ReasonCode);

    private static Task WriteErrorAsync(
        HttpContext context,
        string code,
        string message,
        int statusCode,
        bool retryable = false,
        string? details = null)
        => ApiResponseWriter.WriteErrorAsync(context, statusCode, code, message, retryable, details);
}
