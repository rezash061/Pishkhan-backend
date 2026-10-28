using Microsoft.AspNetCore.Http;
using Pishkhan.SharedKernel;

namespace Pishkhan.Web.Http;

/// <summary>RFC 9457 problem types (overview section 6). Frontends switch on <c>type</c> and translate <c>errors</c> message keys.</summary>
public static class ProblemTypes
{
    public const string Base = "https://pishkhan.ir/problems/";
    public const string Validation = Base + "validation";
    public const string NotFound = Base + "not-found";
    public const string Conflict = Base + "conflict";
    public const string Unauthorized = Base + "unauthorized";
    public const string Forbidden = Base + "forbidden";
    public const string PlanLimit = Base + "plan-limit";
    public const string RateLimited = Base + "rate-limited";
    public const string Unprocessable = Base + "unprocessable";
    public const string Unsupported = Base + "unsupported-media-type";
    public const string Failure = Base + "internal";
    public const string IdempotencyMismatch = Base + "idempotency-key-reused";
}

public static class ResultHttpExtensions
{
    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error!.ToProblem();

    public static IResult ToHttp<T>(this Result<T> result) => result.ToHttp(v => Results.Ok(v));

    public static IResult ToHttp(this Result result) => result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();

    public static IResult ToProblem(this Error error)
    {
        var (status, type) = error.Type switch
        {
            ErrorType.Validation => (StatusCodes.Status400BadRequest, ProblemTypes.Validation),
            ErrorType.NotFound => (StatusCodes.Status404NotFound, ProblemTypes.NotFound),
            ErrorType.Conflict => (StatusCodes.Status409Conflict, ProblemTypes.Conflict),
            ErrorType.Unauthorized => (StatusCodes.Status401Unauthorized, ProblemTypes.Unauthorized),
            ErrorType.Forbidden => (StatusCodes.Status403Forbidden, ProblemTypes.Forbidden),
            ErrorType.PlanLimit => (StatusCodes.Status402PaymentRequired, ProblemTypes.PlanLimit),
            ErrorType.RateLimited => (StatusCodes.Status429TooManyRequests, ProblemTypes.RateLimited),
            ErrorType.Unprocessable => (StatusCodes.Status422UnprocessableEntity, ProblemTypes.Unprocessable),
            ErrorType.Unsupported => (StatusCodes.Status415UnsupportedMediaType, ProblemTypes.Unsupported),
            _ => (StatusCodes.Status500InternalServerError, ProblemTypes.Failure),
        };

        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = error.Code };
        if (error.Fields is { Count: > 0 })
        {
            extensions["errors"] = error.Fields;
        }

        // detail carries the machine-readable limit name for plan-limit problems (the panel turns it into an upgrade prompt).
        return Results.Problem(statusCode: status, type: type, title: error.Code, detail: error.Detail, extensions: extensions);
    }
}
