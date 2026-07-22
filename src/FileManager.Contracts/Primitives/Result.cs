using System;
using System.Diagnostics.CodeAnalysis;

namespace FileManager.Contracts.Primitives;

/// <summary>The outcome state of a <see cref="Result"/> or <see cref="Result{TValue, TError}"/>.
/// A result is always in exactly one state; the enum is [Flags] only so callers can test set
/// membership (e.g. <c>(Status &amp; (Success | Canceled)) != 0</c> — "succeeded or was cancelled,
/// either way not a failure").</summary>
[Flags]
public enum ResultStatus: byte
{
    Unknown = 0,
    Success = 1 << 0,
    Failure = 1 << 1,
    Canceled = 1 << 2,
}

/// <summary>A success with no value, a failure carrying a message, or a cancellation. For void
/// operations. Cancellation is a distinct state (not a failure) so callers can tell "the user
/// cancelled" apart from "the operation failed" — see <see cref="ResultStatus"/>.</summary>
public readonly struct Result
{
    private readonly string? _error;

    public ResultStatus Status { get; }

    public bool IsSuccess => Status == ResultStatus.Success;
    public bool IsFailure => Status == ResultStatus.Failure;
    public bool IsCanceled => Status == ResultStatus.Canceled;

    private Result(string? error, ResultStatus status)
    {
        _error = error;
        Status = status;
    }

    public static Result Success() => new(null, ResultStatus.Success);
    public static Result Failure(string error) => new(error, ResultStatus.Failure);
    public static Result Canceled() => new(null, ResultStatus.Canceled);

    // Ergonomic shortcut: `return "message";` becomes a failure. Success/Canceled stay explicit
    // (Result.Success()/Result.Canceled()) since there is no value to convert from.
    public static implicit operator Result(string error) => Failure(error);

    /// <summary>Gets the error; true only for a genuine failure. A canceled result reports NO
    /// error (there is none), so callers distinguishing cancellation must check <see cref="IsCanceled"/>.</summary>
    public bool TryGetError([MaybeNullWhen(false)] out string error)
    {
        error = _error;
        return IsFailure;
    }
}

/// <summary>A success carrying a <typeparamref name="TValue"/>, a failure carrying a
/// <typeparamref name="TError"/>, or a cancellation. Cancellation is a distinct state (not a
/// failure) so it need not be shoehorned into <typeparamref name="TError"/> — see
/// <see cref="ResultStatus"/>.</summary>
public readonly struct Result<TValue, TError>
    where TError : notnull
{
    private readonly TValue _value;
    private readonly TError _error;

    public ResultStatus Status { get; }

    public bool IsSuccess => Status == ResultStatus.Success;
    public bool IsFailure => Status == ResultStatus.Failure;
    public bool IsCanceled => Status == ResultStatus.Canceled;

    private Result(TValue value, TError error, ResultStatus status)
    {
        _value = value;
        _error = error;
        Status = status;
    }

    public static Result<TValue, TError> Success(TValue value) => new(value, default!, ResultStatus.Success);
    public static Result<TValue, TError> Failure(TError error) => new(default!, error, ResultStatus.Failure);
    public static Result<TValue, TError> Canceled() => new(default!, default!, ResultStatus.Canceled);

    // Ergonomic shortcuts so methods can `return value;` or `return error;` directly.
    // Cancellation has neither a value nor an error, so it always uses the explicit Canceled() factory.
    public static implicit operator Result<TValue, TError>(TValue value) => Success(value);
    public static implicit operator Result<TValue, TError>(TError error) => Failure(error);

    /// <summary>Gets the value; false (and no value) unless this is a success.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out TValue value)
    {
        value = _value;
        return IsSuccess;
    }

    /// <summary>Gets the error; true only for a genuine failure. A canceled result reports NO
    /// error (there is none), so callers distinguishing cancellation must check <see cref="IsCanceled"/>.</summary>
    public bool TryGetError([MaybeNullWhen(false)] out TError error)
    {
        error = _error;
        return IsFailure;
    }

    /// <summary>Two-way Switch. Throws if invoked on a canceled result — use the three-way
    /// overload for anything that can be cancelled rather than silently routing a null error.</summary>
    public void Switch(Action<TValue> onSuccess, Action<TError> onError)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onError);
        if (IsSuccess) onSuccess(_value);
        else if (IsFailure) onError(_error);
        else throw new InvalidOperationException("Switch(onSuccess, onError) was called on a canceled result; use the overload with onCanceled.");
    }

    public void Switch(Action<TValue> onSuccess, Action<TError> onError, Action onCanceled)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onError);
        ArgumentNullException.ThrowIfNull(onCanceled);
        if (IsSuccess) onSuccess(_value);
        else if (IsFailure) onError(_error);
        else onCanceled();
    }

    /// <summary>Two-way Match. Throws if invoked on a canceled result — use the three-way
    /// overload for anything that can be cancelled rather than silently routing a null error.</summary>
    public TResult Match<TResult>(Func<TValue, TResult> onSuccess, Func<TError, TResult> onError)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onError);
        if (IsSuccess) return onSuccess(_value);
        if (IsFailure) return onError(_error);
        throw new InvalidOperationException("Match(onSuccess, onError) was called on a canceled result; use the overload with onCanceled.");
    }

    public TResult Match<TResult>(Func<TValue, TResult> onSuccess, Func<TError, TResult> onError, Func<TResult> onCanceled)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onError);
        ArgumentNullException.ThrowIfNull(onCanceled);
        if (IsSuccess) return onSuccess(_value);
        if (IsFailure) return onError(_error);
        return onCanceled();
    }
}
