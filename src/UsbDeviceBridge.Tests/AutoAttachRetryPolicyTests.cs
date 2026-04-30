using UsbDeviceBridge.Service.Services;

namespace UsbDeviceBridge.Tests;

public class AutoAttachRetryPolicyTests
{
    [Fact]
    public void RecordFailure_UsesExponentialBackoffSchedule()
    {
        var now = new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero);

        var attempt1 = AutoAttachRetryPolicy.RecordFailure(now, null);
        var attempt2 = AutoAttachRetryPolicy.RecordFailure(now, attempt1);
        var attempt3 = AutoAttachRetryPolicy.RecordFailure(now, attempt2);

        Assert.False(attempt1.Abandoned);
        Assert.Equal(1, attempt1.FailureCount);
        Assert.Equal(now.AddSeconds(2), attempt1.NextAttemptUtc);

        Assert.False(attempt2.Abandoned);
        Assert.Equal(2, attempt2.FailureCount);
        Assert.Equal(now.AddSeconds(5), attempt2.NextAttemptUtc);

        Assert.False(attempt3.Abandoned);
        Assert.Equal(3, attempt3.FailureCount);
        Assert.Equal(now.AddSeconds(15), attempt3.NextAttemptUtc);
    }

    [Fact]
    public void RecordFailure_AfterMaxAttempts_IsAbandoned()
    {
        var now = new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero);

        var state = AutoAttachRetryPolicy.RecordFailure(now, null);
        state = AutoAttachRetryPolicy.RecordFailure(now, state);
        state = AutoAttachRetryPolicy.RecordFailure(now, state);
        state = AutoAttachRetryPolicy.RecordFailure(now, state);

        Assert.True(state.Abandoned);
        Assert.Equal(AutoAttachRetryPolicy.MaxAttempts, state.FailureCount);
        Assert.Equal(now, state.NextAttemptUtc);
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        var now = new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero);

        var success = AutoAttachRetryPolicy.RecordSuccess(now);

        Assert.False(success.Abandoned);
        Assert.Equal(0, success.FailureCount);
        Assert.Equal(now, success.NextAttemptUtc);
    }
}
