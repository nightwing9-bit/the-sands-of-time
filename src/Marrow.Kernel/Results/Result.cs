using System.Diagnostics.CodeAnalysis;

namespace Marrow.Kernel.Results;

/// <summary>
/// Dependency-free success-or-typed-error result. Exactly one of value or error is present.
/// Public module operations return this instead of using exceptions for expected failures.
/// </summary>
public sealed class Result<TValue, TError> where TError : notnull
{
    private readonly TValue? _value;
    private readonly TError? _error;

    private Result(bool isSuccess, TValue? value, TError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    /// <summary>The success value. Throws if this result is a failure; prefer <see cref="TryGetValue"/> or <see cref="Match{TOut}"/>.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure: {_error}");

    /// <summary>The error. Throws if this result is a success.</summary>
    public TError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Result is a success and has no error.");

    public static Result<TValue, TError> Success(TValue value) => new(true, value, default);

    public static Result<TValue, TError> Failure(TError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(false, default, error);
    }

    public bool TryGetValue([MaybeNullWhen(false)] out TValue value)
    {
        value = IsSuccess ? _value : default;
        return IsSuccess;
    }

    public bool TryGetError([MaybeNullWhen(false)] out TError error)
    {
        error = IsFailure ? _error : default;
        return IsFailure;
    }

    public TOut Match<TOut>(Func<TValue, TOut> onSuccess, Func<TError, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(_error!);
    }

    public override string ToString() => IsSuccess ? $"Success({_value})" : $"Failure({_error})";
}
