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

    protected abstract bool IsIncremental { get; }

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

        JavDbIncrementalState incrementalState = null;
        if (IsIncremental)
        {
            incrementalState = await LoadIncrementalFailuresAsync(cancellationToken).ConfigureAwait(false);
            var cutoff = DateTimeOffset.UtcNow.Subtract(IncrementalLookback);
            var failedItemIds = incrementalState.Items
                .Select(record => record.ItemId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failedKeywords = incrementalState.Items
                .Select(record => record.Keyword)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            items = items
                .Where(item =>
                    GetDateCreatedUtc(item) >= cutoff ||
                    failedItemIds.Contains(item.Id.ToString("N")) ||
                    failedKeywords.Contains(GetJavDbKeyword(item)))
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

                await TryRemoveIncrementalFailureAsync(item, keyword, cancellationToken)
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
                await TryRecordIncrementalFailureAsync(item, keyword, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _logger.Info("JAVDB metadata task completed: updated={0}, failed={1}, state={2}",
            updated, failed, IsIncremental ? _stateFilePath : "not used for full task");
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

    private async Task TryRecordIncrementalFailureAsync(BaseItem item, string keyword,
        string message, CancellationToken cancellationToken)
    {
        if (!IsIncremental)
            return;

        try
        {
            await RecordIncrementalFailureAsync(item, keyword, message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Write JAVDB incremental task state failed: {0}", exception.Message);
        }
    }

    private async Task TryRemoveIncrementalFailureAsync(BaseItem item, string keyword,
        CancellationToken cancellationToken)
    {
        if (!IsIncremental)
            return;

        try
        {
            await RemoveIncrementalFailureAsync(item, keyword, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Update JAVDB incremental task state failed: {0}", exception.Message);
        }
    }

    private async Task RecordIncrementalFailureAsync(BaseItem item, string keyword,
        string message, CancellationToken cancellationToken)
    {
        await StateFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            state.LastRunAtUtc = DateTimeOffset.UtcNow;
            state.Items.RemoveAll(record => IsSameStateItem(record, item, keyword));
            state.Items.Add(new JavDbIncrementalItemState
            {
                ItemId = item.Id.ToString("N"),
                Name = item.Name,
                Keyword = keyword,
                Status = "failed",
                Message = message ?? string.Empty,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });

            state.Items = state.Items
                .OrderByDescending(record => record.UpdatedAtUtc)
                .Take(2000)
                .ToList();

            await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StateFileLock.Release();
        }
    }

    private async Task RemoveIncrementalFailureAsync(BaseItem item, string keyword,
        CancellationToken cancellationToken)
    {
        await StateFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            var removed = state.Items.RemoveAll(record => IsSameStateItem(record, item, keyword));
            if (removed == 0)
                return;

            state.LastRunAtUtc = DateTimeOffset.UtcNow;
            await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StateFileLock.Release();
        }
    }

    private async Task<JavDbIncrementalState> LoadIncrementalFailuresAsync(CancellationToken cancellationToken)
    {
        await StateFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            state.Items = state.Items
                .Where(IsFailureRecord)
                .GroupBy(
                    record => $"{record.ItemId ?? string.Empty}\n{record.Keyword ?? string.Empty}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(record => record.UpdatedAtUtc).First())
                .OrderByDescending(record => record.UpdatedAtUtc)
                .Take(2000)
                .ToList();
            state.LastRunAtUtc = DateTimeOffset.UtcNow;
            await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
            return state;
        }
        finally
        {
            StateFileLock.Release();
        }
    }

    private async Task WriteStateAsync(JavDbIncrementalState state, CancellationToken cancellationToken)
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

    private async Task<JavDbIncrementalState> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
            return new JavDbIncrementalState();

        try
        {
            var json = await File.ReadAllTextAsync(_stateFilePath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JavDbIncrementalState>(json, StateJsonOptions)
                   ?? new JavDbIncrementalState();
        }
        catch (JsonException exception)
        {
            _logger.Warn("JAVDB incremental task state file is invalid, starting a new one: {0}",
                exception.Message);
            return new JavDbIncrementalState();
        }
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

    private static bool IsFailureRecord(JavDbIncrementalItemState record)
    {
        return string.Equals(record.Status, "failed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameStateItem(JavDbIncrementalItemState record, BaseItem item, string keyword)
    {
        if (!string.IsNullOrWhiteSpace(record.ItemId))
        {
            return string.Equals(record.ItemId, item.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(record.Keyword, keyword, StringComparison.OrdinalIgnoreCase);
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

public sealed class JavDbMetadataFullTask : JavDbMetadataTaskBase
{
#if __EMBY__
    public JavDbMetadataFullTask(ILogManager logManager, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
        : base(logManager, libraryManager, applicationPaths)
    {
    }
#else
    public JavDbMetadataFullTask(ILogger<JavDbMetadataTaskBase> logger, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
        : base(logger, libraryManager, applicationPaths)
    {
    }
#endif

    protected override bool IsIncremental => false;

    public override string Key => $"{Plugin.ProviderName}JavDbMetadataFull";

    public override string Name =>
#if __EMBY__
        "JAVDB 重建流派、标签和工作室 (beta)";
#else
        "JAVDB Rebuild Genres, Tags and Studios (beta)";
#endif

    public override string Description =>
#if __EMBY__
        "手动扫描全部影片，用 JAVDB 数据重建流派、标签和工作室。请求间隔至少 10 秒，主站拒绝请求时自动切换备用站点。";
#else
        "Scans all movies and replaces genres, tags and studios with JAVDB metadata. Requests are spaced by at least 10 seconds and fall back to configured backup domains when needed.";
#endif

    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Enumerable.Empty<TaskTriggerInfo>();
    }
}

public sealed class JavDbMetadataIncrementalTask : JavDbMetadataTaskBase
{
#if __EMBY__
    public JavDbMetadataIncrementalTask(ILogManager logManager, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
        : base(logManager, libraryManager, applicationPaths)
    {
    }
#else
    public JavDbMetadataIncrementalTask(ILogger<JavDbMetadataTaskBase> logger, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
        : base(logger, libraryManager, applicationPaths)
    {
    }
#endif

    protected override bool IsIncremental => true;

    public override string Key => $"{Plugin.ProviderName}JavDbMetadataIncremental";

    public override string Name =>
#if __EMBY__
        "JAVDB 补全流派、标签和工作室 (beta)";
#else
        "JAVDB Complete Genres, Tags and Studios (beta)";
#endif

    public override string Description =>
#if __EMBY__
        "每天处理最近 48 小时内入库的影片，先清空现有流派、标签和工作室，再重试上次失败的项目并写入 JAVDB 数据。仅记录失败项目供下次重试；请求间隔至少 10 秒，主站拒绝请求时自动切换备用站点。";
#else
        "Processes movies added in the last 48 hours, clears existing genres, tags and studios, and then retries previously failed items with JAVDB metadata. Only failures are recorded for the next run. Requests are spaced by at least 10 seconds and fall back to configured backup domains when needed.";
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

    protected override bool IsIncremental => false;

    protected override bool ClearOnly => true;

    public override string Key => $"{Plugin.ProviderName}JavDbMetadataClear";

    public override string Name =>
#if __EMBY__
        "JAVDB 删除流派、标签和工作室 (beta)";
#else
        "JAVDB Delete Genres, Tags and Studios (beta)";
#endif

    public override string Description =>
#if __EMBY__
        "一键删除全部影片的流派、标签和工作室，不会修改其他元数据。";
#else
        "Deletes genres, tags and studios from all movies without changing other metadata.";
#endif

    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Enumerable.Empty<TaskTriggerInfo>();
    }
}

public sealed class JavDbIncrementalState
{
    public DateTimeOffset LastRunAtUtc { get; set; }

    public List<JavDbIncrementalItemState> Items { get; set; } = new();
}

public sealed class JavDbIncrementalItemState
{
    public string ItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Keyword { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
