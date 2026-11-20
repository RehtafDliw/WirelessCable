using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WirelessCable.Core.Domain;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using WirelessCable.Server.Diagnostics;
using WirelessCable.Server.Files;
using WirelessCable.Server.Pairing;
using WirelessCable.Server.Runtime;
using WirelessCable.Server.Transfer;
using WirelessCable.Storage;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;

namespace WirelessCable.Server.WebUi;

/// <summary>
/// 新版界面（WebView2 里的 React 应用）用的本机接口。
///
/// 与 <c>/api/v1</c> 的区别只有一个：身份。设备接口靠设备令牌证明"我是已配对手机"，
/// 浏览器里没有那套凭据；这里靠"loopback + 一次性本机令牌"（<see cref="LocalUiSession"/>），
/// 由 <c>WirelessCableServer</c> 里的中间件统一校验，所以下面每个端点都不再自己鉴权。
///
/// 所有数据都直读仓储/引擎，不做缓存 —— 界面刷新一次就该看到库里的现状。
/// </summary>
public static class LocalUiEndpoints
{
    /// <summary>在线判定窗口：与界面其它地方一致（30 秒没心跳就算掉线）。</summary>
    private static readonly TimeSpan OnlineWindow =
        TimeSpan.FromSeconds(ProtocolConstants.DeviceOfflineAfterSeconds);

    public static void MapLocalUiApi(this WebApplication app)
    {
        var api = app.MapGroup("/app/api");

        // ---------------- 自检 ----------------
        api.MapGet("/ping", (HttpContext context) =>
        {
            var session = context.RequestServices.GetRequiredService<LocalUiSession>();
            return Results.Json(new
            {
                ok = true,
                loopback = LocalUiSession.IsLoopback(context),
                token = LocalUiSession.IsLoopback(context) ? session.Token : null,
                version = AppVersionInfo.Current,
            });
        });

        // ---------------- 运行状态 ----------------
        api.MapGet("/status", (HttpContext context) =>
        {
            var runtime = context.RequestServices.GetRequiredService<ServerRuntime>();
            var options = context.RequestServices.GetRequiredService<ServerOptions>();
            var transfers = context.RequestServices.GetRequiredService<TransferEngine>();
            var snapshot = runtime.Snapshot();

            return Results.Json(new
            {
                version = snapshot.AppVersion,
                state = snapshot.State.ToString(),
                port = snapshot.Port,
                addresses = snapshot.Addresses,
                fingerprint = snapshot.Fingerprint,
                machineName = options.DisplayName ?? Environment.MachineName,
                startedAtUnixMs = snapshot.StartedAtUtc?.ToUnixTimeMilliseconds() ?? 0,
                uptimeSeconds = snapshot.UptimeSeconds,
                protocolVersion = snapshot.ProtocolVersion,
                databaseReady = snapshot.DatabaseReady,
                lastError = snapshot.LastError,
                // mDNS 是"手机换网络后自动找回电脑"的前提：它没在跑，重连发现就是空转。
                // 之前这个状态只在服务对象内部，界面上完全看不到。
                mdnsRunning = context.RequestServices.GetRequiredService<WirelessCableServer>().MdnsRunning,
                mdnsError = context.RequestServices.GetRequiredService<WirelessCableServer>().MdnsError,
                // 手机传过来的文件落到这里；界面上的"打开接收目录"用的是同一个值
                receivedDirectory = transfers.SharedDirectory,
            });
        });

        // ---------------- 诊断 ----------------
        //
        // snapshot 是给回归脚本（scripts/smoke-test.py）和诊断包共用的只读快照：
        // 它只读库、不写任何东西，所以可以随时拉。
        // 手机版安装包：手机浏览器访问这台电脑就能下载安装，
        // 不用把 APK 传来传去（本机界面里有个「下载手机版」链接指向它）。
        // 挂在根路径而不是 /app/api：那个组要本机令牌，手机浏览器拿不到。
        // APK 本身不含凭据，局域网内可下载是可接受的。
        app.MapGet("/download/android", (HttpContext context) =>
        {
            var runtime = context.RequestServices.GetRequiredService<ServerRuntime>();
            var version = runtime.Snapshot().AppVersion;
            var baseDir = AppContext.BaseDirectory;

            var candidates = new[]
            {
                Path.Combine(baseDir, "WirelessCable.apk"),
                Path.Combine(baseDir, "..", "dist", $"WirelessCable-{version}.apk"),
            };

            foreach (var path in candidates)
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full))
                {
                    return Results.File(
                        full,
                        "application/vnd.android.package-archive",
                        "WirelessCable.apk");
                }
            }

