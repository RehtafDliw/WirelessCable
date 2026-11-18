using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Serilog;
using WirelessCable.Infrastructure.Logging;
using WirelessCable.Infrastructure.Security;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Server.Identity;
using WirelessCable.Storage.Entities;
using WirelessCable.Storage.Repositories;

namespace WirelessCable.Server.Pairing;

/// <summary>配对发起结果。</summary>
public sealed record PairingBeginResult(
    bool Success,
    PairingChallengeDto? Challenge,
    string? ErrorCode,
    string? Message,
    int StatusCode);

/// <summary>配对确认结果。</summary>
public sealed record PairingConfirmOutcome(
    bool Success,
    PairingConfirmResponseDto? Response,
    string? ErrorCode,
    string? Message,
    int StatusCode);

/// <summary>
/// 多设备配对队列。
///
/// 安全要点：
/// <list type="number">
/// <item>六位校验码由配对密钥派生（SAS），两端各自算、只显示、<b>不经过网络传输</b>；</item>
/// <item>用户比对两屏数字后在本机点「允许」——批准动作本身就是人工确认；</item>
/// <item>客户端还要用"配对密钥 + 校验码"算出 HMAC 才能确认，中间人算不出这个值；</item>
/// <item>还需用长期身份私钥对会话转录签名，证明持有配对时登记的身份密钥；</item>
/// <item>每个请求独立：独立 requestId、校验码、过期时间、尝试次数与状态，一次批准不影响其他请求；</item>
/// <item>校验码错 5 次即锁定该请求，过期即失效。</item>
/// </list>
/// </summary>
public sealed class PairingService : IDisposable
{
    private const int MaxConcurrentSessions = 16;
    private const int SessionRetentionSeconds = 300;

    private readonly ConcurrentDictionary<string, PairingSession> _sessions = new(StringComparer.Ordinal);
    private readonly DeviceRepository _devices;
    private readonly QrPairingService _qr;
    private readonly ServerRuntimeIdentityProvider _identityProvider;
    private readonly TimeProvider _time;
    private readonly Timer _sweeper;
    private bool _disposed;

