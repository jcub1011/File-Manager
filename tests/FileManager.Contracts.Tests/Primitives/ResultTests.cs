using System;
using FileManager.Contracts.Primitives;

namespace FileManager.Contracts.Tests.Primitives;

public class ResultTests
{
    private enum TestError { NotFound, Denied }

    [Fact]
    public void Success_ExposesValue_AndIsNotError()
    {
        var result = Result<int, string>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.True(result.TryGetValue(out var value));
        Assert.Equal(42, value);
        Assert.False(result.TryGetError(out _));
    }

    [Fact]
    public void Failure_ExposesError_AndHasNoValue()
    {
        var result = Result<int, string>.Failure("boom");

        Assert.False(result.IsSuccess);
        Assert.True(result.TryGetError(out var error));
        Assert.Equal("boom", error);
        Assert.False(result.TryGetValue(out _));
    }

    [Fact]
    public void Default_ReadsAsFailure()
    {
        Result<int, string> result = default;

        Assert.False(result.IsSuccess);
        Assert.False(result.TryGetValue(out _));
    }

    [Fact]
    public void Switch_RoutesToCorrectBranch()
    {
        bool successCalled = false;
        string? capturedError = null;

        Result<int, string>.Success(1).Switch(_ => successCalled = true, e => capturedError = e);
        Assert.True(successCalled);
        Assert.Null(capturedError);

        successCalled = false;
        Result<int, string>.Failure("nope").Switch(_ => successCalled = true, e => capturedError = e);
        Assert.False(successCalled);
        Assert.Equal("nope", capturedError);
    }

    [Fact]
    public void Match_ReturnsBranchResult()
    {
        Assert.Equal("ok:5", Result<int, string>.Success(5).Match(v => $"ok:{v}", e => $"err:{e}"));
        Assert.Equal("err:bad", Result<int, string>.Failure("bad").Match(v => $"ok:{v}", e => $"err:{e}"));
    }

    [Fact]
    public void Switch_ThrowsOnNullHandlers()
    {
        var result = Result<int, string>.Success(1);

        Assert.Throws<ArgumentNullException>(() => result.Switch(null!, _ => { }));
        Assert.Throws<ArgumentNullException>(() => result.Switch(_ => { }, null!));
    }

    [Fact]
    public void Match_ThrowsOnNullHandlers()
    {
        var result = Result<int, string>.Success(1);

        Assert.Throws<ArgumentNullException>(() => result.Match<string>(null!, _ => ""));
        Assert.Throws<ArgumentNullException>(() => result.Match<string>(_ => "", null!));
    }

    [Fact]
    public void CustomErrorType_RoundTrips()
    {
        var result = Result<int, TestError>.Failure(TestError.NotFound);

        Assert.False(result.IsSuccess);
        Assert.True(result.TryGetError(out var error));
        Assert.Equal(TestError.NotFound, error);
        Assert.False(result.TryGetValue(out _));
    }

    [Fact]
    public void ImplicitConversion_FromValue_IsSuccess()
    {
        Result<int, string> result = 5;

        Assert.True(result.IsSuccess);
        Assert.True(result.TryGetValue(out var value));
        Assert.Equal(5, value);
    }

    [Fact]
    public void ImplicitConversion_FromError_IsFailure()
    {
        Result<int, TestError> result = TestError.Denied;

        Assert.False(result.IsSuccess);
        Assert.True(result.TryGetError(out var error));
        Assert.Equal(TestError.Denied, error);
    }

    [Fact]
    public void NonGeneric_Success_HasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.TryGetError(out _));
    }

    [Fact]
    public void NonGeneric_Failure_ExposesError()
    {
        var result = Result.Failure("delete failed");

        Assert.False(result.IsSuccess);
        Assert.True(result.TryGetError(out var error));
        Assert.Equal("delete failed", error);
    }

    [Fact]
    public void NonGeneric_ImplicitConversion_FromString_IsFailure()
    {
        Result result = "boom";

        Assert.False(result.IsSuccess);
        Assert.True(result.TryGetError(out var error));
        Assert.Equal("boom", error);
    }
}
