using MoaiCode.Core.Agent;

namespace MoaiCode.Providers;

/// <summary>오류 시맨틱 분류 (TS openaiErrorClassification 축약판).</summary>
public enum ErrorCategory
{
    Unknown,
    AuthInvalid,      // 401, 403
    RateLimited,      // 429
    Overloaded,       // 529
    ServerError,      // 5xx
    BadRequest,       // 400, 422
    ContextOverflow,  // context length 초과
}

/// <summary>프로바이더 API 오류.</summary>
public sealed class ProviderException : Exception, IModelException
{
    public int StatusCode { get; }
    public ErrorCategory Category { get; }

    public ProviderException(int statusCode, string message)
        : base($"Provider error {statusCode}: {message}")
    {
        StatusCode = statusCode;
        Category = Classify(statusCode, message);
    }

    /// <summary>재시도 가능한 transient 오류인지.</summary>
    public bool IsTransient =>
        Category is ErrorCategory.RateLimited or ErrorCategory.Overloaded or ErrorCategory.ServerError;

    /// <summary>컨텍스트 길이 초과 (Core의 반응형 컴팩션 트리거).</summary>
    public bool IsContextOverflow => Category == ErrorCategory.ContextOverflow;

    private static ErrorCategory Classify(int status, string message)
    {
        var m = message.ToLowerInvariant();
        if (status is 401 or 403)
        {
            return ErrorCategory.AuthInvalid;
        }

        if (status == 429)
        {
            return ErrorCategory.RateLimited;
        }

        if (status == 529)
        {
            return ErrorCategory.Overloaded;
        }

        if (status is 400 or 422)
        {
            return m.Contains("context") || m.Contains("maximum context") || m.Contains("too long")
                ? ErrorCategory.ContextOverflow
                : ErrorCategory.BadRequest;
        }

        if (status >= 500)
        {
            return ErrorCategory.ServerError;
        }

        return ErrorCategory.Unknown;
    }
}
