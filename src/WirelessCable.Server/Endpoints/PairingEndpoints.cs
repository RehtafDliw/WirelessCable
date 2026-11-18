using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using WirelessCable.Server.Http;
using WirelessCable.Server.Pairing;

namespace WirelessCable.Server.Endpoints;

/// <summary>
/// 配对三接口。均为匿名可访问：客户端还没有令牌，这是唯一能建立信任的入口，
/// 因此依赖验证码 + 密钥协商 + 本机批准三重约束。
/// </summary>
public static class PairingEndpoints
{
    public static void MapPairingEndpoints(this WebApplication app)
    {
        app.MapPost(ProtocolConstants.PairRequestPath, HandleRequestAsync);
        app.MapPost(ProtocolConstants.PairConfirmPath, HandleConfirmAsync);
        app.MapGet(ProtocolConstants.PairStatusPath + "{requestId}", HandleStatusAsync);
    }

    private static async Task HandleRequestAsync(
        HttpContext context,
        PairingService pairing,
        CancellationToken cancellationToken)
    {
        var request = await TryReadAsync<PairingRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                ErrorCodes.InvalidRequest,
                "请求体不是合法的配对请求。").ConfigureAwait(false);
            return;
        }

        var result = pairing.Begin(request, context.Connection.RemoteIpAddress?.ToString());

        if (!result.Success || result.Challenge is null)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                result.StatusCode,
                result.ErrorCode ?? ErrorCodes.InternalError,
                result.Message ?? "配对请求失败。").ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, result.Challenge, cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleConfirmAsync(
        HttpContext context,
        PairingService pairing,
        CancellationToken cancellationToken)
    {
        var request = await TryReadAsync<PairingConfirmRequestDto>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                ErrorCodes.InvalidRequest,
                "请求体不是合法的配对确认。").ConfigureAwait(false);
            return;
        }

        var outcome = await pairing.ConfirmAsync(request, cancellationToken).ConfigureAwait(false);

        if (!outcome.Success || outcome.Response is null)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                outcome.StatusCode,
                outcome.ErrorCode ?? ErrorCodes.InternalError,
                outcome.Message ?? "配对确认失败。").ConfigureAwait(false);
            return;
        }

        await ApiResponseWriter.WriteSuccessAsync(context, outcome.Response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleStatusAsync(
        HttpContext context,
        string requestId,
        PairingService pairing,
        CancellationToken cancellationToken)
    {
        var state = pairing.GetState(requestId);
        if (state is null)
        {
            await ApiResponseWriter.WriteErrorAsync(
                context,
                StatusCodes.Status404NotFound,
                ErrorCodes.PairingNotFound,
                "配对请求不存在。").ConfigureAwait(false);
            return;
        }

        var sessions = pairing.GetSessions();
        var session = sessions.FirstOrDefault(s => s.RequestId == requestId);

        var payload = new PairingStatusDto
        {
            RequestId = requestId,
            State = state,
            ExpiresAtUnixMs = session?.ExpiresAtUnixMs ?? 0,
        };

        await ApiResponseWriter.WriteSuccessAsync(context, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T?> TryReadAsync<T>(HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength is 0)
        {
            return null;
        }

        try
        {
            return await context.Request
                .ReadFromJsonAsync<T>(WcJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (BadHttpRequestException)
        {
            return null;
        }
    }
}
