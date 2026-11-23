using System.Collections.Concurrent;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal enum JavDbMetadataItemOutcomeKind
{
    Updated,
    Unchanged,
    NotFound,
    Failed,
    Skipped
}

internal sealed class JavDbMetadataItemOutcome
{
    public JavDbMetadataItemOutcomeKind Kind { get; init; }

    public string Keyword { get; init; } = string.Empty;

    public string FailureKind { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool IsTransientFailure =>
        Kind == JavDbMetadataItemOutcomeKind.Failed &&
        FailureKind is "network" or "rate_limit" or "auth" or "server" or "unknown";

    public static JavDbMetadataItemOutcome Updated(string keyword) => new()
    {
        Kind = JavDbMetadataItemOutcomeKind.Updated,
        Keyword = keyword
    };

    public static JavDbMetadataItemOutcome Unchanged(string keyword) => new()
    {
        Kind = JavDbMetadataItemOutcomeKind.Unchanged,
        Keyword = keyword
    };

    public static JavDbMetadataItemOutcome NotFound(string keyword, string message) => new()
    {
        Kind = JavDbMetadataItemOutcomeKind.NotFound,
        Keyword = keyword,
        FailureKind = "not_found",
        Message = message
    };

    public static JavDbMetadataItemOutcome Failed(string keyword, string failureKind, string message) => new()
    {
        Kind = JavDbMetadataItemOutcomeKind.Failed,
        Keyword = keyword,
        FailureKind = failureKind,
        Message = message
    };

    public static JavDbMetadataItemOutcome Skipped() => new()
    {
        Kind = JavDbMetadataItemOutcomeKind.Skipped
    };
}

internal static class JavDbMetadataEventSuppressor
{
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> SuppressedItems = new();

    public static void Suppress(Guid itemId, TimeSpan duration)
    {
        if (itemId == Guid.Empty)
            return;

        SuppressedItems[itemId] = DateTimeOffset.UtcNow.Add(duration);
        RemoveExpiredEntries();
    }

    public static bool IsSuppressed(Guid itemId)
    {
        if (!SuppressedItems.TryGetValue(itemId, out var expiresAtUtc))
            return false;

        if (expiresAtUtc > DateTimeOffset.UtcNow)
            return true;

        SuppressedItems.TryRemove(itemId, out _);
        return false;
    }

    private static void RemoveExpiredEntries()
    {
        if (SuppressedItems.Count < 128)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in SuppressedItems)
        {
            if (entry.Value <= now)
                SuppressedItems.TryRemove(entry.Key, out _);
        }
    }
}
