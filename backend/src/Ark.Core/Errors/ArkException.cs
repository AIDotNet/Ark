using Ark.Core.Responses;

namespace Ark.Core.Errors;

/// <summary>业务异常：携带统一错误码与期望的 HTTP 状态码，由统一响应过滤器转换为包络。</summary>
public class ArkException : Exception
{
    public int Code { get; }
    public int HttpStatus { get; }

    public ArkException(int code, string message, int httpStatus = 400) : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public static ArkException NotFound(string message) => new(ArkErrorCodes.NotFound, message, 404);
    public static ArkException Validation(string message) => new(ArkErrorCodes.Validation, message, 400);
}