            return Results.NotFound(new { code = "apk_not_found", message = "本机没有找到手机版安装包。" });
        });

        api.MapGet("/diagnostics/snapshot", async (HttpContext context) =>
        {
            var diagnostics = context.RequestServices.GetRequiredService<DiagnosticsService>();
            var snapshot = await diagnostics.SnapshotAsync(context.RequestAborted).ConfigureAwait(false);
            return Results.Json(snapshot);
        });

        // 诊断包：浏览器下载一个 zip。
        // includeNames 默认 false —— 默认脱敏，用户要排查落点再显式打开。
        api.MapGet("/diagnostics/bundle", async (HttpContext context, bool? includeNames) =>
        {
            var diagnostics = context.RequestServices.GetRequiredService<DiagnosticsService>();
            var bytes = await diagnostics
                .BuildBundleAsync(includeNames == true, context.RequestAborted)
                .ConfigureAwait(false);

            return Results.File(
                bytes,
                "application/zip",
                $"wirelesscable-diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        });

        // ---------------- 设备 ----------------
        api.MapGet("/devices", async (HttpContext context) =>
        {
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var pairing = context.RequestServices.GetRequiredService<PairingService>();
            var roots = context.RequestServices.GetRequiredService<SharedDirectoryRepository>();
            var cancellationToken = context.RequestAborted;

            var rows = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;

            var paired = rows.Select(device => new
            {
                deviceId = device.DeviceId,
                name = device.DisplayName,
                platform = device.Platform,
                online = IsOnline(device.LastSeenUtc, now),
                lastSeenUnixMs = device.LastSeenUtc?.ToUnixTimeMilliseconds() ?? 0,
                pairedAtUnixMs = device.PairedAtUtc?.ToUnixTimeMilliseconds() ?? 0,
                machineAccess = device.MachineAccess,
                sessionPaused = device.SessionPaused,
                revoked = device.IsRevoked,
            }).ToArray();

            var pending = pairing.GetSessions()
                .Where(s => string.Equals(s.State, PairingStates.Pending, StringComparison.Ordinal))
                .Select(s => new
                {
                    requestId = s.RequestId,
                    deviceName = s.DeviceName,
                    platform = s.Platform,
                    code = s.Code,
                    createdAtUnixMs = s.CreatedAtUnixMs,
                    expiresAtUnixMs = s.ExpiresAtUnixMs,
                })
                .ToArray();

            var grants = 0;
            foreach (var row in await roots.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                grants += (await roots.ListGrantsAsync(row.DirectoryId, cancellationToken).ConfigureAwait(false)).Count;
            }

            return Results.Json(new
            {
                paired,
                pending,
                stats = new
                {
                    online = paired.Count(d => d.online),
                    paired = paired.Length,
                    directories = (await roots.ListAsync(cancellationToken).ConfigureAwait(false)).Count,
                    grants,
                },
            });
        });

        api.MapPost("/devices/{deviceId}/session", async (
            string deviceId,
            LocalSessionRequest request,
            HttpContext context) =>
        {
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            if (await devices.GetAsync(deviceId, context.RequestAborted).ConfigureAwait(false) is null)
            {
                return Results.NotFound(new { code = "device_not_found" });
            }

            await devices.SetSessionPausedAsync(deviceId, request.Paused, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { ok = true, sessionPaused = request.Paused });
        });

        api.MapPost("/devices/{deviceId}/machine-access", async (
            string deviceId,
            LocalMachineAccessRequest request,
            HttpContext context) =>
        {
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            if (await devices.GetAsync(deviceId, context.RequestAborted).ConfigureAwait(false) is null)
            {
                return Results.NotFound(new { code = "device_not_found" });
            }

            var level = Math.Clamp(request.Level, MachineAccessLevels.None, MachineAccessLevels.ReadWriteDelete);
            await devices.SetMachineAccessAsync(deviceId, level, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { ok = true, machineAccess = level });
        });

        api.MapPost("/devices/{deviceId}/rename", async (
            string deviceId,
            LocalRenameRequest request,
            HttpContext context) =>
        {
            var name = (request.Name ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > 64)
            {
                return Results.BadRequest(new { code = "invalid_name" });
            }

            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            if (await devices.GetAsync(deviceId, context.RequestAborted).ConfigureAwait(false) is null)
            {
                return Results.NotFound(new { code = "device_not_found" });
            }

            await devices.SetDisplayNameAsync(deviceId, name, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { ok = true, name });
        });

        api.MapDelete("/devices/{deviceId}", async (HttpContext context, string deviceId) =>
        {
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var transfers = context.RequestServices.GetRequiredService<TransferEngine>();

            // 与桌面端「删除设备」同序：先清传输记录（按 device_id 关联，设备没了就找不回来），
            // 再删设备行；接收目录里的文件属于用户，一律保留。
            var purged = await transfers.PurgeDeviceRecordsAsync(deviceId, context.RequestAborted).ConfigureAwait(false);
            await devices.DeleteAsync(deviceId, context.RequestAborted).ConfigureAwait(false);

            return Results.Json(new { ok = true, purgedTransfers = purged });
        });

        // ---------------- 配对申请 ----------------
        // 电脑亮码：手机扫这张图就能配对（新版界面首页的「配对码」）。
        // 每次调用都重新生成一次性随机数。
        //
        // 热点是**可选项**：请求里 hotspot=true 才开。以前无条件开，结果是
        // 电脑靠 Wi-Fi 上网、网卡又不支持 STA+AP 并发时，一开热点电脑自己就断网了。
        // 手机和电脑在同一 Wi-Fi 时根本不需要热点，所以默认不开。
        api.MapPost("/pairing/qr", async (HttpContext context) =>
        {
            var server = context.RequestServices.GetRequiredService<WirelessCableServer>();
            var hotspotBridge = context.RequestServices.GetRequiredService<LocalHotspotBridge>();

            if (server.Port is null)
            {
                return Results.Json(new { ok = false, message = "服务未就绪" });
            }

            var request = await ReadOptionalBodyAsync<LocalPairingQrRequest>(context).ConfigureAwait(false);
            var wanted = request?.Hotspot ?? hotspotBridge.ReadPreference?.Invoke() ?? false;

            // 用户这次的选择记下来，下次出码沿用。只在请求里**显式**给了值时才写，
            // 免得"没传参数"把上次的选择冲成默认值。
            if (request?.Hotspot is { } explicitChoice)
            {
                hotspotBridge.WritePreference?.Invoke(explicitChoice);
            }

            QrHotspotDto? hotspot = null;
            string? hotspotText = null;
            string? hotspotError = null;
            // 这次真的把热点关了？是的话二维码要剔掉残留的热点网段地址
            var justStoppedHotspot = false;

            if (wanted && hotspotBridge.EnsureAsync is not null)
            {
                try
                {
                    var offer = await hotspotBridge.EnsureAsync(context.RequestAborted).ConfigureAwait(false);
                    if (offer is null)
                    {
                        hotspotError = hotspotBridge.UnavailableReason?.Invoke()
                            ?? "电脑热点不可用，手机可连同一 Wi-Fi";
                    }
                    else
                    {
                        hotspotText = $"备用热点 {offer.Ssid} · 密码 {offer.Passphrase}";

                        // 地址还没就绪时不写进二维码：手机连上热点却不知道连谁
                        if (!string.IsNullOrWhiteSpace(offer.Address))
                        {
                            hotspot = new QrHotspotDto
                            {
                                Ssid = offer.Ssid,
                                Passphrase = offer.Passphrase,
                                Address = offer.Address!,
                            };
                        }
                        else
                        {
                            // 热点开着但网卡还在等 ICS 配地址（或系统没配成功）：
                            // 这张码里没有 hotspot 字段，手机不会去连热点。
                            // 必须明说，否则用户以为勾了就有兜底。
                            hotspotError = "热点地址还没就绪，请点「重新生成」";
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 出码不能被热点拖死：拿不到热点就把原因写出来
                    Serilog.Log.Warning(ex, "生成配对二维码时开启热点失败");
                    hotspotError = "电脑热点开启失败，手机可连同一 Wi-Fi";
                }
            }
            else if (!wanted)
            {
                // 取消勾选就把我们开的热点关掉，否则它会一直广播到退出应用。
                // 只关"应用开的"：用户在系统设置里自己开的不动。
                try
                {
                    if (hotspotBridge.StopAsync is not null)
                    {
                        justStoppedHotspot = await hotspotBridge.StopAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "关闭热点失败（不影响出码）");
                }
            }

            var qr = server.CreatePairingQr(hotspot, justStoppedHotspot);
            if (qr is null)
            {
                return Results.Json(new { ok = false, message = "服务未就绪" });
            }

            return Results.Json(new
            {
                ok = true,
                pngBase64 = Convert.ToBase64String(qr.PngBytes),
                text = qr.Text,
                expiresAtUnixMs = qr.ExpiresAtUnixMs,
                hotspot = hotspotText,
                // 拆开给：界面要按语言拼文案，整句只适合直接打印
                hotspotSsid = hotspot?.Ssid,
                hotspotPassphrase = hotspot?.Passphrase,
                hotspotAddress = hotspot?.Address,
                hotspotError,
                hotspotPreferred = wanted,
            });
        });

        api.MapPost("/pairing/{requestId}/{decision}", (string requestId, string decision, HttpContext context) =>
        {
            var pairing = context.RequestServices.GetRequiredService<PairingService>();

            var approve = string.Equals(decision, "approve", StringComparison.OrdinalIgnoreCase);
            var ok = approve
                ? pairing.TryApprove(requestId, out var error)
                : pairing.TryReject(requestId, out error);

            return ok
                ? Results.Json(new { ok = true })
                : Results.BadRequest(new { code = "pairing_failed", message = error });
        });

        // ---------------- 传输 ----------------
        api.MapGet("/transfers", async (HttpContext context, int? limit) =>
        {
            var transfers = context.RequestServices.GetRequiredService<TransferEngine>();
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var fileJobs = context.RequestServices.GetRequiredService<DeviceFileRepository>();
            var cancellationToken = context.RequestAborted;

            var names = (await devices.ListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(d => d.DeviceId, d => d.DisplayName, StringComparer.Ordinal);

            // 队列里还没执行的作业（整文件夹是逐个作业下发的，暂停时这些文件还没有传输任务）。
            // 界面靠它判断"这一批到底结束没有"：只看任务列表的话，暂停后几秒就被判成已完成。
            var openJobs = await fileJobs.CountOpenJobsByDirectoryAsync(cancellationToken)
                .ConfigureAwait(false);

            // 上限必须与手机端 /transfers 一致（1000），否则前端按目录分组的批次会被拦腰截断：
            // 一次 330 个文件的整夹下载，200 条只能看到前 200 个，行上显示的"已完成 N/M"就永远对不上。
            var items = await transfers
                .ListRecentAsync(Math.Clamp(limit ?? 100, 1, 1000), names, cancellationToken)
                .ConfigureAwait(false);

            return Results.Json(items.Select(dto =>
            {
                // 这个落点目录下还有多少作业没跑完（pending/paused/running），其中多少是暂停的
                var queue = QueueFor(dto.RelativePath, openJobs);
                return new
                {
                    taskId = dto.TaskId,
                    fileName = dto.FileName,
                    direction = dto.Direction,
                    purpose = dto.Purpose,
                    deviceName = dto.DeviceName,
                    totalBytes = dto.TotalBytes,
                    transferredBytes = dto.TransferredBytes,
                    state = dto.State,
                    errorMessage = dto.ErrorMessage,
                    localPath = dto.LocalPath,
                    // 落点相对路径：整文件夹下载时它是判断"这些任务属于哪个目录"的唯一依据
                    // （服务端不给 batchId，前端只能按路径第一段兜底分组）。
                    // 少了它，194 个文件就会在列表里铺 194 行。
                    relativePath = dto.RelativePath,
                    // 整文件夹传输的批次：界面据此把 N 个任务收成一行
                    batchId = dto.BatchId,
                    batchName = dto.BatchName,
                    queuedJobs = queue.Open,
                    pausedJobs = queue.Paused,
                    createdAtUnixMs = dto.CreatedAtUnixMs,
                    updatedAtUnixMs = dto.UpdatedAtUnixMs,
                };
            }));
        });

        api.MapPost("/transfers/{taskId}/{action}", async (
            string taskId,
            string action,
            HttpContext context) =>
        {
            var transfers = context.RequestServices.GetRequiredService<TransferEngine>();
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var cancellationToken = context.RequestAborted;

            var record = await FindTaskDeviceAsync(transfers, devices, taskId, cancellationToken)
                .ConfigureAwait(false);
            if (record is null)
            {
                return Results.NotFound(new { code = "transfer_not_found" });
            }

            var (device, direction) = record.Value;

            // 只有下载能"重新开始"：上传的源在手机上，电脑单方面重来没有意义，
            // 手机端会把中断的上传自己续上。
            TransferResult<TransferTaskDto> result;
            switch (action.ToLowerInvariant())
            {
                case "pause":
                    result = await transfers.PauseAsync(device, taskId, cancellationToken).ConfigureAwait(false);
                    break;
                case "cancel":
                    result = await transfers.CancelAsync(device, taskId, cancellationToken).ConfigureAwait(false);
                    break;
                case "resume":
                    result = await transfers.ResumeAsync(device, taskId, cancellationToken).ConfigureAwait(false);
                    break;
                case "restart" when string.Equals(direction, TransferDirections.Download, StringComparison.Ordinal):
                    result = await transfers.RestartDownloadAsync(device, taskId, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    return Results.BadRequest(new { code = "unsupported_action", action });
            }

            return result.Success
                ? Results.Json(new { ok = true, state = result.Value?.State })
                : Results.BadRequest(new { code = result.Error?.Code, message = result.Error?.Message });
        });

        // 整批动作：整文件夹在列表里是一行，行上的暂停/继续/取消/重试作用到整批。
        // 逐个 task 发请求时，200 个文件就是 200 次本机 HTTP 往返。
        api.MapPost("/transfers/batch", async (HttpContext context, LocalBatchActionRequest request) =>
        {
            var transfers = context.RequestServices.GetRequiredService<TransferEngine>();
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var files = context.RequestServices.GetRequiredService<FileService>();
            var cancellationToken = context.RequestAborted;

            var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (action is not ("pause" or "resume" or "cancel" or "restart"))
            {
                return Results.BadRequest(new { code = "unsupported_action", action });
            }

            var ids = (request.TaskIds ?? []).Distinct(StringComparer.Ordinal).ToList();
            // 只清队列时没有 taskId（整批任务都被删了就取不到 id），
            // 有落点目录就够定位要作废的作业
            if (ids.Count == 0 && string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                return Results.BadRequest(new { code = "invalid_request", message = "没有指定任务。" });
            }

            var ok = 0;
            var failed = 0;
            // 整批里绝大多数文件早就传完了，暂停/恢复对它们本来就没意义。
            // 把它们算成失败，用户会看到一堆"只有进行中的任务可以暂停"。
            var skipped = 0;
            string? firstError = null;
            string? firstErrorCode = null;
            // 恢复时发现任务已被另一端取消（对面删记录会先暂停再取消）。
            // 端点把它带回界面：那里要弹"传输已被接收方取消，是否删除记录"。
            var cancelledTasks = new List<string>();
            // 整批的动作要能延伸到这个目录还没执行的作业上，设备从任务记录里取一次
            string? batchDeviceId = null;

            foreach (var taskId in ids)
            {
                var record = await FindTaskDeviceAsync(transfers, devices, taskId, cancellationToken)
                    .ConfigureAwait(false);
                if (record is null)
                {
                    failed++;
                    firstError ??= "任务不存在";
                    continue;
                }

                var (device, direction) = record.Value;
                batchDeviceId ??= device.DeviceId;
                TransferResult<TransferTaskDto>? maybe = action switch
                {
                    "pause" => await transfers.PauseAsync(device, taskId, cancellationToken).ConfigureAwait(false),
                    "resume" => await transfers.ResumeAsync(device, taskId, cancellationToken).ConfigureAwait(false),
                    "cancel" => await transfers.CancelAsync(device, taskId, cancellationToken).ConfigureAwait(false),
                    // 只有下载能重新开始：上传的源在手机上，电脑单方面重来没有意义
                    _ when string.Equals(direction, TransferDirections.Download, StringComparison.Ordinal) =>
                        await transfers.RestartDownloadAsync(device, taskId, cancellationToken).ConfigureAwait(false),
                    _ => null,
                };

                if (maybe is not { } result)
                {
                    failed++;
                    firstError ??= "上传任务不能重新开始";
                    firstErrorCode ??= ErrorCodes.TransferStateInvalid;
                    continue;
                }

                if (result.Success)
                {
                    ok++;
                }
                else
                {
                    var code = result.Error?.Code;

                    // 对面删了这条正在传的任务：恢复只能失败，但这不是"出错了"，
                    // 而是"这条已经不存在了"。单独带回去让界面问要不要删记录。
                    if (action == "resume" &&
                        string.Equals(code, ErrorCodes.TransferCancelled, StringComparison.Ordinal))
                    {
                        cancelledTasks.Add(taskId);
                        continue;
                    }

                    // 已完成/已取消/已删除的任务被一起选中时，跳过就好，别报错。
                    // cancel 也要算跳过：取消整批时，已经传完的那几条本来就该被一起清掉，
                    // 它们返回"任务已结束"不是错误（真正清理由后面的 CancelBatchAsync 做）。
                    if (action is "pause" or "resume" or "cancel" &&
                        code is ErrorCodes.TransferStateInvalid or ErrorCodes.TransferNotFound)
                    {
                        skipped++;
                        continue;
                    }

                    failed++;
                    firstError ??= result.Error?.Message ?? code;
                    firstErrorCode ??= code;
                }
            }

            // 暂停/恢复还要**停住作业队列**：其余文件还没执行，也就还没有传输任务，
            // 只暂停已建出来的任务等于什么都没停。作业改成 paused 后手机不会再取走，
            // 恢复时改回 pending 自动续上。
            var jobsChanged = 0;
            if (action is "pause" or "resume" or "cancel")
            {
                if (action == "cancel")
                {
                    // 取消这批：pending / paused / running 一起作废。
                    // running 是手机已经领走、正在执行的那几个——不一起停，
                    // "取消"之后手机上还会把剩下的传完。
                    jobsChanged = await files
                        .SetDeviceJobsStatusManyAsync(
                            batchDeviceId, request.DestinationPath,
                            [FileJobStatuses.Pending, FileJobStatuses.Paused, FileJobStatuses.Running],
                            FileJobStatuses.Cancelled, cancellationToken)
                        .ConfigureAwait(false);

                    // **按 taskId 精确作废关联的作业**，并把这些任务一并取消。
                    // 上面的按目录作废覆盖的是"还没建出传输任务"的那些；
                    // 已经建出来的（手机正在传的那几个）必须靠这一步终结，
                    // 否则手机端列表里还挂着一条"已取消"、也还在传（真机）。
                    // batchDeviceId 可能为空（取消一个不存在的对象、只有落点目录时）：
                    // 传空进去查设备会抛异常，这里直接跳过。
                    if (!string.IsNullOrWhiteSpace(batchDeviceId))
                    {
                        var jobDevice = await devices
                            .GetAsync(batchDeviceId, cancellationToken)
                            .ConfigureAwait(false);
                        if (jobDevice is not null)
                        {
                            // **双向终止**：任务终结、**两端都删记录**、勾了就连文件一起删。
                            // 只作废作业不够——另一端还把已传完的那些归在「已完成」里
                            // （真机：双端拉文件夹，任一端取消，另一端照样显示已完成）。
                            var cancelled = await transfers
                                .CancelBatchAsync(jobDevice, ids, request.DeleteFiles, cancellationToken)
                                .ConfigureAwait(false);
                            if (cancelled.Success && cancelled.Value is not null)
                            {
                                ok += cancelled.Value.Cancelled + cancelled.Value.Deleted;
                            }
                        }
                    }
                }
                else if (action == "pause")
                {
                    jobsChanged = await files
                        .SetDeviceJobsStatusManyAsync(
                            batchDeviceId, request.DestinationPath,
                            [FileJobStatuses.Pending, FileJobStatuses.Running],
                            FileJobStatuses.Paused, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    jobsChanged = await files
                        .SetDeviceJobsStatusManyAsync(
                            batchDeviceId, request.DestinationPath,
                            [FileJobStatuses.Paused],
                            FileJobStatuses.Pending, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // 一条都没成功也不算请求失败：批里本来就有已完成的任务时，暂停它们会失败，
            // 但用户要的是"剩下的都停下"，不该整单报错。
            // 队列里有作业被停住时同样算成功——那正是用户在停的东西。
            //
            // 全都动不了（记录已删、任务已完成）时也返回 200：界面那边"取消"是两步
            // （先作废队列、再删记录），这一步报 400 会让第二步根本执行不了，
            // 用户看到的就是"不能再次取消"（真机上就卡在这儿）。
            return Results.Json(new
            {
                ok,
                failed,
                skipped,
                firstError,
                firstErrorCode,
                cancelledTasks,
                jobsChanged,
                // "什么都没发生"和"做成了"在旧返回里分不出来，界面只能一律提示成功。
                // 现在至少带上 ok/skipped，前端可以据实说话。
                noop = ok == 0 && jobsChanged == 0,
            });
        });

        // 局部函数：把"任务落点相对路径"对齐到作业的 destination_path
        static (int Open, int Paused) QueueFor(
            string? relativePath,
            IReadOnlyDictionary<string, (int Open, int Paused)> openJobs)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return (0, 0);
            }

            var slash = relativePath.LastIndexOf('/');
            if (slash <= 0)
            {
                return (0, 0);
            }

            return openJobs.TryGetValue(relativePath[..slash], out var found) ? found : (0, 0);
        }

        // 电脑 → 手机：界面选了文件之后落任务（手机来拉）。
        api.MapPost("/transfers/send", async (HttpContext context, LocalSendRequest request) =>
        {
            var transfers = context.RequestServices.GetRequiredService<TransferEngine>();
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var cancellationToken = context.RequestAborted;

            var device = await devices.GetAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                return Results.NotFound(new { code = "device_not_found" });
            }

            var created = new List<string>();
            var failed = new List<string>();

            foreach (var path in request.Paths ?? [])
            {
                var result = await transfers.CreateDownloadAsync(device, path, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (result.Success && result.Value is not null)
                {
                    created.Add(result.Value.TaskId);
                }
                else
                {
                    failed.Add($"{Path.GetFileName(path)}：{result.Error?.Message ?? result.Error?.Code}");
                }
            }

            return Results.Json(new { ok = failed.Count == 0, created, failed });
        });

        // 删除传输记录（只删记录；文件不动）。桌面端「全选 + 删除」用。
        api.MapPost("/transfers/delete", async (HttpContext context, LocalDeleteTransfersRequest request) =>
        {
            var transfers = context.RequestServices.GetRequiredService<TransferEngine>();
            var result = await transfers
                .DeleteRecordsAsync(request.TaskIds ?? [], request.DeleteFiles, context.RequestAborted)
                .ConfigureAwait(false);

            return result.Success
                ? Results.Json(new
                {
                    ok = true,
                    deleted = result.Value?.Deleted ?? 0,
                    filesDeleted = result.Value?.FilesDeleted ?? 0,
                    // 其中有多少条是"还在传、删除时顺手取消掉"的
                    cancelled = result.Value?.Cancelled ?? 0,
                    rejected = result.Value?.Rejected ?? [],
                })
                : Results.BadRequest(new { code = result.Error?.Code, message = result.Error?.Message });
        });

        // ---------------- 共享目录 ----------------
        api.MapGet("/roots", async (HttpContext context) =>
        {
            var roots = context.RequestServices.GetRequiredService<SharedDirectoryRepository>();
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var cancellationToken = context.RequestAborted;

            var names = (await devices.ListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(d => d.DeviceId, d => d.DisplayName, StringComparer.Ordinal);

            var rows = await roots.ListAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<object>();

            foreach (var row in rows)
            {
                var grants = await roots.ListGrantsAsync(row.DirectoryId, cancellationToken).ConfigureAwait(false);
                result.Add(new
                {
                    directoryId = row.DirectoryId,
                    name = row.DisplayName,
                    path = row.RootPath,
                    createdAtUnixMs = row.CreatedAtUtc.ToUnixTimeMilliseconds(),
                    grants = grants.Select(g => new
                    {
                        deviceId = g.DeviceId,
                        // 授权行可能指向已删除的设备（记录没清）：给一句人话，不把 id 甩到界面上
                        deviceName = names.TryGetValue(g.DeviceId, out var n) ? n : "已移除的设备",
                        permissions = (int)g.Permissions,
                        permissionNames = DescribePermissions(g.Permissions),
                    }),
                });
            }

            return Results.Json(result);
        });

        // 新增共享目录：界面选好文件夹 + 勾选设备后一次性落库。
        // 存在同一路径时按"复用已有记录"处理，不重复插入（否则同一目录会在列表里出现两次）。
        api.MapPost("/roots", async (HttpContext context, LocalRootRequest request) =>
        {
            var roots = context.RequestServices.GetRequiredService<SharedDirectoryRepository>();
            var cancellationToken = context.RequestAborted;

            var path = (request.Path ?? string.Empty).Trim();
            if (path.Length == 0 || !Directory.Exists(path))
            {
                return Results.BadRequest(new { code = "invalid_path", message = "目录不存在或不可读" });
            }

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? new DirectoryInfo(path).Name
                : request.Name.Trim();

            var existing = await roots.FindByPathAsync(path, cancellationToken).ConfigureAwait(false);
            var directoryId = existing?.DirectoryId ?? Guid.NewGuid().ToString("n");
            var now = DateTimeOffset.UtcNow;

            if (existing is null)
            {
                await roots.UpsertAsync(
                    new SharedDirectoryRecord
                    {
                        DirectoryId = directoryId,
                        DisplayName = name,
                        RootPath = path,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var grant in request.Grants ?? [])
            {
                if (string.IsNullOrEmpty(grant.DeviceId))
                {
                    continue;
                }

                var permissions = (SharedDirectoryPermissions)grant.Permissions
                    & SharedDirectoryPermissions.All;
                await roots.SetGrantAsync(directoryId, grant.DeviceId, permissions, cancellationToken)
                    .ConfigureAwait(false);
            }

            return Results.Json(new { ok = true, directoryId, name });
        });

        // 整份覆盖某个目录的设备授权：界面按"勾了哪些设备"提交，
        // 没提交的视为收回（SetGrant + DeleteGrant 一组，避免只增不减）。
        api.MapPost("/roots/{directoryId}/grants", async (
            string directoryId,
            HttpContext context,
            LocalRootRequest request) =>
        {
            var roots = context.RequestServices.GetRequiredService<SharedDirectoryRepository>();
            var cancellationToken = context.RequestAborted;

            if (await roots.GetAsync(directoryId, cancellationToken).ConfigureAwait(false) is null)
            {
                return Results.NotFound(new { code = "root_not_found" });
            }

            var desired = (request.Grants ?? [])
                .Where(g => !string.IsNullOrEmpty(g.DeviceId))
                .ToDictionary(g => g.DeviceId, g => (SharedDirectoryPermissions)g.Permissions & SharedDirectoryPermissions.All);

            var existing = await roots.ListGrantsAsync(directoryId, cancellationToken).ConfigureAwait(false);
            foreach (var grant in existing)
            {
                if (!desired.ContainsKey(grant.DeviceId))
                {
                    await roots.DeleteGrantAsync(directoryId, grant.DeviceId, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var pair in desired)
            {
                await roots.SetGrantAsync(directoryId, pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
            }

            return Results.Json(new { ok = true, grants = desired.Count });
        });

        // 取消共享：目录记录与它的授权一起删（文件本身不动）
        api.MapDelete("/roots/{directoryId}", async (string directoryId, HttpContext context) =>
        {
            var roots = context.RequestServices.GetRequiredService<SharedDirectoryRepository>();
            if (await roots.GetAsync(directoryId, context.RequestAborted).ConfigureAwait(false) is null)
            {
                return Results.NotFound(new { code = "root_not_found" });
            }

            await roots.DeleteAsync(directoryId, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new { ok = true });
        });

        // 改接收目录（手机上传的落点）。只影响之后接收的文件，已有文件不搬。
        api.MapPost("/settings/received-directory", async (HttpContext context, LocalReceivedDirectoryRequest request) =>
        {
            var server = context.RequestServices.GetRequiredService<WirelessCableServer>();
            var (ok, error) = await server
                .SetReceivedDirectoryAsync(request.Path ?? string.Empty, context.RequestAborted)
                .ConfigureAwait(false);

            return ok
                ? Results.Json(new { ok = true, path = server.SharedDirectory })
                : Results.BadRequest(new { code = "invalid_directory", message = error });
        });

        // ---------------- 手机文件（电脑浏览手机） ----------------
        // 新版界面的「手机文件」页：根 → 目录 → 文件，保存到电脑走 download 作业。
        api.MapGet("/files/roots", async (HttpContext context, string deviceId) =>
        {
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var files = context.RequestServices.GetRequiredService<FileService>();
            var cancellationToken = context.RequestAborted;

            if (await devices.GetAsync(deviceId, cancellationToken).ConfigureAwait(false) is null)
            {
                return Results.NotFound(new { code = "device_not_found" });
            }

            var roots = await files.ListDeviceRootsAsync(deviceId, cancellationToken).ConfigureAwait(false);
            return Results.Json(roots.Select(root => new
            {
                rootId = root.RootId,
                displayName = root.DisplayName,
                kind = root.Kind,
                capabilities = root.Capabilities,
            }));
        });

        // 列目录。索引快照没有这个目录（超出深度）时现场问一次手机：
        // 8 秒没回就如实返回 error，界面显示"手机没有响应"，不假装空目录。
        api.MapGet("/files/entries", async (
            HttpContext context,
            string deviceId,
            string rootId,
            string? path,
            int? refresh) =>
        {
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var files = context.RequestServices.GetRequiredService<FileService>();
            var cancellationToken = context.RequestAborted;
            var relativePath = path ?? string.Empty;

            // refresh=1：改过手机文件之后用。索引快照还是旧的，必须现场问一次手机，
            // 否则刚删/刚移走的条目会"复活"。
            // 用 int 而不是 bool：查询串写的是 refresh=1，bool 绑定会直接回 400
            var forceLive = refresh == 1;

            if (await devices.GetAsync(deviceId, cancellationToken).ConfigureAwait(false) is null)
            {
                return Results.NotFound(new { code = "device_not_found" });
            }

            var entries = forceLive
                ? []
                : await files
                    .ListDeviceEntriesAsync(deviceId, rootId, relativePath, cancellationToken)
                    .ConfigureAwait(false);

            var live = false;
            string? error = null;

            if (forceLive || (entries.Count == 0 && relativePath.Length > 0))
            {
                // forceLive 时不做"索引里有没有"的判断：就是要现场问一次手机
                var indexed = !forceLive
                    && await files
                        .DeviceEntryExistsAsync(deviceId, rootId, relativePath, cancellationToken)
                        .ConfigureAwait(false);

                if (!indexed)
                {
                    try
                    {
                        var job = await files
                            .EnqueueDeviceJobAsync(
                                deviceId,
                                rootId,
                                FileOperations.List,
                                relativePath,
                                null,
                                null,
                                cancellationToken: cancellationToken)
                            .ConfigureAwait(false);

                        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
                        while (DateTimeOffset.UtcNow < deadline)
                        {
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                            var current = await files.GetJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
                            if (current is null)
                            {
                                break;
                            }

                            if (string.Equals(current.Status, FileJobStatuses.Failed, StringComparison.Ordinal))
                            {
                                error = current.ErrorMessage ?? "手机读取目录失败";
                                break;
                            }

                            if (!string.Equals(current.Status, FileJobStatuses.Completed, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            entries = string.IsNullOrEmpty(current.Payload)
                                ? []
                                : WcJson.Deserialize<FileEntryDto[]>(current.Payload) ?? [];
                            live = true;
                            break;
                        }

                        if (!live && error is null)
                        {
                            error = "手机没有响应，该目录可能尚未索引";
                        }
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "读取手机目录失败 device={Device} root={Root} path={Path}",
                            deviceId, rootId, relativePath);
                        error = ex.Message;
                    }
                }
            }

            return Results.Json(new
            {
                entries = entries.Select(entry => new
                {
                    name = entry.Name,
                    relativePath = entry.RelativePath,
                    isDirectory = entry.IsDirectory,
                    sizeBytes = entry.SizeBytes,
                    modifiedUnixMs = entry.ModifiedUnixMs,
                    capabilities = entry.Capabilities,
                }),
                live,
                error,
            });
        });

        // 还没被手机取走的作业：界面据此显示"等待手机…"，避免重复排队。
        api.MapGet("/files/pending", async (HttpContext context, string deviceId) =>
        {
            var files = context.RequestServices.GetRequiredService<FileService>();
            var jobs = await files.ListPendingDeviceJobsAsync(deviceId, context.RequestAborted).ConfigureAwait(false);

            return Results.Json(jobs.Select(job => new
            {
                jobId = job.JobId,
                operation = job.Operation,
                sourcePath = job.SourcePath,
                displayName = job.DisplayName,
            }));
        });

        // 下发手机文件作业：保存到电脑 / 移动 / 复制 / 删除 / 重命名。
        // 手机不支持的操作由服务端的能力位校验挡住（EnqueueDeviceJobAsync 会抛）。
        api.MapPost("/files/jobs", async (HttpContext context, LocalFileJobRequest request) =>
        {
            var files = context.RequestServices.GetRequiredService<FileService>();
            var cancellationToken = context.RequestAborted;
            var operation = request.Operation ?? string.Empty;

            if (operation is not (FileOperations.Download
                or FileOperations.Move
                or FileOperations.Copy
                or FileOperations.Delete
                or FileOperations.Rename))
            {
                return Results.BadRequest(new { code = "unsupported_operation", message = $"不支持的操作：{operation}" });
            }

            if (string.IsNullOrWhiteSpace(request.SourcePath))
            {
                return Results.BadRequest(new { code = "invalid_path", message = "没有指定要操作的文件。" });
            }

            if (operation is FileOperations.Move or FileOperations.Copy
                && string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                return Results.BadRequest(new { code = "invalid_path", message = "没有指定目标位置。" });
            }

            if (operation == FileOperations.Rename)
            {
                var name = (request.DisplayName ?? string.Empty).Trim();
                if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name is "." or "..")
                {
                    return Results.BadRequest(new { code = "invalid_name", message = "名称不合法。" });
                }
            }

            try
            {
                var job = await files
                    .EnqueueDeviceJobAsync(
                        request.DeviceId,
                        request.RootId,
                        operation,
                        request.SourcePath,
                        request.DestinationPath,
                        request.DisplayName,
                        cancellationToken: cancellationToken,
                        batchTotal: request.BatchTotal,
                        batchName: request.BatchName,
                        batchTotalBytes: request.BatchTotalBytes)
                    .ConfigureAwait(false);

                return Results.Json(new { jobId = job.JobId, status = job.Status });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { code = "job_rejected", message = ex.Message });
            }
        });

        api.MapGet("/files/jobs/{jobId}", async (string jobId, HttpContext context) =>
        {
            var files = context.RequestServices.GetRequiredService<FileService>();
            var job = await files.GetJobAsync(jobId, context.RequestAborted).ConfigureAwait(false);
            if (job is null)
            {
                return Results.NotFound(new { code = "job_not_found" });
            }

            return Results.Json(new
            {
                jobId = job.JobId,
                status = job.Status,
                errorCode = job.ErrorCode,
                errorMessage = job.ErrorMessage,
            });
        });

        // ---------------- 设置（只读快照） ----------------
        api.MapGet("/settings", async (HttpContext context) =>
        {
            var runtime = context.RequestServices.GetRequiredService<ServerRuntime>();
            var transfers = context.RequestServices.GetRequiredService<TransferEngine>();
            var devices = context.RequestServices.GetRequiredService<DeviceRepository>();
            var cancellationToken = context.RequestAborted;

            var snapshot = runtime.Snapshot();
            var paired = await devices.CountPairedAsync(cancellationToken).ConfigureAwait(false);

            return Results.Json(new
            {
                version = snapshot.AppVersion,
                machineName = Environment.MachineName,
                port = snapshot.Port,
                addresses = snapshot.Addresses,
                fingerprint = snapshot.Fingerprint,
                protocolVersion = snapshot.ProtocolVersion,
                pairedDevices = paired,
                receivedDirectory = transfers.SharedDirectory,
                maxActiveTransfers = ProtocolConstants.MaxActiveTransfersPerDevice,
                chunkSize = ProtocolConstants.ChunkSizeBytes,

                // 出码时是否同时开热点：对话框里那个勾选框的初值（也是上次的选择）
                hotspotOnPairing = context.RequestServices
                    .GetRequiredService<LocalHotspotBridge>().ReadPreference?.Invoke() ?? false,
            });
        });
    }

    private static bool IsOnline(DateTimeOffset? lastSeenUtc, DateTimeOffset now) =>
        lastSeenUtc is not null && now - lastSeenUtc.Value <= OnlineWindow;

    /// <summary>
    /// 读可选的 JSON 请求体。没有 body、body 是空、或格式不对都返回 null，
    /// 由调用方按"没传参"处理——出码这类端点不该因为客户端没带 body 就 400。
    /// </summary>
    private static async Task<T?> ReadOptionalBodyAsync<T>(HttpContext context)
        where T : class
    {
        // 只挡"明确是 0"的：chunked 传输时 ContentLength 是 null 但 body 有内容，
        // 按 null 判断会把参数当没传。空 body 由下面的 JsonException 兜住。
        if (context.Request.ContentLength is 0)
        {
            return null;
        }

        try
        {
            return await context.Request
                .ReadFromJsonAsync<T>(WcJson.Options, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> DescribePermissions(SharedDirectoryPermissions permissions)
    {
        var names = new List<string>();
        if (permissions.HasFlag(SharedDirectoryPermissions.Browse)) names.Add("浏览");
        if (permissions.HasFlag(SharedDirectoryPermissions.Download)) names.Add("下载");
        if (permissions.HasFlag(SharedDirectoryPermissions.Upload)) names.Add("上传");
        if (permissions.HasFlag(SharedDirectoryPermissions.Create)) names.Add("新建");
        if (permissions.HasFlag(SharedDirectoryPermissions.Rename)) names.Add("重命名");
        if (permissions.HasFlag(SharedDirectoryPermissions.Move)) names.Add("移动");
        if (permissions.HasFlag(SharedDirectoryPermissions.Delete)) names.Add("删除");
        return names;
    }

    /// <summary>任务属于哪台设备、什么方向 —— 暂停/取消都要把设备带上。</summary>
    /// <summary>
    /// 把 taskId 还原成"哪台设备 + 什么方向"。
    ///
    /// 必须**按 taskId 直查**，不能拿设备列表接口去反扫：
    /// ① 列表会过滤"手机删过的"行（删除各删各的），手机清理过历史后，
    ///    桌面点继续只会得到"任务不存在"——其实行还在，只是对它不可见；
    /// ② 列表有 200 条上限，几百个文件的整批里排在后面的任务永远查不到。
    /// </summary>
    private static async Task<(StoredDevice Device, string Direction)?> FindTaskDeviceAsync(
        TransferEngine transfers,
        DeviceRepository devices,
        string taskId,
        CancellationToken cancellationToken)
    {
        var record = await transfers.FindRecordAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var device = await devices.GetAsync(record.DeviceId, cancellationToken).ConfigureAwait(false);
        return device is null ? null : (device, record.Direction);
    }

    public sealed class LocalSessionRequest
    {
        public bool Paused { get; set; }
    }

    public sealed class LocalMachineAccessRequest
    {
        public int Level { get; set; }
    }

    public sealed class LocalRenameRequest
    {
        public string? Name { get; set; }
    }

    public sealed class LocalRootRequest
    {
        public string? Path { get; set; }

        public string? Name { get; set; }

        public List<LocalGrant>? Grants { get; set; }
    }

    public sealed class LocalGrant
    {
        public string DeviceId { get; set; } = string.Empty;

        public int Permissions { get; set; }
    }

    public sealed class LocalDeleteTransfersRequest
    {
        public List<string>? TaskIds { get; set; }

        public bool DeleteFiles { get; set; }
    }

    /// <summary>一整行（可能是一整批）上的动作。</summary>
    public sealed class LocalBatchActionRequest
    {
        public List<string>? TaskIds { get; set; }

        /// <summary>pause / resume / cancel / restart。</summary>
        public string? Action { get; set; }

        /// <summary>`cancel` 时：连已经收到的文件一起删掉（用户勾了"同时删除文件"）。</summary>
        public bool DeleteFiles { get; set; }

        /// <summary>
        /// 这批的落点相对目录（整文件夹下载时的分组键）。
        ///
        /// 暂停/恢复要连**还没下发给手机**的作业一起停：传输任务是手机执行一个作业才建一条，
        /// 队列里剩下的几十上百个文件根本没对应任务可暂停。没有这个字段，
        /// 用户点了暂停只会收到"只有进行中的任务可以暂停"。
        /// </summary>
        public string? DestinationPath { get; set; }
    }

    /// <summary>出配对码的请求。</summary>
    public sealed class LocalPairingQrRequest
    {
        /// <summary>
        /// 是否同时开启电脑热点。null 表示"没传"，沿用上次的选择。
        ///
        /// 可空是有意的：老前端/脚本不带这个字段时，不能把它当成 false 去关热点。
        /// </summary>
        public bool? Hotspot { get; set; }
    }

    public sealed class LocalSendRequest
    {
        public string DeviceId { get; set; } = string.Empty;

        public List<string>? Paths { get; set; }
    }

    public sealed class LocalReceivedDirectoryRequest
    {
        public string? Path { get; set; }
    }

    public sealed class LocalFileJobRequest
    {
        public string DeviceId { get; set; } = string.Empty;

        public string RootId { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public string? SourcePath { get; set; }

        /// <summary>移动/复制的完整目标路径（含新名字）。</summary>
        public string? DestinationPath { get; set; }

        /// <summary>重命名的新名字（或保存到电脑时的文件名）。</summary>
        public string? DisplayName { get; set; }

        /// <summary>
        /// 本批共多少个文件（整文件夹保存到电脑时由界面填）。
        /// 手机端逐条执行作业、不知道总数，靠它显示 17/330 而不是 17/17。
        /// </summary>
        public int? BatchTotal { get; set; }

        /// <summary>本批对应的文件夹名（手机端收成一行时显示）。</summary>
        public string? BatchName { get; set; }

        /// <summary>本批所有文件的总字节数：手机端进度条的分母。</summary>
        public long? BatchTotalBytes { get; set; }
    }
}