    public PairingService(
        DeviceRepository devices,
        QrPairingService qr,
        ServerRuntimeIdentityProvider identityProvider,
        TimeProvider? timeProvider = null)
    {
        _devices = devices;
        _qr = qr;
        _identityProvider = identityProvider;
        _time = timeProvider ?? TimeProvider.System;
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>队列发生变化（新增/批准/拒绝/完成/过期），供 UI 刷新。</summary>
    public event EventHandler? Changed;

    public int PendingCount => _sessions.Values.Count(static s => s.State == PairingStates.Pending);

    /// <summary>当前所有未清理的配对请求（含验证码，仅本机可见）。</summary>
    public IReadOnlyList<PairingSessionViewDto> GetSessions()
        => _sessions.Values
            .OrderByDescending(static s => s.CreatedAt)
            .Select(static s => new PairingSessionViewDto
            {
                RequestId = s.RequestId,
                DeviceId = s.DeviceId,
                DeviceName = s.DeviceName,
                Platform = s.Platform,
                Code = s.Code,
                State = s.State,
                CreatedAtUnixMs = s.CreatedAt.ToUnixTimeMilliseconds(),
                ExpiresAtUnixMs = s.ExpiresAt.ToUnixTimeMilliseconds(),
                Attempts = s.Attempts,
            })
            .ToArray();

    /// <summary>本机用户在界面上批准某个请求。</summary>
    public bool TryApprove(string requestId, out string? error)
    {
        error = null;
        if (!_sessions.TryGetValue(requestId, out var session))
        {
            error = "配对请求不存在。";
            return false;
        }

        lock (session.Gate)
        {
            if (session.State != PairingStates.Pending)
            {
                error = $"当前状态为 {session.State}，无法批准。";
                return false;
            }

            if (session.ExpiresAt <= _time.GetUtcNow())
            {
                session.State = PairingStates.Expired;
                error = "配对请求已过期。";
                OnChanged();
                return false;
            }

            session.State = PairingStates.Approved;
        }

        OnChanged();
        return true;
    }

    /// <summary>拒绝某个请求。拒绝后不可再确认。</summary>
    public bool TryReject(string requestId, out string? error)
    {
        error = null;
        if (!_sessions.TryGetValue(requestId, out var session))
        {
            error = "配对请求不存在。";
            return false;
        }

        lock (session.Gate)
        {
            if (session.State is PairingStates.Completed)
            {
                error = "该请求已完成配对。";
                return false;
            }

            session.State = PairingStates.Rejected;
        }

        OnChanged();
        return true;
    }

    /// <summary>发起配对。返回服务端临时公钥，不含验证码。</summary>
    public PairingBeginResult Begin(PairingRequestDto request, string? remoteAddress)
    {
        if (!IsPlausibleDeviceId(request.DeviceId))
        {
            return FailBegin(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "deviceId 非法。");
        }

        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 64)
        {
            return FailBegin(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "deviceName 非法。");
        }

        if (string.IsNullOrWhiteSpace(request.ClientNonce) || !IsBase64(request.ClientNonce))
        {
            return FailBegin(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "clientNonce 非法。");
        }

        var identity = _identityProvider.GetIdentity();
        if (identity is null)
        {
            return FailBegin(StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServiceUnavailable, "服务尚未就绪。");
        }

        // 同一设备重新发起时，丢弃它之前的请求（避免旧请求堆积）
        foreach (var pair in _sessions)
        {
            if (string.Equals(pair.Value.DeviceId, request.DeviceId, StringComparison.Ordinal))
            {
                RemoveSession(pair.Key);
            }
        }

        if (_sessions.Count >= MaxConcurrentSessions)
        {
            Sweep();
            if (_sessions.Count >= MaxConcurrentSessions)
            {
                return FailBegin(StatusCodes.Status429TooManyRequests, ErrorCodes.Conflict, "并发配对请求过多，请稍后重试。");
            }
        }

        if (!string.IsNullOrEmpty(request.QrNonce) && !_qr.Consume(request.QrNonce))
        {
            return FailBegin(StatusCodes.Status400BadRequest, ErrorCodes.QrNonceInvalid, "二维码已过期或已被使用。");
        }

        // 先校验对端两把公钥，避免无效请求占用会话
        try
        {
            using var identityKey = PairingCrypto.ImportIdentityKey(request.IdentityPublicKey);
            using var peerKey = PairingCrypto.ImportPeerKey(request.EphemeralPublicKey);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return FailBegin(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "公钥格式非法（仅支持 P-256 SPKI）。");
        }

        var serverKey = PairingCrypto.CreateEphemeralKey();
        var serverPublicKey = PairingCrypto.ExportPublicKey(serverKey);
        var serverNonce = PairingCrypto.GenerateNonce();

        byte[] pairingKey;
        byte[] transcript;

        try
        {
            using var peer = PairingCrypto.ImportPeerKey(request.EphemeralPublicKey);
            var shared = PairingCrypto.DeriveSharedSecret(serverKey, peer);
            transcript = PairingCrypto.BuildTranscript(new PairingTranscript(
                request.DeviceId,
                request.EphemeralPublicKey,
                serverPublicKey,
                request.ClientNonce,
                serverNonce));

            pairingKey = PairingCrypto.DerivePairingKey(shared, transcript);
            CryptographicOperations.ZeroMemory(shared);
        }
        catch (CryptographicException ex)
        {
            serverKey.Dispose();
            Log.Warning(ex, "配对密钥协商失败 device={DeviceId}", request.DeviceId);
            return FailBegin(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "密钥协商失败。");
        }

        var now = _time.GetUtcNow();
        var session = new PairingSession
        {
            RequestId = Guid.NewGuid().ToString("N"),
            DeviceId = request.DeviceId,
            DeviceName = request.DeviceName.Trim(),
            Platform = request.Platform,
            IdentityPublicKey = request.IdentityPublicKey,
            ClientEphemeralPublicKey = request.EphemeralPublicKey,
            ServerKey = serverKey,
            ServerEphemeralPublicKey = serverPublicKey,
            ClientNonce = request.ClientNonce,
            ServerNonce = serverNonce,
            Code = PairingCrypto.DeriveSasCode(pairingKey),
            PairingKey = pairingKey,
            Transcript = transcript,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(ProtocolConstants.PairingTtlSeconds),
            RemoteAddress = remoteAddress ?? "unknown",
        };

        _sessions[session.RequestId] = session;

        Log.Information(
            "收到配对请求 device={DeviceId} name={Name} platform={Platform} from={Remote} request={RequestId}",
            session.DeviceId,
            session.DeviceName,
            session.Platform,
            session.RemoteAddress,
            session.RequestId);

        OnChanged();

        return new PairingBeginResult(
            true,
            new PairingChallengeDto
            {
                RequestId = session.RequestId,
                ServerEphemeralPublicKey = serverPublicKey,
                ServerNonce = serverNonce,
                ExpiresAtUnixMs = session.ExpiresAt.ToUnixTimeMilliseconds(),
                State = session.State,
                ServerDeviceId = identity.DeviceId,
                ServerName = identity.DisplayName,
                PairingSuite = ProtocolConstants.PairingSuite,
            },
            null,
            null,
            StatusCodes.Status200OK);
    }

