using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WirelessCable.Core.Domain;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
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
                // 手机传过来的文件落到这里；界面上的"打开接收目录"用的是同一个值
                receivedDirectory = transfers.SharedDirectory,
            });
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
        // 每次调用都重新生成一次性随机数；热点只是兜底，开不出来照样出码。
        api.MapPost("/pairing/qr", async (HttpContext context) =>
        {
            var server = context.RequestServices.GetRequiredService<WirelessCableServer>();
            var hotspotBridge = context.RequestServices.GetRequiredService<LocalHotspotBridge>();

            if (server.Port is null)
            {
                return Results.Json(new { ok = false, message = "服务未就绪" });
            }

            QrHotspotDto? hotspot = null;
            string? hotspotText = null;
            string? hotspotError = null;

            if (hotspotBridge.EnsureAsync is not null)
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
                    }
                }
                catch (Exception ex)
                {
                    // 出码不能被热点拖死：拿不到热点就把原因写出来
                    Serilog.Log.Warning(ex, "生成配对二维码时开启热点失败");
                    hotspotError = "电脑热点开启失败，手机可连同一 Wi-Fi";
                }
            }

            var qr = server.CreatePairingQr(hotspot);
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
                hotspotAddress = hotspot?.Address,
                hotspotError,
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
            var cancellationToken = context.RequestAborted;

            var names = (await devices.ListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(d => d.DeviceId, d => d.DisplayName, StringComparer.Ordinal);

            // 上限必须与手机端 /transfers 一致（1000），否则前端按目录分组的批次会被拦腰截断：
            // 一次 330 个文件的整夹下载，200 条只能看到前 200 个，行上显示的"已完成 N/M"就永远对不上。
            var items = await transfers
                .ListRecentAsync(Math.Clamp(limit ?? 100, 1, 1000), names, cancellationToken)
                .ConfigureAwait(false);

            return Results.Json(items.Select(dto => new
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
                createdAtUnixMs = dto.CreatedAtUnixMs,
                updatedAtUnixMs = dto.UpdatedAtUnixMs,
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
            var cancellationToken = context.RequestAborted;

            var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (action is not ("pause" or "resume" or "cancel" or "restart"))
            {
                return Results.BadRequest(new { code = "unsupported_action", action });
            }

            var ids = (request.TaskIds ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (ids.Count == 0)
            {
                return Results.BadRequest(new { code = "invalid_request", message = "没有指定任务。" });
            }

            var ok = 0;
            var failed = 0;
            string? firstError = null;

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
                    continue;
                }

                if (result.Success)
                {
                    ok++;
                }
                else
                {
                    failed++;
                    firstError ??= result.Error?.Message ?? result.Error?.Code;
                }
            }

            // 一条都没成功才算整体失败：批里本来就有已完成的任务时，
            // 暂停它们会失败，但用户要的是"剩下的都停下"，不该整单报错。
            return ok > 0
                ? Results.Json(new { ok, failed, firstError })
                : Results.BadRequest(new { code = "batch_failed", message = firstError ?? "这一批任务都处理不了。" });
        });

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
                        cancellationToken: cancellationToken)
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
            });
        });
    }

    private static bool IsOnline(DateTimeOffset? lastSeenUtc, DateTimeOffset now) =>
        lastSeenUtc is not null && now - lastSeenUtc.Value <= OnlineWindow;

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
    private static async Task<(StoredDevice Device, string Direction)?> FindTaskDeviceAsync(
        TransferEngine transfers,
        DeviceRepository devices,
        string taskId,
        CancellationToken cancellationToken)
    {
        foreach (var device in await devices.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            var items = await transfers.ListAsync(device, 200, cancellationToken).ConfigureAwait(false);
            var hit = items.FirstOrDefault(i => string.Equals(i.TaskId, taskId, StringComparison.Ordinal));
            if (hit is not null)
            {
                return (device, hit.Direction);
            }
        }

        return null;
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
    }
}
