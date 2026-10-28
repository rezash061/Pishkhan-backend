namespace Pishkhan.SharedKernel;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
    PlanLimit,
    RateLimited,
    Unprocessable,
    Unsupported,
    Failure,
}

/// <summary>
/// An expected failure. <see cref="Code"/> is a message key (for example <c>validation.phone.invalid</c>)
/// that frontends translate; it is never an English sentence shown to users.
/// </summary>
public sealed record Error(string Code, ErrorType Type, string? Detail = null, IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public static Error Validation(string code, IReadOnlyDictionary<string, string[]>? fields = null) => new(code, ErrorType.Validation, Fields: fields);

    public static Error NotFound(string code = "common.not_found") => new(code, ErrorType.NotFound);

    public static Error Conflict(string code) => new(code, ErrorType.Conflict);

    public static Error Unauthorized(string code = "auth.unauthorized") => new(code, ErrorType.Unauthorized);

    public static Error Forbidden(string code = "auth.forbidden") => new(code, ErrorType.Forbidden);

    public static Error PlanLimit(string limit) => new("billing.plan_limit", ErrorType.PlanLimit, limit);

    public static Error RateLimited(string code = "common.rate_limited") => new(code, ErrorType.RateLimited);

    public static Error Unprocessable(string code) => new(code, ErrorType.Unprocessable);

    public static Error Unsupported(string code) => new(code, ErrorType.Unsupported);
}

public readonly struct Result
{
    private Result(Error? error) => Error = error;

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => Failure(error);
}

public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result has no value: " + Error!.Code);

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) => IsSuccess ? onSuccess(_value!) : onFailure(Error!);
}
