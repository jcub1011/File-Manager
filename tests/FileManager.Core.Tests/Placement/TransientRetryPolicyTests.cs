using FileManager.Contracts.Primitives;
using FileManager.Core.Jobs;
using FileManager.Core.Placement;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace FileManager.Core.Tests.Placement;

public sealed class TransientRetryPolicyTests
{
    private static JobError Err() => new() { Code = JobErrorCode.TargetWriteFailed, Message = "transient" };

    [Fact]
    public async Task Succeeds_without_delay_on_the_first_attempt()
    {
        var policy = new TransientRetryPolicy(new FakeTimeProvider(), NullLogger<TransientRetryPolicy>.Instance);
        int calls = 0;
        Result<int, JobError> result = await policy.ExecuteAsync<int>("op", _ => { calls++; return Task.FromResult<Result<int, JobError>>(7); });

        Assert.True(result.TryGetValue(out int value));
        Assert.Equal(7, value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retries_up_to_three_attempts_then_returns_the_final_failure()
    {
        var time = new FakeTimeProvider();
        var policy = new TransientRetryPolicy(time, NullLogger<TransientRetryPolicy>.Instance);
        int calls = 0;

        Task<Result<int, JobError>> run = policy.ExecuteAsync<int>("op", _ =>
        {
            calls++;
            return Task.FromResult<Result<int, JobError>>(Err());
        });

        // Two 2s delays sit between the three attempts; advance past both.
        while (!run.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromSeconds(2));
        }

        Result<int, JobError> result = await run;
        Assert.True(result.IsFailure);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Recovers_when_a_later_attempt_succeeds()
    {
        var time = new FakeTimeProvider();
        var policy = new TransientRetryPolicy(time, NullLogger<TransientRetryPolicy>.Instance);
        int calls = 0;

        Task<Result<int, JobError>> run = policy.ExecuteAsync<int>("op", _ =>
        {
            calls++;
            return Task.FromResult<Result<int, JobError>>(calls >= 2 ? 42 : Err());
        });
        while (!run.IsCompleted)
        {
            await Task.Yield();
            time.Advance(TimeSpan.FromSeconds(2));
        }

        Result<int, JobError> result = await run;
        Assert.True(result.TryGetValue(out int value));
        Assert.Equal(42, value);
        Assert.Equal(2, calls);
    }
}
