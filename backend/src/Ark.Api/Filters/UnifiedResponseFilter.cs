using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Ark.Core.Errors;
using Ark.Core.Responses;
using Microsoft.AspNetCore.Http.Json;

namespace Ark.Api.Filters;

/// <summary>端点元数据标记：跳过统一包络（文件流下载等原始响应）。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SkipEnvelopeAttribute : Attribute;

/// <summary>
/// 统一响应过滤器：成功 → ApiEnvelope{code:0,...}；ArkException → 对应错误码；
/// 未知异常 → 1000 + 500。挂在 /api 根组（第一个注册 = 最外层）。
/// </summary>
public sealed class UnifiedResponseFilter(ILogger<UnifiedResponseFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var traceId = ctx.HttpContext.TraceIdentifier;
        try
        {
            var result = await next(ctx);
            if (ctx.HttpContext.GetEndpoint()?.Metadata.GetMetadata<SkipEnvelopeAttribute>() is not null)
                return result;
            return EnvelopeOk(result, traceId);
        }
        catch (ArkException ex)
        {
            return EnvelopeError(ex.Code, ex.Message, ex.HttpStatus, traceId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "未处理异常 {Method} {Path}", ctx.HttpContext.Request.Method, ctx.HttpContext.Request.Path);
            return EnvelopeError(ArkErrorCodes.Unknown, ex.Message, 500, traceId);
        }
    }

    private static readonly MethodInfo OkMethod = typeof(UnifiedResponseFilter)
        .GetMethod(nameof(OkTyped), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static IResult EnvelopeOk(object? data, string traceId)
    {
        var type = data?.GetType() ?? typeof(object);
        return (IResult)OkMethod.MakeGenericMethod(type).Invoke(null, [data, traceId])!;
    }

    private static IResult OkTyped<T>(T? data, string traceId) =>
        Results.Json(ApiEnvelope<T>.Ok(data, traceId));

    private static IResult EnvelopeError(int code, string message, int status, string traceId) =>
        Results.Json(ApiEnvelope<object>.Error(code, message, traceId), statusCode: status);
}

/// <summary>请求体验证过滤器：DataAnnotations 校验失败 → 1400。</summary>
public sealed class ValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        foreach (var arg in ctx.Arguments)
        {
            if (arg is null or HttpContext or CancellationToken or IFormFile or Stream) continue;
            var type = arg.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(Guid) || type.IsEnum) continue;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) continue;

            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(arg, new ValidationContext(arg), results, validateAllProperties: true))
                throw ArkException.Validation(string.Join("; ", results.Select(r => r.ErrorMessage)));
        }
        return await next(ctx);
    }
}
