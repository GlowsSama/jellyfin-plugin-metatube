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

#if __EMBY__
using JavDbPremiereDate = System.DateTimeOffset;
#else
using JavDbPremiereDate = System.DateTime;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public abstract class JavDbMetadataTaskBase : IScheduledTask
{
    private const string JavDbProvider = "JavDB";
    private const int StateSaveBatchSize = 25;
    private static readonly TimeSpan JavDbTaskInterval = TimeSpan.FromSeconds(10);
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
    private static readonly Regex PersonNameSeparatorPattern = new(
        @"\s*[,，、;/；|｜]\s*",
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
        var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        if (!selection.HasAny)
        {
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
                "JAVDB 替换项目发生变化 | 上次={0} | 当前={1} | 将清除成功记录并执行全量处理",
                state.ReplacementFields, selection.Signature);
            state.Successes.Clear();
            state.ReplacementFields = selection.Signature;
        }

        var libraryScope = JavDbLibraryScope.Filter(
            _libraryManager, allItems, configuration.GetLibraryProviderFilters());
        var plan = JavDbMetadataSyncPlanner.Create(libraryScope.Items, state, GetJavDbKeyword);
        var itemList = plan.PendingItems;
        _logger.Info(
            "JAVDB 元数据任务开始 | 模式={0} | 原因={1} | 替换项目={2} | 全库影片={3} | 媒体库范围={4} | 范围内影片={5} | 媒体库范围跳过={6} | 无番号跳过={7} | 记录跳过={8} | 待处理={9} | 成功记录={10} | 失败记录={11}",
            plan.ModeLabel, plan.ModeReason, selection.Signature, allItems.Count, libraryScope.Description,
            libraryScope.Items.Count, libraryScope.SkippedItemCount, plan.SkippedWithoutNumberCount,
            plan.SkippedByStateCount, itemList.Count, state.Successes.Count, state.Failures.Count);
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
                progress?.Report(itemList.Count == 0 ? 100 : (double)index / itemList.Count * 100);

                var keyword = GetJavDbKeyword(item);
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    _logger.Debug("JAVDB 元数据跳过 | 项目ID={0} | 标题={1} | 原因=无法识别番号",
                        item.Id.ToString("N"), item.Name);
                    continue;
                }

                var original = CaptureSnapshot(item, selection.HasPeople);
                var metadataCleared = false;
                var javDbRequested = false;
                try
                {
                    var cleared = new JavDbMetadataSnapshot
                    {
                        Genres = selection.Genres ? Array.Empty<string>() : original.Genres,
                        Tags = selection.Tags ? Array.Empty<string>() : original.Tags,
                        Studios = selection.Studios ? Array.Empty<string>() : original.Studios,
                        CommunityRating = selection.Rating ? null : original.CommunityRating,
                        PremiereDate = selection.ReleaseDate ? null : original.PremiereDate,
                        ProductionYear = selection.ReleaseDate ? null : original.ProductionYear,
                        People = selection.HasPeople ? RemoveSelectedPeople(original.People, selection) : null
                    };
                    metadataCleared = true;
                    await ApplyMetadataAsync(item, cleared, selection, cancellationToken)
                        .ConfigureAwait(false);

                    javDbRequested = true;
                    var info = await ApiClient.GetMovieInfoAsync(JavDbProvider, keyword, false, cancellationToken)
                        .ConfigureAwait(false);
                    var desired = new JavDbMetadataSnapshot
                    {
                        Genres = selection.Genres ? BuildGenres(info.Genres) : original.Genres,
                        Tags = selection.Tags ? BuildTags(info.Series) : original.Tags,
                        Studios = selection.Studios ? BuildStudios(info.Maker, info.Label) : original.Studios,
                        CommunityRating = selection.Rating ? BuildRating(info.Score) : original.CommunityRating,
                        PremiereDate = selection.ReleaseDate
                            ? GetPremiereDate(info.ReleaseDate)
                            : original.PremiereDate,
                        ProductionYear = selection.ReleaseDate
                            ? info.ReleaseDate.GetValidYear()
                            : original.ProductionYear,
                        People = selection.HasPeople
                            ? BuildPeopleWithJavDbSelections(original.People, info.Actors, info.Director, selection)
                            : null
                    };
                    var current = CaptureSnapshot(item, selection.HasPeople);
                    var needsApply = HasChanges(current, desired, selection);
                    var itemUpdated = HasChanges(original, desired, selection);

                    if (needsApply)
                    {
                        await ApplyMetadataAsync(item, desired, selection, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (itemUpdated)
                        updated++;

                    RecordOutcome(state, item, keyword, true, string.Empty);
                    _logger.Info(
                        "JAVDB 元数据成功 | 番号={0} | 项目ID={1} | 标题={2} | 替换项目={3} | 已更新={4}",
                        keyword, item.Id.ToString("N"), item.Name, selection.Signature,
                        itemUpdated ? "是" : "否");
                    pendingStateWrites++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (metadataCleared)
                    {
                        await TryRestoreMetadataAsync(item, original, selection)
                            .ConfigureAwait(false);
                    }

                    throw;
                }
                catch (Exception exception)
                {
                    if (metadataCleared)
                    {
                        await TryRestoreMetadataAsync(item, original, selection)
                            .ConfigureAwait(false);
                    }

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

                if (javDbRequested && index + 1 < itemList.Count)
                {
                    await Task.Delay(JavDbTaskInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await TrySaveStateAsync(state, CancellationToken.None).ConfigureAwait(false);
        }

        _logger.Info(
            "JAVDB 元数据任务完成 | 模式={0} | 更新={1} | 失败={2} | 媒体库范围跳过={3} | 无番号跳过={4} | 记录跳过={5} | 状态文件={6}",
            plan.ModeLabel, updated, failed, libraryScope.SkippedItemCount, plan.SkippedWithoutNumberCount,
            plan.SkippedByStateCount, _stateFilePath);
        progress?.Report(100);
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
        // JAVDB provider ids are already normalized. DMM/FANZA use padded ids such as
        // ipz00959, so prefer the title before falling back to the file name.
        var pid = item.GetPid(Plugin.ProviderId);
        if (pid.Provider.Equals(JavDbProvider, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pid.Id))
        {
            return pid.Id.Trim();
        }

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

    private static string TrimNumberPadding(string number)
    {
        // IPZ-00377 -> IPZ-377; keep at least 3 digits.
        var trimmed = number.TrimStart('0');
        return trimmed.Length >= 3 ? trimmed : trimmed.PadLeft(3, '0');
    }

    private static JavDbPremiereDate? GetPremiereDate(DateTime releaseDate)
    {
        var validDate = releaseDate.GetValidDateTime();
#if __EMBY__
        return validDate.HasValue ? new DateTimeOffset(validDate.Value) : null;
#else
        return validDate;
#endif
    }

    private JavDbMetadataSnapshot CaptureSnapshot(BaseItem item, bool includePeople)
    {
        return new JavDbMetadataSnapshot
        {
            Genres = CloneValues(item.Genres),
            Tags = CloneValues(item.Tags),
            Studios = CloneValues(item.Studios),
            CommunityRating = item.CommunityRating,
            PremiereDate = item.PremiereDate,
            ProductionYear = item.ProductionYear,
            People = includePeople ? ClonePeople(GetPeople(item)) : null
        };
    }

    private List<PersonInfo> GetPeople(BaseItem item)
    {
#if __EMBY__
        return _libraryManager.GetItemPeople(item) ?? new List<PersonInfo>();
#else
        return _libraryManager.GetPeople(item)?.ToList() ?? new List<PersonInfo>();
#endif
    }

    private async Task ApplyMetadataAsync(
        BaseItem item,
        JavDbMetadataSnapshot target,
        JavDbReplacementSelection selection,
        CancellationToken cancellationToken)
    {
        var original = CaptureSnapshot(item, selection.HasPeople);
        try
        {
            if (selection.Genres)
                item.Genres = CloneValues(target.Genres);
            if (selection.Tags)
                item.Tags = CloneValues(target.Tags);
            if (selection.Studios)
                item.Studios = CloneValues(target.Studios);
            if (selection.Rating)
                item.CommunityRating = target.CommunityRating;
            if (selection.ReleaseDate)
            {
                item.PremiereDate = target.PremiereDate;
                item.ProductionYear = target.ProductionYear;
            }

#if __EMBY__
            if (selection.HasMetadata)
            {
                _libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
            }

            if (selection.HasPeople)
            {
                _libraryManager.UpdatePeople(item, ClonePeople(target.People), false);
            }

            await Task.CompletedTask.ConfigureAwait(false);
#else
            if (selection.HasMetadata)
            {
                await _libraryManager
                    .UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (selection.HasPeople)
            {
                await _libraryManager
                    .UpdatePeopleAsync(item, ClonePeople(target.People), cancellationToken)
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
        if (selection.ReleaseDate)
        {
            item.PremiereDate = snapshot.PremiereDate;
            item.ProductionYear = snapshot.ProductionYear;
        }
    }

    private static bool HasChanges(
        JavDbMetadataSnapshot original,
        JavDbMetadataSnapshot desired,
        JavDbReplacementSelection selection)
    {
        return (selection.Genres && !HasSameValues(original.Genres, desired.Genres)) ||
               (selection.Tags && !HasSameValues(original.Tags, desired.Tags)) ||
               (selection.Studios && !HasSameValues(original.Studios, desired.Studios)) ||
               (selection.Rating && original.CommunityRating != desired.CommunityRating) ||
               (selection.ReleaseDate &&
                (original.PremiereDate != desired.PremiereDate ||
                 original.ProductionYear != desired.ProductionYear)) ||
               (selection.Actors && !HasSamePersonNames(original.People, desired.People, false)) ||
               (selection.Directors && !HasSamePersonNames(original.People, desired.People, true));
    }

    private static List<PersonInfo> BuildPeopleWithJavDbSelections(
        IEnumerable<PersonInfo> currentPeople,
        IEnumerable<string> javDbActors,
        string javDbDirectors,
        JavDbReplacementSelection selection)
    {
        var people = ClonePeople(currentPeople);
        if (selection.Actors)
            people = ReplacePeople(people, CleanValues(javDbActors), false);
        if (selection.Directors)
            people = ReplacePeople(people, SplitPersonNames(javDbDirectors), true);
        return people;
    }

    private static List<PersonInfo> ReplacePeople(
        IEnumerable<PersonInfo> currentPeople,
        IEnumerable<string> names,
        bool directors)
    {
        var people = ClonePeople((currentPeople ?? Enumerable.Empty<PersonInfo>())
            .Where(person => directors ? !IsDirector(person) : !IsActor(person)));
        foreach (var name in names)
        {
            people.Add(new PersonInfo
            {
                Name = name,
#if __EMBY__
                Type = directors ? PersonType.Director : PersonType.Actor
#else
                Type = directors ? PersonKind.Director : PersonKind.Actor
#endif
            });
        }

        return people;
    }

    private static List<PersonInfo> RemoveSelectedPeople(
        IEnumerable<PersonInfo> people,
        JavDbReplacementSelection selection)
    {
        return ClonePeople((people ?? Enumerable.Empty<PersonInfo>())
            .Where(person => !(selection.Actors && IsActor(person)) &&
                             !(selection.Directors && IsDirector(person))));
    }

    private static bool IsActor(PersonInfo person)
    {
#if __EMBY__
        return person?.Type == PersonType.Actor;
#else
        return person?.Type == PersonKind.Actor;
#endif
    }

    private static bool IsDirector(PersonInfo person)
    {
#if __EMBY__
        return person?.Type == PersonType.Director;
#else
        return person?.Type == PersonKind.Director;
#endif
    }

    private static bool HasSamePersonNames(
        IEnumerable<PersonInfo> currentPeople,
        IEnumerable<PersonInfo> expectedPeople,
        bool directors)
    {
        var currentNames = (currentPeople ?? Enumerable.Empty<PersonInfo>())
            .Where(person => directors ? IsDirector(person) : IsActor(person))
            .Select(person => person.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name));
        var expectedNames = (expectedPeople ?? Enumerable.Empty<PersonInfo>())
            .Where(person => directors ? IsDirector(person) : IsActor(person))
            .Select(person => person.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name));
        return currentNames.SequenceEqual(expectedNames, StringComparer.OrdinalIgnoreCase);
    }

    private static List<PersonInfo> ClonePeople(IEnumerable<PersonInfo> people)
    {
        return (people ?? Enumerable.Empty<PersonInfo>())
            .Where(person => person != null)
            .Select(person => new PersonInfo
            {
                Name = person.Name,
                Role = person.Role,
                Type = person.Type
            })
            .ToList();
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

    private static IEnumerable<string> SplitPersonNames(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? Enumerable.Empty<string>()
            : CleanValues(PersonNameSeparatorPattern.Split(value));
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

        public JavDbPremiereDate? PremiereDate { get; init; }

        public int? ProductionYear { get; init; }

        public List<PersonInfo> People { get; init; }
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
        "从JAVDB替换其它元数据";
#else
        "Replace Other Metadata from JAVDB";
#endif

    public override string Description =>
#if __EMBY__
        "按插件设置中“JAVDB替换其它元数据”的勾选项替换流派、标签、工作室、演员、导演、评分和发行时间。处理范围由“媒体库自定义刮削源”中的有效规则决定：填写规则时只处理这些媒体库，未填写时处理全库；无法识别番号的影片会直接跳过且不请求 JAVDB。基础元数据通常由普通刮削源提供，本任务只处理勾选的其它元数据。没有成功记录或替换项目发生变化时执行全量处理，否则只处理新影片和失败记录。任务会先清空已勾选项目，再请求 JAVDB 写入新数据；未勾选项目和失败回滚数据保持不变。逐条结果会写入 Emby 日志。每个番号任务完成后间隔 10 秒；同一站点失败会自动重试两次，仍未成功才轮换备用站点；全部站点失败后按 15、30、60、120 分钟分级冷却。";
#else
        "Replaces only the other metadata fields selected in the plugin settings. Valid library rules limit the task to those libraries; when no rules are configured, all libraries are processed. Items without a recognizable movie number are skipped without a JAVDB request. Basic metadata is normally supplied by regular providers. A full pass runs when there are no successful records or the selected fields change; otherwise only new items and previous failures are processed. Selected fields are cleared before JAVDB is requested, while unselected fields remain unchanged. Original values are restored when a request or update fails. Per-item results are written to the server log. Each movie-number task is followed by a 10-second interval. A failed domain is retried twice before rotating to the next backup. If every domain fails, cooldowns escalate through 15, 30, 60 and 120 minutes.";
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
