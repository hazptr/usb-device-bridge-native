namespace UsbDeviceBridge.Service.Services;

public sealed record AutoAttachRetryState(int FailureCount, DateTimeOffset NextAttemptUtc, bool Abandoned);

public static class AutoAttachRetryPolicy
{
    public const int MaxAttempts = 4;

    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(60),
    ];

    public static AutoAttachRetryState RecordFailure(DateTimeOffset now, AutoAttachRetryState? previous)
    {
        var previousFailures = previous?.FailureCount ?? 0;
        var failures = previousFailures + 1;
        var abandoned = failures >= MaxAttempts;

        if (abandoned)
            return new AutoAttachRetryState(failures, now, true);

        var delay = BackoffDelays[Math.Min(failures - 1, BackoffDelays.Length - 1)];
        return new AutoAttachRetryState(failures, now.Add(delay), false);
    }

    public static AutoAttachRetryState RecordSuccess(DateTimeOffset now)
    {
        return new AutoAttachRetryState(0, now, false);
    }
}
