using Microsoft.AspNetCore.Http;
using WirelessCable.Protocol;
using WirelessCable.Protocol.Dtos;
using WirelessCable.Protocol.Json;
using WirelessCable.Server.Middleware;
using Xunit;

namespace WirelessCable.Server.Tests;

public sealed class ExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task UnhandledException_BecomesInternalErrorEnvelope_WithoutLeakingMessage()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/api/v1/boom";

        var middleware = new ExceptionHandlingMiddleware(_ => throw new InvalidOperationException("数据库密码是 hunter2"));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var result = WcJson.DeserializeRequired<ApiResult<object>>(body);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InternalError, result.Error!.Code);
        Assert.True(result.Error.Retryable);
        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.TraceId));
    }

    [Fact]
    public async Task BadHttpRequest_BecomesInvalidRequestEnvelope()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/api/v1/bad";

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new BadHttpRequestException("请求体格式错误", StatusCodes.Status400BadRequest));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var result = WcJson.DeserializeRequired<ApiResult<object>>(body);

        Assert.Equal(ErrorCodes.InvalidRequest, result.Error!.Code);
    }
}
