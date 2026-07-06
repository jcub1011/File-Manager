using System;
using System.Diagnostics.CodeAnalysis;

namespace FileManager.Contracts.Primitives;

/// <summary>A success with no value, or a failure carrying a message. For void operations.</summary>
public readonly struct Result
{
    private readonly string? _error;

    public bool IsSuccess { get; }

    private Result(string? error, bool isSuccess)
    {
        _error = error;
        IsSuccess = isSuccess;
    }

    public static Result Success() => new(null, true);
    public static Result Failure(string error) => new(error, false);

    // Ergonomic shortcut: `return "message";` becomes a failure. Success stays explicit
    // (Result.Success()) since there is no value to convert from.
    public static implicit operator Result(string error) => Failure(error);

    public bool TryGetError([MaybeNullWhen(false)] out string error)
    {
        error = _error;
        return !IsSuccess;
    }
}

/// <summary>A success carrying a <typeparamref name="TValue"/>, or a failure carrying a <typeparamref name="TError"/>.</summary>
public readonly struct Result<TValue, TError>
    where TError : notnull
{
    private readonly TValue _value;
    private readonly TError _error;

    public bool IsSuccess { get; }

    private Result(TValue value, TError error, bool isSuccess)
    {
        _value = value;
        _error = error;
        IsSuccess = isSuccess;
    }

    public static Result<TValue, TError> Success(TValue value) => new(value, default!, true);
    public static Result<TValue, TError> Failure(TError error) => new(default!, error, false);

    // Ergonomic shortcuts so methods can `return value;` or `return error;` directly.
    public static implicit operator Result<TValue, TError>(TValue value) => Success(value);
    public static implicit operator Result<TValue, TError>(TError error) => Failure(error);

    /// <summary>Gets the value; false (and no value) when this is a failure.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out TValue value)
    {
        value = _value;
        return IsSuccess;
    }

    /// <summary>Gets the error; false (and no error) when this is a success.</summary>
    public bool TryGetError([MaybeNullWhen(false)] out TError error)
    {
        error = _error;
        return !IsSuccess;
    }

    public void Switch(Action<TValue> onSuccess, Action<TError> onError)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onError);
        if (IsSuccess) onSuccess(_value);
        else onError(_error);
    }

    public TResult Match<TResult>(Func<TValue, TResult> onSuccess, Func<TError, TResult> onError)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onError);
        return IsSuccess ? onSuccess(_value) : onError(_error);
    }
}
