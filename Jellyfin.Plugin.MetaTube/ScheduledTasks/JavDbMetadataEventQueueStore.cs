using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal static class JavDbMetadataEventQueueStore
{
    public const string FileName = "javdb-metadata-event-queue.json";
    private const int CurrentSchemaVersion = 1;
    private const int MaxQueueItems = 5000;
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<JavDbMetadataEventQueue> LoadAsync(
        string queueFilePath,
        CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var queue = new JavDbMetadataEventQueue();
            if (File.Exists(queueFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(queueFilePath, cancellationToken).ConfigureAwait(false);
                    queue = JsonSerializer.Deserialize<JavDbMetadataEventQueue>(json, JsonOptions)
                            ?? new JavDbMetadataEventQueue();
                }
                catch (JsonException)
                {
                    queue = new JavDbMetadataEventQueue();
                }
            }

            Normalize(queue);
            return queue;
        }
        finally
        {
            FileLock.Release();
        }
    }

    public static async Task SaveAsync(
        string queueFilePath,
        JavDbMetadataEventQueue queue,
        CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Normalize(queue);
            Directory.CreateDirectory(Path.GetDirectoryName(queueFilePath) ?? ".");
            var temporaryPath = $"{queueFilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var json = JsonSerializer.Serialize(queue, JsonOptions);
                await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporaryPath, queueFilePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static void Normalize(JavDbMetadataEventQueue queue)
    {
        queue ??= new JavDbMetadataEventQueue();
        queue.SchemaVersion = CurrentSchemaVersion;
        queue.Items ??= new List<JavDbMetadataEventQueueItem>();
        queue.Items = queue.Items
            .Where(item => item != null && !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.UpdatedAtUtc)
                .First())
            .OrderBy(item => item.NextAttemptAtUtc)
            .ThenBy(item => item.EnqueuedAtUtc)
            .Take(MaxQueueItems)
            .ToList();
    }
}

internal sealed class JavDbMetadataEventQueue
{
    public int SchemaVersion { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public List<JavDbMetadataEventQueueItem> Items { get; set; } = new();
}

internal sealed class JavDbMetadataEventQueueItem
{
    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public bool Force { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset EnqueuedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset LastAttemptAtUtc { get; set; }

    public DateTimeOffset NextAttemptAtUtc { get; set; }

    public string LastError { get; set; } = string.Empty;
}