    /// <summary>提交确认：验证码 HMAC + 身份签名，通过后颁发封装令牌。</summary>
    public async Task<PairingConfirmOutcome> ConfirmAsync(PairingConfirmRequestDto request, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(request.RequestId, out var session))
        {
            return FailConfirm(StatusCodes.Status404NotFound, ErrorCodes.PairingNotFound, "配对请求不存在。");
        }

        byte[] providedConfirmation;
        byte[] providedSignature;
        try
        {
            providedConfirmation = Convert.FromBase64String(request.Confirmation);
            providedSignature = Convert.FromBase64String(request.Signature);
        }
        catch (FormatException)
        {
            return FailConfirm(StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest, "确认数据不是合法 Base64。");
        }

        lock (session.Gate)
        {
            if (session.State is PairingStates.Completed)
            {
                return FailConfirm(StatusCodes.Status409Conflict, ErrorCodes.PairingAlreadyCompleted, "该请求已完成配对。");
            }

            if (session.State is PairingStates.Rejected)
            {
                return FailConfirm(StatusCodes.Status403Forbidden, ErrorCodes.PairingNotApproved, "本机已拒绝该配对请求。");
            }

            if (session.State is PairingStates.Locked)
            {
                return FailConfirm(StatusCodes.Status400BadRequest, ErrorCodes.PairingAttemptsExceeded, "验证码尝试次数过多，请重新发起配对。");
            }

            if (session.State is PairingStates.Expired || session.ExpiresAt <= _time.GetUtcNow())
            {
                session.State = PairingStates.Expired;
                return FailConfirm(StatusCodes.Status400BadRequest, ErrorCodes.PairingExpired, "配对请求已过期。");
            }

            if (session.State is not PairingStates.Approved)
            {
                return FailConfirm(StatusCodes.Status409Conflict, ErrorCodes.PairingNotApproved, "请先在本机确认该配对请求。");
            }

            var expected = PairingCrypto.ComputeConfirmation(session.PairingKey, session.Code);
            if (!PairingCrypto.FixedTimeEquals(expected, providedConfirmation))
            {
                session.Attempts++;
                if (session.Attempts >= ProtocolConstants.PairingMaxAttempts)
                {
                    session.State = PairingStates.Locked;
                }

                Log.Warning(
                    "配对码校验失败 device={DeviceId} attempts={Attempts} request={RequestId}",
                    session.DeviceId,
                    session.Attempts,
                    session.RequestId);

                OnChanged();
                return FailConfirm(
                    StatusCodes.Status401Unauthorized,
                    session.State is PairingStates.Locked ? ErrorCodes.PairingAttemptsExceeded : ErrorCodes.PairingCodeInvalid,
                    session.State is PairingStates.Locked ? "验证码尝试次数过多，请重新发起配对。" : "验证码不正确。");
            }

            // 验证码通过后，再验证长期身份私钥签名（证明持有配对登记的身份密钥）
            try
            {
                using var identityKey = PairingCrypto.ImportIdentityKey(session.IdentityPublicKey);
                if (!identityKey.VerifyData(session.Transcript, providedSignature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                {
                    session.Attempts++;
                    OnChanged();
                    return FailConfirm(StatusCodes.Status401Unauthorized, ErrorCodes.SignatureInvalid, "身份签名校验失败。");
                }
            }
            catch (CryptographicException)
            {
                return FailConfirm(StatusCodes.Status400BadRequest, ErrorCodes.SignatureInvalid, "身份公钥无法用于验签。");
            }
        }

        // 走到这里说明已通过全部密码学校验
        var token = PairingCrypto.GenerateToken();
        var tokenHash = PairingCrypto.HashToken(token);
        var pairedAt = _time.GetUtcNow();
        var sealedToken = PairingCrypto.SealToken(session.PairingKey, session.RequestId, System.Text.Encoding.UTF8.GetBytes(token));

        var existing = await _devices.GetAsync(session.DeviceId, cancellationToken).ConfigureAwait(false);

        await _devices.UpsertAsync(new StoredDevice
        {
            DeviceId = session.DeviceId,
            DisplayName = session.DeviceName,
            Platform = session.Platform,
            CertificateFingerprint = existing?.CertificateFingerprint ?? string.Empty,
            IdentityPublicKey = session.IdentityPublicKey,
            TokenHash = tokenHash,
            IsPaired = true,
            IsRevoked = false,
            LastSeenUtc = pairedAt,
            PairedAtUtc = pairedAt,
            CreatedAtUtc = existing?.CreatedAtUtc ?? pairedAt,
        }, cancellationToken).ConfigureAwait(false);

        lock (session.Gate)
        {
            session.State = PairingStates.Completed;
        }

        session.DisposeKey();
        OnChanged();

        Log.Information(
            "配对完成 device={DeviceId} name={Name} platform={Platform}",
            session.DeviceId,
            session.DeviceName,
            session.Platform);

        return new PairingConfirmOutcome(
            true,
            new PairingConfirmResponseDto
            {
                SealedToken = Convert.ToBase64String(sealedToken),
                ServerDeviceId = _identityProvider.GetIdentity()?.DeviceId ?? string.Empty,
                PairedAtUnixMs = pairedAt.ToUnixTimeMilliseconds(),
                HeartbeatIntervalSeconds = ProtocolConstants.HeartbeatIntervalSeconds,
            },
            null,
            null,
            StatusCodes.Status200OK);
    }

    /// <summary>查询单个请求状态（不含验证码）。</summary>
    public string? GetState(string requestId)
        => _sessions.TryGetValue(requestId, out var session) ? session.State : null;

    public void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _sessions)
        {
            var session = pair.Value;
            var changed = false;

            lock (session.Gate)
            {
                if (session.State is PairingStates.Pending or PairingStates.Approved && session.ExpiresAt <= now)
                {
                    session.State = PairingStates.Expired;
                    changed = true;
                }

                if (session.ExpiresAt.AddSeconds(SessionRetentionSeconds) <= now)
                {
                    RemoveSession(pair.Key);
                    continue;
                }
            }

            if (changed)
            {
                OnChanged();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sweeper.Dispose();

        foreach (var session in _sessions.Values)
        {
            session.DisposeKey();
        }

        _sessions.Clear();
    }

    private void RemoveSession(string requestId)
    {
        if (_sessions.TryRemove(requestId, out var removed))
        {
            removed.DisposeKey();
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static PairingBeginResult FailBegin(int status, string code, string message)
        => new(false, null, code, message, status);

    private static PairingConfirmOutcome FailConfirm(int status, string code, string message)
        => new(false, null, code, message, status);

    private static bool IsPlausibleDeviceId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length is >= 8 and <= 64 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsBase64(string value)
    {
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out _);
    }

    /// <summary>一次配对请求的内存态。持有服务端临时私钥，完成后立即释放。</summary>
    private sealed class PairingSession
    {
        public required string RequestId { get; init; }
        public required string DeviceId { get; init; }
        public required string DeviceName { get; init; }
        public required string Platform { get; init; }
        public required string IdentityPublicKey { get; init; }
        public required string ClientEphemeralPublicKey { get; init; }
        public required ECDiffieHellman ServerKey { get; init; }
        public required string ServerEphemeralPublicKey { get; init; }
        public required string ClientNonce { get; init; }
        public required string ServerNonce { get; init; }
        public required string Code { get; init; }
        public required byte[] PairingKey { get; init; }
        public required byte[] Transcript { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required string RemoteAddress { get; init; }

        public readonly Lock Gate = new();
        public string State { get; set; } = PairingStates.Pending;
        public int Attempts { get; set; }

        public void DisposeKey()
        {
            try
            {
                ServerKey.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

/// <summary>取当前服务端身份的小接口，避免 PairingService 直接依赖宿主。</summary>
public interface ServerRuntimeIdentityProvider
{
    DeviceIdentity? GetIdentity();
}
