namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal static class JavDbRequestScheduler
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset? _lastRequestCompletedAt;

    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(10);

    public static async Task<T> RunAsync<T>(
        Func<Task<T>> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastRequestCompletedAt.HasValue)
            {
                var elapsed = DateTimeOffset.UtcNow - _lastRequestCompletedAt.Value;
                var remaining = Interval - elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await request().ConfigureAwait(false);
            }
            finally
            {
                // Only JAVDB callers use this scheduler. Other providers must never be
                // delayed by this shared request cooldown.
                _lastRequestCompletedAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            RequestGate.Release();
        }
    }
}
