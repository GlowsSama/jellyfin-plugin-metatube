using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
#if __EMBY__
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Logging;

#else
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public abstract class JavDbMetadataTaskBase : IScheduledTask
{
    private const string JavDbProvider = "JavDB";
    private const string StateFileName = "javdb-metadata-incremental-state.json";
    private const int MaxRecordsPerTable = 10000;
    private static readonly TimeSpan IncrementalLookback = TimeSpan.FromHours(48);
    private static readonly TimeSpan JavDbRequestInterval = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim JavDbRequestGate = new(1, 1);
    private static readonly SemaphoreSlim StateFileLock = new(1, 1);
    private static DateTimeOffset _lastJavDbRequestUtc = DateTimeOffset.MinValue;
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly Regex Fc2NumberPattern = new(
        @"(?i)(?<![A-Z0-9])FC2(?:[-_\s]?PPV)?[-_\s]?(?<number>\d{5,})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeparatedNumberPattern = new(
        @"(?i)(?<![A-Z0-9])(?<prefix>[A-Z0-9]{2,12})[-_\s](?<number>\d{2,6})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CompactNumberPattern = new(
        @"(?i)(?<![A-Z0-9])(?<prefix>[A-Z]{2,10})(?<number>\d{2,5})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumericDateNumberPattern = new(
        @"(?<!\d)(?<number>\d{6}[-_]\d{3})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly string _stateFilePath;

#if __EMBY__
    protected JavDbMetadataTaskBase(ILogManager logManager, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
    {
        _logger = logManager.CreateLogger<JavDbMetadataTaskBase>();
        _libraryManager = libraryManager;
        _stateFilePath = Path.Combine(applicationPaths.PluginConfigurationsPath, StateFileName);
    }
#else
    protected JavDbMetadataTaskBase(ILogger<JavDbMetadataTaskBase> logger, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _stateFilePath = Path.Combine(applicationPaths.PluginConfigurationsPath, StateFileName);
    }
#endif

    protected virtual bool ClearOnly => false;

    public abstract string Key { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public string Category => Plugin.ProviderName;

    public abstract IEnumerable<TaskTriggerInfo> GetDefaultTriggers();

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        await Task.Yield();

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            Recursive = true,
#if __EMBY__
            IncludeItemTypes = new[] { nameof(Movie) }
#else
            IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
        }).ToList();

        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        if (!ClearOnly && (state.Successes.Count > 0 || state.Failures.Count > 0))
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(IncrementalLookback);
            var failedItemIds = state.Failures
                .Select(record => record.ItemId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failedKeywords = state.Failures
                .Select(record => record.Keyword)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var successfulItemIds = state.Successes
                .Select(record => record.ItemId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var successfulKeywords = state.Successes
                .Select(record => record.Keyword)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            items = items
                .Where(item =>
                {
                    var itemId = item.Id.ToString("N");
                    var keyword = GetJavDbKeyword(item);
                    var isFailed = failedItemIds.Contains(itemId) ||
                                   (!string.IsNullOrWhiteSpace(keyword) && failedKeywords.Contains(keyword));
                    var isSuccessful = successfulItemIds.Contains(itemId) ||
                                       (!string.IsNullOrWhiteSpace(keyword) && successfulKeywords.Contains(keyword));

                    // A failed item is retried even when it was recently successful.
                    return isFailed ||
                           (GetDateCreatedUtc(item) >= cutoff && !isSuccessful);
                })
                .ToList();
        }

        var itemList = items
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        progress?.Report(0);

        if (ClearOnly)
        {
            await ClearMetadataAsync(itemList, progress, cancellationToken).ConfigureAwait(false);
            await ResetStateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var updated = 0;
        var failed = 0;
        for (var index = 0; index < itemList.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = itemList[index];
            progress?.Report(itemList.Count == 0 ? 100 : (double)index / itemList.Count * 100);

            var keyword = GetJavDbKeyword(item);
            if (string.IsNullOrWhiteSpace(keyword))
                continue;

            try
            {
                await ReplaceMetadataAsync(
                        item, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), cancellationToken)
                    .ConfigureAwait(false);

                await WaitForJavDbRequestSlotAsync(cancellationToken).ConfigureAwait(false);
                var info = await ApiClient.GetMovieInfoAsync(JavDbProvider, keyword, false, cancellationToken)
                    .ConfigureAwait(false);
                var genres = BuildGenres(info.Genres);
                var tags = BuildTags(info.Series);
                var studios = BuildStudios(info.Maker, info.Label);

                if (!HasSameValues(item.Genres, genres) ||
                    !HasSameValues(item.Tags, tags) ||
                    !HasSameValues(item.Studios, studios))
                {
                    _logger.Info("Update JAVDB genres, tags and studios for video: {0}", item.Name);
                    await ReplaceMetadataAsync(item, genres, tags, studios, cancellationToken)
                        .ConfigureAwait(false);
                    updated++;
                }

                await TryRecordOutcomeAsync(item, keyword, true, string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                _logger.Error("Update JAVDB genres, tags and studios for video {0} failed: {1}",
                    item.Name, exception.Message);
                await TryRecordOutcomeAsync(item, keyword, false, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _logger.Info("JAVDB metadata task completed: updated={0}, failed={1}, state={2}",
            updated, failed, _stateFilePath);
        progress?.Report(100);
    }

    private static async Task WaitForJavDbRequestSlotAsync(CancellationToken cancellationToken)
    {
        await JavDbRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastJavDbRequestUtc;
            if (elapsed < JavDbRequestInterval)
            {
                await Task.Delay(JavDbRequestInterval - elapsed, cancellationToken)
                    .ConfigureAwait(false);
            }

            _lastJavDbRequestUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            JavDbRequestGate.Release();
        }
    }

    private async Task ClearMetadataAsync(IReadOnlyList<BaseItem> items, IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var cleared = 0;
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(items.Count == 0 ? 100 : (double)index / items.Count * 100);

            var item = items[index];
            if ((item.Genres?.Length ?? 0) == 0 &&
                (item.Tags?.Length ?? 0) == 0 &&
                (item.Studios?.Length ?? 0) == 0)
            {
                continue;
            }

            await ReplaceMetadataAsync(
                    item, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), cancellationToken)
                .ConfigureAwait(false);
            cleared++;
        }

        _logger.Info("JAVDB metadata clear task completed: cleared={0}", cleared);
        progress?.Report(100);
    }

    private async Task TryRecordOutcomeAsync(BaseItem item, string keyword, bool success, string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordOutcomeAsync(item, keyword, success, message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Write JAVDB metadata task state failed: {0}", exception.Message);
        }
    }

    private async Task RecordOutcomeAsync(BaseItem item, string keyword, bool success, string message,
        CancellationToken cancellationToken)
    {
        await StateFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateFileAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            state.LastRunAtUtc = now;
            RemoveSameItem(state.Successes, item, keyword);
            RemoveSameItem(state.Failures, item, keyword);

            var record = new JavDbMetadataRecord
            {
                ItemId = item.Id.ToString("N"),
                Name = item.Name,
                Keyword = keyword,
                Message = success ? string.Empty : message ?? string.Empty,
                UpdatedAtUtc = now
            };
            if (success)
                state.Successes.Add(record);
            else
                state.Failures.Add(record);

            NormalizeState(state);
            await WriteStateFileAsync(state, cancellationToken).ConfigureAwait(false);
            await SaveStateToConfigurationAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StateFileLock.Release();
        }
    }

    private async Task ResetStateAsync(CancellationToken cancellationToken)
    {
        await StateFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = new JavDbMetadataState
            {
                LastRunAtUtc = DateTimeOffset.UtcNow
            };
            await WriteStateFileAsync(state, cancellationToken).ConfigureAwait(false);
            await SaveStateToConfigurationAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StateFileLock.Release();
        }
    }

    private async Task<JavDbMetadataState> LoadStateAsync(CancellationToken cancellationToken)
    {
        await StateFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = Plugin.Instance.Configuration;
            var fileState = await ReadStateFileAsync(cancellationToken).ConfigureAwait(false);
            var configurationState = ReadConfigurationState(configuration);
            var hasConfigurationRecords = configurationState.Successes.Count > 0 ||
                                          configurationState.Failures.Count > 0;
            var state = configuration.JavDbMetadataRecordsInitialized
                ? hasConfigurationRecords
                    ? configurationState
                    : new JavDbMetadataState()
                : hasConfigurationRecords
                    ? configurationState
                    : fileState;

            // The first run after upgrading imports the old failure-only state file.
            configuration.JavDbMetadataRecordsInitialized = true;
            NormalizeState(state);
            await WriteStateFileAsync(state, cancellationToken).ConfigureAwait(false);
            await SaveStateToConfigurationAsync(state, cancellationToken).ConfigureAwait(false);
            return state;
        }
        finally
        {
            StateFileLock.Release();
        }
    }

    private static JavDbMetadataState ReadConfigurationState(Configuration.PluginConfiguration configuration)
    {
        return new JavDbMetadataState
        {
            Successes = ParseRecords(configuration.JavDbMetadataSuccessRecords),
            Failures = ParseRecords(configuration.JavDbMetadataFailureRecords)
        };
    }

    private async Task SaveStateToConfigurationAsync(JavDbMetadataState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = Plugin.Instance.Configuration;
        configuration.JavDbMetadataSuccessRecords = SerializeRecords(state.Successes);
        configuration.JavDbMetadataFailureRecords = SerializeRecords(state.Failures);
        configuration.JavDbMetadataRecordsInitialized = true;
        Plugin.Instance.SaveConfiguration(configuration);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task WriteStateFileAsync(JavDbMetadataState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath) ?? ".");
        var temporaryPath = $"{_stateFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(state, StateJsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, _stateFilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<JavDbMetadataState> ReadStateFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
            return new JavDbMetadataState();

        try
        {
            var json = await File.ReadAllTextAsync(_stateFilePath, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<JavDbMetadataState>(json, StateJsonOptions)
                        ?? new JavDbMetadataState();
            if (state.Successes.Count == 0 && state.Failures.Count == 0 &&
                json.Contains("\"items\"", StringComparison.OrdinalIgnoreCase))
            {
                var legacy = JsonSerializer.Deserialize<LegacyJavDbIncrementalState>(json, StateJsonOptions);
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

            return state;
        }
        catch (JsonException exception)
        {
            _logger.Warn("JAVDB metadata task state file is invalid, starting a new one: {0}",
                exception.Message);
            return new JavDbMetadataState();
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

    private static void RemoveSameItem(List<JavDbMetadataRecord> records, BaseItem item, string keyword)
    {
        records.RemoveAll(record => IsSameItem(record, item, keyword));
    }

    private static bool IsSameItem(JavDbMetadataRecord record, BaseItem item, string keyword)
    {
        if (!string.IsNullOrWhiteSpace(record.ItemId))
            return string.Equals(record.ItemId, item.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);

        return string.Equals(record.Keyword, keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRecordKey(JavDbMetadataRecord record)
    {
        if (record == null)
            return string.Empty;

        return !string.IsNullOrWhiteSpace(record.ItemId)
            ? record.ItemId.Trim()
            : record.Keyword?.Trim() ?? string.Empty;
    }

    private static string SerializeRecords(IEnumerable<JavDbMetadataRecord> records)
    {
        return string.Join('\n', (records ?? Enumerable.Empty<JavDbMetadataRecord>())
            .Select(record => string.Join('\t',
                SanitizeRecordField(record.Keyword),
                SanitizeRecordField(record.Name),
                SanitizeRecordField(record.ItemId),
                record.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                SanitizeRecordField(record.Message))));
    }

    private static List<JavDbMetadataRecord> ParseRecords(string value)
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

        return NormalizeRecords(records);
    }

    private static string SanitizeRecordField(string value)
    {
        return (value ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    internal static string GetJavDbKeyword(BaseItem item)
    {
        var pid = item.GetPid(Plugin.ProviderId);
        if (pid.Provider.Equals(JavDbProvider, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pid.Id))
        {
            return pid.Id;
        }

        if (LooksLikeMovieNumber(pid.Id))
            return NormalizeMovieNumber(pid.Id);

        foreach (var value in new[] { item.Name, item.OriginalTitle, item.FileNameWithoutExtension })
        {
            var keyword = ExtractMovieNumber(value);
            if (!string.IsNullOrWhiteSpace(keyword))
                return keyword;
        }

        return string.Empty;
    }

    internal static string ExtractMovieNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var fc2Match = Fc2NumberPattern.Match(value);
        if (fc2Match.Success)
            return $"FC2-{fc2Match.Groups["number"].Value}";

        var numericDateMatch = NumericDateNumberPattern.Match(value);
        if (numericDateMatch.Success)
            return numericDateMatch.Groups["number"].Value.Replace('_', '-');

        foreach (Match separatedMatch in SeparatedNumberPattern.Matches(value))
        {
            var prefix = separatedMatch.Groups["prefix"].Value.ToUpperInvariant();
            var number = separatedMatch.Groups["number"].Value;
            if (!prefix.All(char.IsDigit) || (prefix.Length == 6 && number.Length == 3))
                return $"{prefix}-{number}";
        }

        var compactMatch = CompactNumberPattern.Match(value);
        if (compactMatch.Success)
        {
            return $"{compactMatch.Groups["prefix"].Value.ToUpperInvariant()}-" +
                   compactMatch.Groups["number"].Value;
        }

        return string.Empty;
    }

    private static bool LooksLikeMovieNumber(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               NormalizeMovieNumber(value) != string.Empty;
    }

    private static string NormalizeMovieNumber(string value)
    {
        var keyword = ExtractMovieNumber(value);
        if (!string.IsNullOrWhiteSpace(keyword))
            return keyword;

        var compact = value.Trim().Replace('_', '-').ToUpperInvariant();
        return Regex.IsMatch(compact, @"^[A-Z0-9]{2,12}-\d{2,6}$",
            RegexOptions.CultureInvariant)
            ? compact
            : string.Empty;
    }

    private async Task ReplaceMetadataAsync(
        BaseItem item,
        string[] genres,
        string[] tags,
        string[] studios,
        CancellationToken cancellationToken)
    {
        item.Genres = genres;
        item.Tags = tags;
        item.Studios = studios;
#if __EMBY__
        _libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
        await Task.CompletedTask.ConfigureAwait(false);
#else
        await _libraryManager
            .UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
#endif
    }

    private static string[] BuildGenres(IEnumerable<string> javDbGenres)
    {
        return CleanValues(javDbGenres).ToArray();
    }

    private static string[] BuildTags(params string[] values)
    {
        return CleanValues(values).ToArray();
    }

    private static string[] BuildStudios(params string[] values)
    {
        return CleanValues(values).ToArray();
    }

    private static IEnumerable<string> CleanValues(IEnumerable<string> values)
    {
        return (values ?? Enumerable.Empty<string>())
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasSameValues(IEnumerable<string> current, IEnumerable<string> expected)
    {
        return (current ?? Enumerable.Empty<string>())
            .SequenceEqual(expected ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    private static DateTimeOffset GetDateCreatedUtc(BaseItem item)
    {
        return item.DateCreated.ToUniversalTime();
    }
}

public sealed class JavDbMetadataCompleteTask : JavDbMetadataTaskBase
{
#if __EMBY__
    public JavDbMetadataCompleteTask(ILogManager logManager, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
        : base(logManager, libraryManager, applicationPaths)
    {
    }
#else
    public JavDbMetadataCompleteTask(ILogger<JavDbMetadataTaskBase> logger, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
        : base(logger, libraryManager, applicationPaths)
    {
    }
#endif

    public override string Key => $"{Plugin.ProviderName}JavDbMetadataComplete";

    public override string Name =>
#if __EMBY__
        "JAVDB 补全流派/标签/工作室数据 (beta)";
#else
        "JAVDB Complete Genres, Tags and Studios (beta)";
#endif

    public override string Description =>
#if __EMBY__
        "成功和失败记录都为空时扫描全部影片；否则处理最近 48 小时内入库的影片并重试失败记录。执行时先清空现有流派、标签和工作室，再写入 JAVDB 数据。记录可在插件设置中删除；删除两张记录表后，下次执行将自动全量更新。请求间隔至少 10 秒，主站拒绝请求时自动切换备用站点。";
#else
        "Scans all movies when both success and failure tables are empty; otherwise processes movies added in the last 48 hours and retries failures. Existing genres, tags and studios are cleared before JAVDB metadata is written. Clear both record tables in the plugin settings to force a full update. Requests are spaced by at least 10 seconds and fall back to configured backup domains when needed.";
#endif

    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
#if __EMBY__
            Type = TaskTriggerInfo.TriggerDaily,
#else
            Type = TaskTriggerInfoType.DailyTrigger,
#endif
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }
}

public sealed class JavDbMetadataClearTask : JavDbMetadataTaskBase
{
#if __EMBY__
    public JavDbMetadataClearTask(ILogManager logManager, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
        : base(logManager, libraryManager, applicationPaths)
    {
    }
#else
    public JavDbMetadataClearTask(ILogger<JavDbMetadataTaskBase> logger, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
        : base(logger, libraryManager, applicationPaths)
    {
    }
#endif

    protected override bool ClearOnly => true;

    public override string Key => $"{Plugin.ProviderName}JavDbMetadataClear";

    public override string Name =>
#if __EMBY__
        "JAVDB 清空流派/标签/工作室数据 (beta)";
#else
        "JAVDB Clear Genres, Tags and Studios (beta)";
#endif

    public override string Description =>
#if __EMBY__
        "一键删除全部影片的流派、标签和工作室，并清空 JAVDB 成功/失败记录。下次补全任务将执行全量更新。";
#else
        "Deletes genres, tags and studios from all movies and clears the JAVDB success/failure tables. The next completion task will run in full mode.";
#endif

    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Enumerable.Empty<TaskTriggerInfo>();
    }
}

public sealed class JavDbMetadataState
{
    public DateTimeOffset LastRunAtUtc { get; set; }

    public List<JavDbMetadataRecord> Successes { get; set; } = new();

    public List<JavDbMetadataRecord> Failures { get; set; } = new();
}

public sealed class JavDbMetadataRecord
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
