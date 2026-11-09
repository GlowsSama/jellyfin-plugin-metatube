using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

internal static class JavDbMetadataStateStore
{
    public const string StateFileName = "javdb-metadata-incremental-state.json";
    private const int MaxRecordsPerTable = 10000;
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<JavDbMetadataState> LoadAsync(
        string stateFilePath,
        Func<JavDbMetadataState> legacyStateFactory,
        CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(stateFilePath))
                return await ReadStateFileAsync(stateFilePath, cancellationToken).ConfigureAwait(false);

            var state = legacyStateFactory?.Invoke() ?? new JavDbMetadataState();
            NormalizeState(state);
            await WriteStateFileAsync(stateFilePath, state, cancellationToken).ConfigureAwait(false);
            return state;
        }
        finally
        {
            FileLock.Release();
        }
    }

    public static async Task SaveAsync(
        string stateFilePath,
        JavDbMetadataState state,
        CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NormalizeState(state);
            await WriteStateFileAsync(stateFilePath, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public static JavDbMetadataSummary GetSummary(
        string stateFilePath,
        Func<JavDbMetadataState> legacyStateFactory = null)
    {
        if (string.IsNullOrWhiteSpace(stateFilePath))
            return new JavDbMetadataSummary();

        FileLock.Wait();
        try
        {
            var state = File.Exists(stateFilePath)
                ? ReadStateFile(stateFilePath)
                : legacyStateFactory?.Invoke() ?? new JavDbMetadataState();

            if (!File.Exists(stateFilePath))
            {
                NormalizeState(state);
                WriteStateFile(stateFilePath, state);
            }

            return new JavDbMetadataSummary
            {
                SuccessCount = state.Successes.Count,
                FailureCount = state.Failures.Count,
                LastRunAtUtc = state.LastRunAtUtc
            };
        }
        finally
        {
            FileLock.Release();
        }
    }

    public static void Clear(string stateFilePath)
    {
        if (string.IsNullOrWhiteSpace(stateFilePath))
            return;

        FileLock.Wait();
        try
        {
            if (File.Exists(stateFilePath))
                File.Delete(stateFilePath);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public static List<JavDbMetadataRecord> ParseLegacyRecords(string value)
    {
        var records = new List<JavDbMetadataRecord>();
        foreach (var rawLine in (value ?? string.Empty).Split('\n'))
        {
            var fields = rawLine.TrimEnd('\r').Split('\t');
            if (fields.Length < 4 || string.IsNullOrWhiteSpace(fields[0]))
                continue;

            if (!DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var updatedAt))
                updatedAt = DateTimeOffset.MinValue;

            records.Add(new JavDbMetadataRecord
            {
                Keyword = fields[0].Trim(),
                Name = fields[1].Trim(),
                ItemId = fields[2].Trim(),
                UpdatedAtUtc = updatedAt,
                Message = fields.Length > 4 ? fields[4].Trim() : string.Empty
            });
        }

        return records
            .Where(record => !string.IsNullOrWhiteSpace(GetRecordKey(record)))
            .GroupBy(GetRecordKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.UpdatedAtUtc).First())
            .OrderByDescending(record => record.UpdatedAtUtc)
            .Take(MaxRecordsPerTable)
            .ToList();
    }

    private static async Task<JavDbMetadataState> ReadStateFileAsync(
        string stateFilePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(stateFilePath, cancellationToken).ConfigureAwait(false);
            return DeserializeState(json);
        }
        catch (JsonException)
        {
            return new JavDbMetadataState();
        }
    }

    private static JavDbMetadataState ReadStateFile(string stateFilePath)
    {
        try
        {
            return DeserializeState(File.ReadAllText(stateFilePath));
        }
        catch (JsonException)
        {
            return new JavDbMetadataState();
        }
    }

    private static JavDbMetadataState DeserializeState(string json)
    {
        var state = JsonSerializer.Deserialize<JavDbMetadataState>(json, JsonOptions)
                    ?? new JavDbMetadataState();
        if (state.Successes.Count == 0 && state.Failures.Count == 0 &&
            json.Contains("\"items\"", StringComparison.OrdinalIgnoreCase))
        {
            var legacy = JsonSerializer.Deserialize<LegacyJavDbIncrementalState>(json, JsonOptions);
            foreach (var item in legacy?.Items ?? Enumerable.Empty<JavDbIncrementalItemState>())
            {
                if (!string.Equals(item.Status, "failed", StringComparison.OrdinalIgnoreCase))
                    continue;

                state.Failures.Add(new JavDbMetadataRecord
                {
                    ItemId = item.ItemId,
                    Name = item.Name,
                    Keyword = item.Keyword,
                    Message = item.Message,
                    UpdatedAtUtc = item.UpdatedAtUtc
                });
            }
        }

        NormalizeState(state);
        return state;
    }

    private static async Task WriteStateFileAsync(
        string stateFilePath,
        JavDbMetadataState state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath) ?? ".");
        var temporaryPath = $"{stateFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, stateFilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void WriteStateFile(string stateFilePath, JavDbMetadataState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath) ?? ".");
        var temporaryPath = $"{stateFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(temporaryPath, json, Encoding.UTF8);
            File.Move(temporaryPath, stateFilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void NormalizeState(JavDbMetadataState state)
    {
        state.Successes = NormalizeRecords(state.Successes);
        state.Failures = NormalizeRecords(state.Failures);

        var failedKeys = state.Failures
            .Select(GetRecordKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        state.Successes = state.Successes
            .Where(record => !failedKeys.Contains(GetRecordKey(record)))
            .ToList();
    }

    private static List<JavDbMetadataRecord> NormalizeRecords(IEnumerable<JavDbMetadataRecord> records)
    {
        return (records ?? Enumerable.Empty<JavDbMetadataRecord>())
            .Where(record => record != null && !string.IsNullOrWhiteSpace(GetRecordKey(record)))
            .GroupBy(GetRecordKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(record => record.UpdatedAtUtc).First())
            .OrderByDescending(record => record.UpdatedAtUtc)
            .Take(MaxRecordsPerTable)
            .ToList();
    }

    private static string GetRecordKey(JavDbMetadataRecord record)
    {
        if (record == null)
            return string.Empty;

        return !string.IsNullOrWhiteSpace(record.ItemId)
            ? record.ItemId.Trim()
            : record.Keyword?.Trim() ?? string.Empty;
    }
}

internal sealed class JavDbMetadataSummary
{
    public int SuccessCount { get; set; }

    public int FailureCount { get; set; }

    public DateTimeOffset LastRunAtUtc { get; set; }
}

internal sealed class JavDbMetadataState
{
    public DateTimeOffset LastRunAtUtc { get; set; }

    public List<JavDbMetadataRecord> Successes { get; set; } = new();

    public List<JavDbMetadataRecord> Failures { get; set; } = new();
}

internal sealed class JavDbMetadataRecord
{
    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Keyword { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class LegacyJavDbIncrementalState
{
    public List<JavDbIncrementalItemState> Items { get; set; } = new();
}

internal sealed class JavDbIncrementalItemState
{
    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Keyword { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
