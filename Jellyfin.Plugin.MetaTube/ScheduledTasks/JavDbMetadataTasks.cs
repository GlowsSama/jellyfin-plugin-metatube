using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Metadata;
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
    private const int MaxJavDbKeywordCandidates = 8;
    private static readonly SemaphoreSlim WorkflowGate = new(1, 1);
    private static readonly Regex Fc2NumberPattern = new(
        @"(?i)(?<![A-Z0-9])FC2(?:[-_\s]?PPV)?[-_\s]?(?<number>\d{5,})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TokyoHotNumberPattern = new(
        @"(?i)(?<![A-Z0-9])N[-_\s]?(?<number>\d{4})(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SpecialNumberPattern = new(
        @"(?i)(?<![A-Z0-9])(?<number>(?:HEYZO[-_\s]\d{4,6}|HEYDOUGA[-_\s]\d{4,6}[-_]\d{1,6}|MKBD[-_\s]?S\d{1,5}|MXNB[-_\s]\d{1,6}[A-Z]|\d{6}[-_]\d{3}))(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeparatedNumberPattern = new(
        @"(?i)(?<![A-Z0-9])(?<prefix>[A-Z0-9]{2,12})[-_\s](?<number>\d{2,6})(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CompactNumberPattern = new(
        @"(?i)(?<![A-Z0-9])(?<prefix>[A-Z]{2,10})(?<number>\d{2,5})(?![A-Z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DetailIdPattern = new(
        @"^(?=.*[A-Za-z])[A-Za-z0-9]{4,20}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> IgnoredNumberPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "KIRARI"
    };

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
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        return RunWorkflowExclusiveAsync(
            () => ExecuteCoreAsync(cancellationToken, progress),
            cancellationToken);
    }

    internal static async Task RunWorkflowExclusiveAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        await WorkflowGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            WorkflowGate.Release();
        }
    }

    private async Task ExecuteCoreAsync(CancellationToken cancellationToken, IProgress<double> progress)
    {
        await Task.Yield();

        var allItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            Recursive = true,
#if __EMBY__
            IncludeItemTypes = new[] { nameof(Movie) }
#else
            IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
        }).ToList();

        var configuration = Plugin.Instance.Configuration;
        var selection = JavDbReplacementSelection.FromConfiguration(configuration);
        var libraryScope = JavDbLibraryScope.Filter(
            _libraryManager, allItems, configuration.GetLibraryProviderFilters());
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        if (!selection.HasAny)
        {
            await MetadataOrganizer.OrganizeAsync(
                    libraryScope.Items, _libraryManager, _logger, cancellationToken, progress)
                .ConfigureAwait(false);
            state.LastRunAtUtc = DateTimeOffset.UtcNow;
            state.LastRunMode = nameof(JavDbMetadataSyncMode.Skipped);
            state.ReplacementFields = selection.Signature;
            await TrySaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            _logger.Warn("JAVDB 元数据任务已跳过 | 原因=未在插件设置中勾选任何替换项目");
            progress?.Report(100);
            return;
        }

        var invalidFailureCount = state.Failures.RemoveAll(record => string.IsNullOrWhiteSpace(record.Keyword));
        if (invalidFailureCount > 0)
        {
            _logger.Info("JAVDB 历史失败记录清理 | 移除无番号误记录={0}", invalidFailureCount);
        }

        if (!string.Equals(state.ReplacementFields, selection.Signature, StringComparison.Ordinal))
        {
            _logger.Info(
                "JAVDB 替换项目发生变化 | 上次={0} | 当前={1} | 将清除成功记录并重新计算待处理范围",
                state.ReplacementFields, selection.Signature);
            state.Successes.Clear();
            state.ReplacementFields = selection.Signature;
        }

        var plan = JavDbMetadataSyncPlanner.Create(
            libraryScope.Items, state, GetJavDbKeyword, selection);
        var clearedCompletedFailures = RemoveCompletedMetadataFailures(state, plan.CompletedMetadataItems);
        if (clearedCompletedFailures > 0)
        {
            _logger.Info(
                "JAVDB 已完成影片的失败记录清理 | 元数据齐全项目={0} | 移除失败记录={1}",
                plan.CompletedMetadataItems.Count, clearedCompletedFailures);
        }

        var itemList = plan.PendingItems;
        _logger.Info(
            "JAVDB 元数据任务开始 | 模式={0} | 原因={1} | 替换项目={2} | 全库影片={3} | 媒体库范围={4} | 范围内影片={5} | 媒体库范围跳过={6} | 无番号跳过={7} | 记录跳过={8} | 元数据齐全跳过={9} | 所选元数据缺失待补={10} | 待处理={11} | 成功记录={12} | 失败记录={13}",
            plan.ModeLabel, plan.ModeReason, selection.Signature, allItems.Count, libraryScope.Description,
            libraryScope.Items.Count, libraryScope.SkippedItemCount, plan.SkippedWithoutNumberCount,
            plan.SkippedByStateCount, plan.SkippedByCompletedMetadataCount,
            plan.PendingByMissingMetadataCount,
            itemList.Count, state.Successes.Count, state.Failures.Count);
        progress?.Report(0);

        var updated = 0;
        var failed = 0;
        var pendingStateWrites = 0;
        state.LastRunAtUtc = DateTimeOffset.UtcNow;
        state.LastRunMode = plan.Mode.ToString();

        try
        {
            for (var index = 0; index < itemList.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = itemList[index];
                progress?.Report(itemList.Count == 0 ? 85 : (double)index / itemList.Count * 85);
                var outcome = await ProcessSingleItemAsync(item, state, selection, cancellationToken)
                    .ConfigureAwait(false);
                if (outcome.Kind == JavDbMetadataItemOutcomeKind.Updated)
                    updated++;
                if (outcome.Kind is JavDbMetadataItemOutcomeKind.Failed
                    or JavDbMetadataItemOutcomeKind.NotFound)
                    failed++;
                if (outcome.Kind != JavDbMetadataItemOutcomeKind.Skipped)
                    pendingStateWrites++;

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

        var organized = await MetadataOrganizer.OrganizeAsync(
                libraryScope.Items, _libraryManager, _logger, cancellationToken, progress, 85, 100)
            .ConfigureAwait(false);
        _logger.Info(
            "JAVDB 元数据任务完成 | 模式={0} | 更新={1} | 失败={2} | 整理元数据={3} | 媒体库范围跳过={4} | 无番号跳过={5} | 记录跳过={6} | 元数据齐全跳过={7} | 状态文件={8}",
            plan.ModeLabel, updated, failed, organized, libraryScope.SkippedItemCount, plan.SkippedWithoutNumberCount,
            plan.SkippedByStateCount, plan.SkippedByCompletedMetadataCount, _stateFilePath);
        progress?.Report(100);
    }

    internal async Task<JavDbMetadataItemOutcome> ProcessSingleItemAsync(
        BaseItem item,
        JavDbMetadataState state,
        JavDbReplacementSelection selection,
        CancellationToken cancellationToken)
    {
        if (item == null || state == null || selection?.HasAny != true)
            return JavDbMetadataItemOutcome.Skipped();

        var keywords = GetJavDbKeywords(item);
        if (keywords.Count == 0)
        {
            _logger.Debug("JAVDB 元数据跳过 | 项目ID={0} | 标题={1} | 原因=无法识别番号",
                item.Id.ToString("N"), item.Name);
            return JavDbMetadataItemOutcome.Skipped();
        }

        var failureAlias = FindFailureAlias(state, item, keywords);
        var sourceKeyword = failureAlias == null
            ? keywords[0]
            : GetFailureSourceKeyword(failureAlias);
        if (failureAlias != null &&
            !string.IsNullOrWhiteSpace(failureAlias.Keyword))
        {
            var mappedKeyword = failureAlias.Keyword.Trim();
            keywords.RemoveAll(candidate =>
                string.Equals(candidate, mappedKeyword, StringComparison.OrdinalIgnoreCase));
            keywords.Insert(0, mappedKeyword);
        }

        var keyword = keywords[0];
        var original = CaptureSnapshot(item);
        var metadataCleared = false;
        try
        {
            var cleared = new JavDbMetadataSnapshot
            {
                Genres = selection.Genres ? Array.Empty<string>() : original.Genres,
                Tags = selection.Tags ? Array.Empty<string>() : original.Tags,
                Studios = selection.Studios ? Array.Empty<string>() : original.Studios,
                CommunityRating = selection.Rating ? null : original.CommunityRating
            };
            metadataCleared = true;
            await ApplyMetadataAsync(item, cleared, selection, cancellationToken).ConfigureAwait(false);

            _logger.Info(
                "JAVDB 番号候选 | 项目ID={0} | 标题={1} | 候选={2}",
                item.Id.ToString("N"), item.Name, string.Join(" | ", keywords));

            MovieInfo info = null;
            var matchedKeyword = keyword;
            foreach (var candidate in keywords)
            {
                _logger.Info("JAVDB 候选请求 | 项目ID={0} | 番号={1}",
                    item.Id.ToString("N"), candidate);
                try
                {
                    info = await JavDbRequestScheduler.RunAsync(
                            () => ApiClient.GetMovieInfoAsync(JavDbProvider, candidate, false, cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);
                    matchedKeyword = candidate;
                    _logger.Info("JAVDB 候选命中 | 项目ID={0} | 番号={1} | 结果ID={2}",
                        item.Id.ToString("N"), candidate, info.Id);
                    break;
                }
                catch (ApiRequestException exception) when (exception.IsNotFound)
                {
                    _logger.Info("JAVDB 候选未找到 | 项目ID={0} | 番号={1} | 继续下一候选",
                        item.Id.ToString("N"), candidate);
                }
            }

            if (info == null)
            {
                var title = GetJavDbTitle(item);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    _logger.Info("JAVDB 标题兜底开始 | 项目ID={0} | 标题={1}",
                        item.Id.ToString("N"), title);
                    List<MovieSearchResult> titleResults;
                    try
                    {
                        titleResults = await JavDbRequestScheduler.RunAsync(
                                () => ApiClient.SearchMovieAsync(title, JavDbProvider, false, cancellationToken),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (ApiRequestException exception) when (exception.IsNotFound)
                    {
                        titleResults = new List<MovieSearchResult>();
                    }

                    var titleMatch = SelectConfidentTitleMatch(title, titleResults);
                    if (titleMatch != null)
                    {
                        var titleMatchId = !string.IsNullOrWhiteSpace(titleMatch.Id)
                            ? titleMatch.Id
                            : titleMatch.Number;
                        _logger.Info(
                            "JAVDB 标题兜底命中 | 项目ID={0} | 标题={1} | 结果ID={2} | 番号={3}",
                            item.Id.ToString("N"), title, titleMatchId, titleMatch.Number);
                        try
                        {
                            info = await JavDbRequestScheduler.RunAsync(
                                    () => ApiClient.GetMovieInfoAsync(JavDbProvider, titleMatchId, false,
                                        cancellationToken),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            matchedKeyword = string.IsNullOrWhiteSpace(titleMatch.Number)
                                ? titleMatchId
                                : titleMatch.Number;
                        }
                        catch (ApiRequestException exception) when (exception.IsNotFound)
                        {
                            _logger.Info("JAVDB 标题兜底结果已失效 | 项目ID={0} | 结果ID={1}",
                                item.Id.ToString("N"), titleMatchId);
                        }
                    }
                    else
                    {
                        _logger.Info(
                            "JAVDB 标题兜底未采用 | 项目ID={0} | 标题={1} | 原因=无唯一高置信度结果 | 结果数={2}",
                            item.Id.ToString("N"), title, titleResults.Count);
                    }
                }
            }

            if (info == null)
            {
                await TryRestoreMetadataAsync(item, original, selection).ConfigureAwait(false);
                metadataCleared = false;
                var failureMessage = $"not_found: JAVDB 未找到候选 [{string.Join(", ", keywords)}]";
                _logger.Warn(
                    "JAVDB 元数据未找到 | 项目ID={0} | 标题={1} | 候选={2} | 已恢复原元数据",
                    item.Id.ToString("N"), item.Name, string.Join(" | ", keywords));
                RecordOutcome(state, item, sourceKeyword, keyword, false, "not_found", failureMessage);
                return JavDbMetadataItemOutcome.NotFound(keyword, failureMessage);
            }

            keyword = matchedKeyword;
            var desired = new JavDbMetadataSnapshot
            {
                Genres = selection.Genres ? BuildGenres(info.Genres) : original.Genres,
                Tags = selection.Tags ? BuildTags(info.Series) : original.Tags,
                Studios = selection.Studios ? BuildStudios(info.Maker, info.Label) : original.Studios,
                CommunityRating = selection.Rating ? BuildRating(info.Score) : original.CommunityRating
            };
            var current = CaptureSnapshot(item);
            var needsApply = HasChanges(current, desired, selection);
            var itemUpdated = HasChanges(original, desired, selection);

            if (needsApply)
                await ApplyMetadataAsync(item, desired, selection, cancellationToken).ConfigureAwait(false);

            RecordOutcome(state, item, sourceKeyword, keyword, true, string.Empty, string.Empty);
            _logger.Info(
                "JAVDB 元数据成功 | 番号={0} | 项目ID={1} | 标题={2} | 替换项目={3} | 已更新={4}",
                keyword, item.Id.ToString("N"), item.Name, selection.Signature,
                itemUpdated ? "是" : "否");
            return itemUpdated
                ? JavDbMetadataItemOutcome.Updated(keyword)
                : JavDbMetadataItemOutcome.Unchanged(keyword);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (metadataCleared)
                await TryRestoreMetadataAsync(item, original, selection).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            if (metadataCleared)
                await TryRestoreMetadataAsync(item, original, selection).ConfigureAwait(false);

            var failureKind = ClassifyJavDbFailure(exception);
            var failureMessage = $"{failureKind}: {exception.Message}";
            _logger.Error(
                "JAVDB 元数据失败 | 番号={0} | 项目ID={1} | 标题={2} | 类型={3} | 错误={4}",
                keyword, item.Id.ToString("N"), item.Name, failureKind, exception.Message);
            RecordOutcome(state, item, sourceKeyword, keyword, false, failureKind, failureMessage);
            return JavDbMetadataItemOutcome.Failed(keyword, failureKind, failureMessage);
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

    internal Task<JavDbMetadataState> LoadStateForEventAsync(CancellationToken cancellationToken)
    {
        return LoadStateAsync(cancellationToken);
    }

    internal Task<bool> SaveStateForEventAsync(
        JavDbMetadataState state,
        CancellationToken cancellationToken)
    {
        return TrySaveStateAsync(state, cancellationToken);
    }

    private static int RemoveCompletedMetadataFailures(
        JavDbMetadataState state,
        IEnumerable<BaseItem> completedItems)
    {
        var items = (completedItems ?? Enumerable.Empty<BaseItem>()).ToList();
        if (state?.Failures == null || items.Count == 0)
            return 0;

        var itemIds = items
            .Select(item => item.Id.ToString("N"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keywords = items
            .SelectMany(GetJavDbKeywords)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return state.Failures.RemoveAll(record =>
            (!string.IsNullOrWhiteSpace(record.ItemId) && itemIds.Contains(record.ItemId)) ||
            (!string.IsNullOrWhiteSpace(record.Keyword) && keywords.Contains(record.Keyword)) ||
            (!string.IsNullOrWhiteSpace(record.SourceKeyword) && keywords.Contains(record.SourceKeyword)));
    }

    private static void RecordOutcome(
        JavDbMetadataState state,
        BaseItem item,
        string sourceKeyword,
        string effectiveKeyword,
        bool success,
        string failureKind,
        string message)
    {
        var now = DateTimeOffset.UtcNow;
        state.LastRunAtUtc = now;
        state.Successes.RemoveAll(record => IsSameItem(record, item, sourceKeyword));
        state.Failures.RemoveAll(record => IsSameItem(record, item, sourceKeyword));

        var record = new JavDbMetadataRecord
        {
            ItemId = item.Id.ToString("N"),
            Name = item.Name,
            SourceKeyword = sourceKeyword,
            Keyword = effectiveKeyword,
            Message = success ? string.Empty : message ?? string.Empty,
            FailureKind = success ? string.Empty : failureKind ?? string.Empty,
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

        if (configuration.JavDbMetadataFailureEditorDirty)
        {
            var result = JavDbMetadataStateStore.ApplyFailureEditor(
                state, configuration.JavDbMetadataFailureEditor);
            try
            {
                await JavDbMetadataStateStore.SaveAsync(_stateFilePath, state, cancellationToken)
                    .ConfigureAwait(false);
                configuration.JavDbMetadataFailureEditor = string.Empty;
                configuration.JavDbMetadataFailureEditorDirty = false;
                Plugin.Instance.SaveConfiguration(configuration);
                _logger.Info(
                    "JAVDB 失败任务清单已应用 | 新增任务={0} | 修改映射={1} | 标记完成={2} | 保持不变={3}",
                    result.Added, result.Updated, result.Completed, result.Unchanged);
            }
            catch (Exception exception)
            {
                _logger.Error("JAVDB 失败任务清单应用失败 | 错误={0}", exception.Message);
            }
        }

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

    private static JavDbMetadataRecord FindFailureAlias(
        JavDbMetadataState state,
        BaseItem item,
        IReadOnlyCollection<string> keywords)
    {
        var itemId = item.Id.ToString("N");
        var byItemId = state.Failures.FirstOrDefault(record =>
            string.Equals(record.ItemId, itemId, StringComparison.OrdinalIgnoreCase));
        if (byItemId != null)
            return byItemId;

        return state.Failures.FirstOrDefault(record =>
            keywords.Any(keyword =>
                string.Equals(record.Keyword, keyword, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(record.SourceKeyword, keyword, StringComparison.OrdinalIgnoreCase)));
    }

    private static string GetFailureSourceKeyword(JavDbMetadataRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.SourceKeyword))
            return record.SourceKeyword.Trim();
        return record.Keyword?.Trim() ?? string.Empty;
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

        return string.Equals(record.Keyword, keyword, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(record.SourceKeyword, keyword, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetJavDbKeyword(BaseItem item)
    {
        return GetJavDbKeywords(item).FirstOrDefault() ?? string.Empty;
    }

    internal static List<string> GetJavDbKeywords(BaseItem item)
    {
        var candidates = new List<string>();
        foreach (var value in new[] { item.Name, item.OriginalTitle, item.FileNameWithoutExtension })
        {
            foreach (var keyword in ExtractMovieNumbers(value))
                AddCandidate(candidates, keyword);
        }

        var pid = item.GetPid(Plugin.ProviderId);
        var providerId = pid.Id?.Trim() ?? string.Empty;
        if (DetailIdPattern.IsMatch(providerId) &&
            !IgnoredNumberPrefixes.Any(prefix =>
                providerId.StartsWith($"{prefix}-", StringComparison.OrdinalIgnoreCase)))
        {
            AddCandidate(candidates, providerId);
        }

        return candidates.Take(MaxJavDbKeywordCandidates).ToList();
    }

    internal static string ExtractMovieNumber(string value)
    {
        return ExtractMovieNumbers(value).FirstOrDefault() ?? string.Empty;
    }

    internal static List<string> ExtractMovieNumbers(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();

        var candidates = new List<string>();
        var fc2Match = Fc2NumberPattern.Match(value);
        if (fc2Match.Success)
            AddNumberVariants(candidates, $"FC2-{fc2Match.Groups["number"].Value}");

        var tokyoHotMatch = TokyoHotNumberPattern.Match(value);
        if (tokyoHotMatch.Success)
            AddNumberVariants(candidates, $"N{tokyoHotMatch.Groups["number"].Value}");

        var specialMatches = SpecialNumberPattern.Matches(value).Cast<Match>().ToList();
        foreach (var specialMatch in specialMatches)
        {
            var raw = specialMatch.Groups["number"].Value;
            AddNumberVariants(candidates, raw, Regex.IsMatch(raw, @"^\d{6}[-_]\d{3}$"));
        }

        foreach (Match separatedMatch in SeparatedNumberPattern.Matches(value))
        {
            if (specialMatches.Any(specialMatch =>
                    separatedMatch.Index >= specialMatch.Index &&
                    separatedMatch.Index + separatedMatch.Length <= specialMatch.Index + specialMatch.Length))
            {
                continue;
            }

            var prefix = separatedMatch.Groups["prefix"].Value.ToUpperInvariant();
            if (IgnoredNumberPrefixes.Contains(prefix))
                continue;

            var number = separatedMatch.Groups["number"].Value;
            AddNumberVariants(candidates, $"{prefix}-{number}");
        }

        foreach (Match compactMatch in CompactNumberPattern.Matches(value))
        {
            var prefix = compactMatch.Groups["prefix"].Value.ToUpperInvariant();
            if (IgnoredNumberPrefixes.Contains(prefix))
                continue;

            var compactNumber = compactMatch.Groups["number"].Value;
            AddNumberVariants(candidates, $"{prefix}-{compactNumber}");
        }

        return candidates;
    }

    private static void AddNumberVariants(List<string> candidates, string raw, bool isDateNumber = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        var normalized = Regex.Replace(raw.Trim().ToUpperInvariant(), @"\s+", "-");
        normalized = isDateNumber
            ? Regex.Replace(normalized, @"[^0-9_-]", string.Empty)
            : Regex.Replace(normalized, @"[^A-Z0-9_-]", string.Empty).Replace('_', '-');
        normalized = Regex.Replace(normalized, @"-{2,}", "-").Trim('-', '_');
        AddCandidate(candidates, normalized);

        if (isDateNumber)
        {
            AddCandidate(candidates, normalized.Replace('_', '-'));
            return;
        }

        var match = Regex.Match(normalized,
            @"^(?<prefix>[A-Z0-9]{2,12})-(?<number>\d{2,6})(?<suffix>[A-Z]?)$");
        if (!match.Success || match.Groups["prefix"].Value == "FC2")
            return;

        var number = match.Groups["number"].Value;
        if (number.Length <= 3)
            return;

        var trimmed = number.TrimStart('0');
        if (trimmed.Length == 0)
            trimmed = "0";
        if (!string.Equals(trimmed, number, StringComparison.Ordinal))
        {
            AddCandidate(candidates,
                $"{match.Groups["prefix"].Value}-{trimmed}{match.Groups["suffix"].Value}");
        }
    }

    private static void AddCandidate(List<string> candidates, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;
        if (candidates.Any(value => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)))
            return;
        candidates.Add(candidate);
    }

    private static string GetJavDbTitle(BaseItem item)
    {
        foreach (var value in new[] { item.Name, item.OriginalTitle, item.FileNameWithoutExtension })
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static MovieSearchResult SelectConfidentTitleMatch(string title,
        IEnumerable<MovieSearchResult> searchResults)
    {
        var queryNumbers = ExtractMovieNumbers(title);
        var scored = (searchResults ?? Enumerable.Empty<MovieSearchResult>())
            .Where(result => result != null)
            .Select(result => new
            {
                Result = result,
                Score = GetTitleSimilarity(title, result.Title),
                NumberMatches = queryNumbers.Any(number => AreEquivalentMovieNumbers(number, result.Number))
            })
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Result.Id, StringComparer.Ordinal)
            .ToList();

        if (scored.Count == 0)
            return null;

        var best = scored[0];
        if (best.Score < 0.72)
            return null;
        if (!best.NumberMatches && best.Score < 0.92)
            return null;

        var runnerUp = scored.Skip(1)
            .FirstOrDefault(value => !AreEquivalentMovieNumbers(best.Result.Number, value.Result.Number));
        if (runnerUp != null && best.Score - runnerUp.Score < 0.12)
            return null;

        return best.Result;
    }

    private static double GetTitleSimilarity(string left, string right)
    {
        var leftKey = NormalizeTitleKey(left);
        var rightKey = NormalizeTitleKey(right);
        if (leftKey.Length == 0 || rightKey.Length == 0)
            return 0;
        if (string.Equals(leftKey, rightKey, StringComparison.Ordinal))
            return 1;
        if (Math.Min(leftKey.Length, rightKey.Length) >= 8 &&
            (leftKey.Contains(rightKey, StringComparison.Ordinal) ||
             rightKey.Contains(leftKey, StringComparison.Ordinal)))
        {
            return 0.94;
        }

        var leftBigrams = GetTitleBigrams(leftKey);
        var rightBigrams = GetTitleBigrams(rightKey);
        if (leftBigrams.Count == 0 || rightBigrams.Count == 0)
            return 0;

        var common = leftBigrams.Count(rightBigrams.Contains);
        return 2d * common / (leftBigrams.Count + rightBigrams.Count);
    }

    private static string NormalizeTitleKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
        }

        return builder.ToString();
    }

    private static HashSet<string> GetTitleBigrams(string value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < value.Length; index++)
            result.Add(value.Substring(index, 2));
        return result;
    }

    private static bool AreEquivalentMovieNumbers(string left, string right)
    {
        var leftKey = NormalizeNumberKey(left);
        var rightKey = NormalizeNumberKey(right);
        if (leftKey.Length == 0 || rightKey.Length == 0)
            return false;
        if (string.Equals(leftKey, rightKey, StringComparison.Ordinal))
            return true;
        if (string.Equals(leftKey.Replace('_', '-'), rightKey.Replace('_', '-'),
                StringComparison.Ordinal))
        {
            return true;
        }

        var leftMatch = Regex.Match(leftKey, @"^(?<prefix>[A-Z]+)-(?<number>\d+)(?<suffix>[A-Z]?)$");
        var rightMatch = Regex.Match(rightKey, @"^(?<prefix>[A-Z]+)-(?<number>\d+)(?<suffix>[A-Z]?)$");
        if (leftMatch.Success && rightMatch.Success)
        {
            return string.Equals(leftMatch.Groups["prefix"].Value,
                       rightMatch.Groups["prefix"].Value, StringComparison.Ordinal) &&
                   string.Equals(TrimLeadingZeros(leftMatch.Groups["number"].Value),
                       TrimLeadingZeros(rightMatch.Groups["number"].Value), StringComparison.Ordinal) &&
                   string.Equals(leftMatch.Groups["suffix"].Value,
                       rightMatch.Groups["suffix"].Value, StringComparison.Ordinal);
        }

        return string.Equals(leftKey.Replace("-", string.Empty).Replace("_", string.Empty),
            rightKey.Replace("-", string.Empty).Replace("_", string.Empty),
            StringComparison.Ordinal);
    }

    private static string NormalizeNumberKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var normalized = Regex.Replace(value.Trim().ToUpperInvariant(), @"\s+", "-");
        normalized = Regex.Replace(normalized, @"-{2,}", "-").Trim('-', '_');
        return normalized;
    }

    private static string TrimLeadingZeros(string value)
    {
        var trimmed = value.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    private static string ClassifyJavDbFailure(Exception exception)
    {
        var message = exception?.Message ?? string.Empty;
        if (exception is ApiRequestException apiException)
        {
            if (apiException.IsNotFound ||
                message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("info not found", StringComparison.OrdinalIgnoreCase))
            {
                return "not_found";
            }

            if (message.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("cooldown", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("限流", StringComparison.Ordinal) ||
                message.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
                apiException.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return "rate_limit";
            }

            if (apiException.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
                apiException.ApiErrorCode is 401 or 403 ||
                message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("登录", StringComparison.Ordinal))
            {
                return "auth";
            }

            if ((int)apiException.StatusCode >= 500 || apiException.ApiErrorCode >= 500)
                return "server";
            if (apiException.StatusCode == HttpStatusCode.BadRequest ||
                message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("keyword", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("番号", StringComparison.Ordinal))
            {
                return "number_mismatch";
            }
        }

        if (exception is HttpRequestException or TaskCanceledException ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("network", StringComparison.OrdinalIgnoreCase))
        {
            return "network";
        }
        if (message.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cooldown", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("限流", StringComparison.Ordinal) ||
            message.Contains("too many", StringComparison.OrdinalIgnoreCase))
        {
            return "rate_limit";
        }
        if (message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("登录", StringComparison.Ordinal))
        {
            return "auth";
        }
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("info not found", StringComparison.OrdinalIgnoreCase))
        {
            return "not_found";
        }
        if (message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("keyword", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("番号", StringComparison.Ordinal))
        {
            return "number_mismatch";
        }
        if (message.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("anti-bot", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("server", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "server";
        }

        return "unknown";
    }

    private static JavDbMetadataSnapshot CaptureSnapshot(BaseItem item)
    {
        return new JavDbMetadataSnapshot
        {
            Genres = CloneValues(item.Genres),
            Tags = CloneValues(item.Tags),
            Studios = CloneValues(item.Studios),
            CommunityRating = item.CommunityRating
        };
    }

    private async Task ApplyMetadataAsync(
        BaseItem item,
        JavDbMetadataSnapshot target,
        JavDbReplacementSelection selection,
        CancellationToken cancellationToken)
    {
        var original = CaptureSnapshot(item);
        try
        {
            JavDbMetadataEventSuppressor.Suppress(item.Id, TimeSpan.FromSeconds(45));
            if (selection.Genres)
                item.Genres = CloneValues(target.Genres);
            if (selection.Tags)
                item.Tags = CloneValues(target.Tags);
            if (selection.Studios)
                item.Studios = CloneValues(target.Studios);
            if (selection.Rating)
                item.CommunityRating = target.CommunityRating;

#if __EMBY__
            if (selection.HasAny)
            {
                _libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
            }

            await Task.CompletedTask.ConfigureAwait(false);
#else
            if (selection.HasAny)
            {
                await _libraryManager
                    .UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
            }
#endif
        }
        catch
        {
            RestoreInMemory(item, original, selection);
            throw;
        }
    }

    private async Task TryRestoreMetadataAsync(
        BaseItem item,
        JavDbMetadataSnapshot snapshot,
        JavDbReplacementSelection selection)
    {
        try
        {
            await ApplyMetadataAsync(item, snapshot, selection, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "JAVDB 元数据恢复失败 | 项目ID={0} | 标题={1} | 错误={2}",
                item.Id.ToString("N"), item.Name, exception.Message);
        }
    }

    private static void RestoreInMemory(
        BaseItem item,
        JavDbMetadataSnapshot snapshot,
        JavDbReplacementSelection selection)
    {
        if (selection.Genres)
            item.Genres = CloneValues(snapshot.Genres);
        if (selection.Tags)
            item.Tags = CloneValues(snapshot.Tags);
        if (selection.Studios)
            item.Studios = CloneValues(snapshot.Studios);
        if (selection.Rating)
            item.CommunityRating = snapshot.CommunityRating;
    }

    private static bool HasChanges(
        JavDbMetadataSnapshot original,
        JavDbMetadataSnapshot desired,
        JavDbReplacementSelection selection)
    {
        return (selection.Genres && !HasSameValues(original.Genres, desired.Genres)) ||
               (selection.Tags && !HasSameValues(original.Tags, desired.Tags)) ||
               (selection.Studios && !HasSameValues(original.Studios, desired.Studios)) ||
               (selection.Rating && original.CommunityRating != desired.CommunityRating);
    }

    private static string[] CloneValues(IEnumerable<string> values)
    {
        return (values ?? Enumerable.Empty<string>()).ToArray();
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

    private static float? BuildRating(float score)
    {
        return score > 0 ? (float?)Math.Round(score * 2, 1) : null;
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

    private sealed class JavDbMetadataSnapshot
    {
        public string[] Genres { get; init; } = Array.Empty<string>();

        public string[] Tags { get; init; } = Array.Empty<string>();

        public string[] Studios { get; init; } = Array.Empty<string>();

        public float? CommunityRating { get; init; }
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
        // Emby orders scheduled tasks by Name. Leading spaces control display order without changing the visible title.
        "\u0020\u0020\u0020补充其他元数据";
#else
        "Replace Other Metadata from JAVDB";
#endif

    public override string Description =>
#if __EMBY__
        "按插件设置从 JAVDB 补充其他元数据，并整理流派、角标和排序。勾选“流派”时以流派字段判断：流派为空视为待补，流派非空跳过；如需重新处理全部影片，请先运行“删除其它元数据”。请求间隔至少 10 秒。";
#else
        "Manual maintenance task. The task replaces selected metadata from JAVDB, then organizes Chinese subtitle genres, cover badges, genre substitutions, deduplication and sorting. New items are normally processed by the event queue; missed or failed items can be processed by running this task. Valid library rules limit the task to those libraries; when no rules are configured, all libraries are processed. Items without a recognizable movie number are skipped without a JAVDB request. JAVDB requests are serialized with at least 10 seconds between requests; other providers are not affected.";
#endif

    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Automatic processing is handled by the event queue and nightly fallback scan.
        yield break;
    }
}
