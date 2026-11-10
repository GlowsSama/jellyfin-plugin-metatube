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
    private const int StateSaveBatchSize = 25;
    private static readonly TimeSpan JavDbRequestInterval = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim JavDbRequestGate = new(1, 1);
    private static DateTimeOffset _lastJavDbRequestUtc = DateTimeOffset.MinValue;
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
        _stateFilePath = Path.Combine(applicationPaths.PluginConfigurationsPath,
            JavDbMetadataStateStore.StateFileName);
    }
#else
    protected JavDbMetadataTaskBase(ILogger<JavDbMetadataTaskBase> logger, ILibraryManager libraryManager,
        IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _stateFilePath = Path.Combine(applicationPaths.PluginConfigurationsPath,
            JavDbMetadataStateStore.StateFileName);
    }
#endif

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
        var totalItems = items.Count;

        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
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

                return isFailed || !isSuccessful;
            })
            .ToList();

        var itemList = items
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        var skipped = Math.Max(0, totalItems - itemList.Count);
        var mode = state.Successes.Count == 0 ? "全量" : "增量";
        _logger.Info(
            "JAVDB 元数据任务开始 | 模式={0} | 影片总数={1} | 待处理={2} | 跳过={3} | 成功记录={4} | 失败记录={5}",
            mode, totalItems, itemList.Count, skipped, state.Successes.Count, state.Failures.Count);
        progress?.Report(0);

        var updated = 0;
        var failed = 0;
        var pendingStateWrites = 0;
        state.LastRunAtUtc = DateTimeOffset.UtcNow;
        try
        {
            for (var index = 0; index < itemList.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = itemList[index];
                progress?.Report(itemList.Count == 0 ? 100 : (double)index / itemList.Count * 100);

                var keyword = GetJavDbKeyword(item);
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    failed++;
                    const string message = "无法从文件名、标题或原始标题识别番号";
                    _logger.Warn("JAVDB 元数据失败 | 项目ID={0} | 标题={1} | 错误={2}",
                        item.Id.ToString("N"), item.Name, message);
                    RecordOutcome(state, item, string.Empty, false, message);
                    pendingStateWrites++;
                    if (pendingStateWrites >= StateSaveBatchSize &&
                        await TrySaveStateAsync(state, cancellationToken).ConfigureAwait(false))
                    {
                        pendingStateWrites = 0;
                    }

                    continue;
                }

                try
                {
                    await ReplaceMetadataAsync(
                            item, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                            cancellationToken)
                        .ConfigureAwait(false);

                    await WaitForJavDbRequestSlotAsync(cancellationToken).ConfigureAwait(false);
                    var info = await ApiClient.GetMovieInfoAsync(JavDbProvider, keyword, false, cancellationToken)
                        .ConfigureAwait(false);
                    var genres = BuildGenres(info.Genres);
                    var tags = BuildTags(info.Series);
                    var studios = BuildStudios(info.Maker, info.Label);
                    var itemUpdated = false;

                    if (!HasSameValues(item.Genres, genres) ||
                        !HasSameValues(item.Tags, tags) ||
                        !HasSameValues(item.Studios, studios))
                    {
                        await ReplaceMetadataAsync(item, genres, tags, studios, cancellationToken)
                            .ConfigureAwait(false);
                        updated++;
                        itemUpdated = true;
                    }

                    RecordOutcome(state, item, keyword, true, string.Empty);
                    _logger.Info(
                        "JAVDB 元数据成功 | 番号={0} | 项目ID={1} | 标题={2} | 已更新={3}",
                        keyword, item.Id.ToString("N"), item.Name,
                        itemUpdated ? "是" : "否");
                    pendingStateWrites++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    _logger.Error(
                        "JAVDB 元数据失败 | 番号={0} | 项目ID={1} | 标题={2} | 错误={3}",
                        keyword, item.Id.ToString("N"), item.Name, exception.Message);
                    RecordOutcome(state, item, keyword, false, exception.Message);
                    pendingStateWrites++;
                }

                if (pendingStateWrites >= StateSaveBatchSize)
                {
                    if (await TrySaveStateAsync(state, cancellationToken).ConfigureAwait(false))
                        pendingStateWrites = 0;
                }
            }
        }
        finally
        {
            await TrySaveStateAsync(state, CancellationToken.None).ConfigureAwait(false);
        }

        _logger.Info(
            "JAVDB 元数据任务完成 | 模式={0} | 更新={1} | 失败={2} | 跳过={3} | 状态文件={4}",
            mode, updated, failed, skipped, _stateFilePath);
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

    private async Task<bool> TrySaveStateAsync(JavDbMetadataState state, CancellationToken cancellationToken)
    {
        try
        {
            await JavDbMetadataStateStore.SaveAsync(_stateFilePath, state, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            _logger.Error("Write JAVDB metadata task state failed: {0}", exception.Message);
            return false;
        }
    }

    private static void RecordOutcome(
        JavDbMetadataState state,
        BaseItem item,
        string keyword,
        bool success,
        string message)
    {
        var now = DateTimeOffset.UtcNow;
        state.LastRunAtUtc = now;
        state.Successes.RemoveAll(record => IsSameItem(record, item, keyword));
        state.Failures.RemoveAll(record => IsSameItem(record, item, keyword));

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
    }

    private async Task<JavDbMetadataState> LoadStateAsync(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var state = await JavDbMetadataStateStore.LoadAsync(
                _stateFilePath,
                () => ReadConfigurationState(configuration),
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(configuration.JavDbMetadataSuccessRecords) ||
            !string.IsNullOrWhiteSpace(configuration.JavDbMetadataFailureRecords) ||
            !configuration.JavDbMetadataRecordsInitialized)
        {
            configuration.JavDbMetadataSuccessRecords = string.Empty;
            configuration.JavDbMetadataFailureRecords = string.Empty;
            configuration.JavDbMetadataRecordsInitialized = true;
            Plugin.Instance.SaveConfiguration(configuration);
        }

        return state;
    }

    private static JavDbMetadataState ReadConfigurationState(Configuration.PluginConfiguration configuration)
    {
        return new JavDbMetadataState
        {
            Successes = JavDbMetadataStateStore.ParseLegacyRecords(configuration.JavDbMetadataSuccessRecords),
            Failures = JavDbMetadataStateStore.ParseLegacyRecords(configuration.JavDbMetadataFailureRecords)
        };
    }

    private static bool IsSameItem(JavDbMetadataRecord record, BaseItem item, string keyword)
    {
        if (!string.IsNullOrWhiteSpace(record.ItemId))
            return string.Equals(record.ItemId, item.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);

        return string.Equals(record.Keyword, keyword, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetJavDbKeyword(BaseItem item)
    {
        // Only JAVDB ids are valid movie numbers. Sources such as DMM/FANZA use padded ids
        // (e.g. ipz00377) that do not map directly to the real number (IPZ-377).
        var pid = item.GetPid(Plugin.ProviderId);
        if (pid.Provider.Equals(JavDbProvider, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pid.Id))
        {
            return pid.Id;
        }

        foreach (var value in new[] { item.FileNameWithoutExtension, item.Name, item.OriginalTitle })
        {
            var keyword = ExtractMovieNumber(value);
            if (!string.IsNullOrWhiteSpace(keyword))
                return keyword;
        }

        return string.Empty;
    }

    private static string TrimNumberPadding(string number)
    {
        // IPZ-00377 -> IPZ-377; keep at least 3 digits.
        var trimmed = number.TrimStart('0');
        return trimmed.Length >= 3 ? trimmed : trimmed.PadLeft(3, '0');
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
            {
                if (!prefix.All(char.IsDigit) && number.Length > 3)
                    number = TrimNumberPadding(number);
                return $"{prefix}-{number}";
            }
        }

        var compactMatch = CompactNumberPattern.Match(value);
        if (compactMatch.Success)
        {
            var compactNumber = compactMatch.Groups["number"].Value;
            if (compactNumber.Length > 3)
                compactNumber = TrimNumberPadding(compactNumber);
            return $"{compactMatch.Groups["prefix"].Value.ToUpperInvariant()}-{compactNumber}";
        }

        return string.Empty;
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
        "从JAVDB替换流派/标签/工作室数据";
#else
        "Replace Genres, Tags and Studios from JAVDB";
#endif

    public override string Description =>
#if __EMBY__
        "默认执行全量替换：先清空影片现有的流派、标签和工作室，再写入 JAVDB 数据。已有成功记录时按记录增量跳过，失败记录会自动重试；清空状态记录后，下一次任务会重新执行全量替换。逐条成功和失败结果会写入 Emby 日志。请求间隔至少 10 秒，主站拒绝请求时自动切换备用站点。";
#else
        "Runs a full replacement by default: existing genres, tags and studios are cleared before JAVDB metadata is written. Items present in the success state are skipped on later runs, while failures are retried automatically. Clearing the state causes the next run to perform a full replacement. Per-item success and failure results are written to the server log. Requests are spaced by at least 10 seconds and fall back to configured backup domains when needed.";
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
