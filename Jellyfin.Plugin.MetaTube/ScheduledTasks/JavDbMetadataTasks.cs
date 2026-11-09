using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Extensions;
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
    private static readonly TimeSpan IncrementalLookback = TimeSpan.FromHours(48);
    private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(1);
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

#if __EMBY__
    protected JavDbMetadataTaskBase(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<JavDbMetadataTaskBase>();
        _libraryManager = libraryManager;
    }
#else
    protected JavDbMetadataTaskBase(ILogger<JavDbMetadataTaskBase> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }
#endif

    protected abstract bool IsIncremental { get; }

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

        if (IsIncremental)
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(IncrementalLookback);
            items = items.Where(item => GetDateCreatedUtc(item) >= cutoff).ToList();
        }

        var itemList = items.ToList();
        progress?.Report(0);

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
                if ((item.Genres?.Length ?? 0) > 0 || (item.Tags?.Length ?? 0) > 0)
                {
                    await ReplaceGenresAndTagsAsync(
                            item, Array.Empty<string>(), Array.Empty<string>(), cancellationToken)
                        .ConfigureAwait(false);
                }

                var info = await ApiClient.GetMovieInfoAsync(JavDbProvider, keyword, false, cancellationToken)
                    .ConfigureAwait(false);
                var genres = BuildGenres(info.Genres);
                var tags = BuildTags(info.Series, info.Maker, info.Label);

                if (!HasSameValues(item.Genres, genres) || !HasSameValues(item.Tags, tags))
                {
                    _logger.Info("Update JAVDB genres and tags for video: {0}", item.Name);
                    await ReplaceGenresAndTagsAsync(item, genres, tags, cancellationToken)
                        .ConfigureAwait(false);
                    updated++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                _logger.Error("Update JAVDB genres and tags for video {0} failed: {1}",
                    item.Name, exception.Message);
            }

            if (index < itemList.Count - 1)
                await Task.Delay(RequestInterval, cancellationToken).ConfigureAwait(false);
        }

        _logger.Info("JAVDB metadata task completed: updated={0}, failed={1}", updated, failed);
        progress?.Report(100);
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

    private async Task ReplaceGenresAndTagsAsync(
        BaseItem item,
        string[] genres,
        string[] tags,
        CancellationToken cancellationToken)
    {
        item.Genres = genres;
        item.Tags = tags;
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
    public JavDbMetadataFullTask(ILogManager logManager, ILibraryManager libraryManager)
        : base(logManager, libraryManager)
    {
    }
#else
    public JavDbMetadataFullTask(ILogger<JavDbMetadataTaskBase> logger, ILibraryManager libraryManager)
        : base(logger, libraryManager)
    {
    }
#endif

    protected override bool IsIncremental => false;

    public override string Key => $"{Plugin.ProviderName}JavDbMetadataFull";

    public override string Name =>
#if __EMBY__
        "JAVDB 全量补全流派和标签";
#else
        "JAVDB Full Genre and Tag Refresh";
#endif

    public override string Description =>
#if __EMBY__
        "手动扫描全部影片，先清空现有流派和标签，再写入 JAVDB 返回结果。";
#else
        "Scans all movies, clears existing genres and tags, and then writes JAVDB metadata.";
#endif

    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Enumerable.Empty<TaskTriggerInfo>();
    }
}

public sealed class JavDbMetadataIncrementalTask : JavDbMetadataTaskBase
{
#if __EMBY__
    public JavDbMetadataIncrementalTask(ILogManager logManager, ILibraryManager libraryManager)
        : base(logManager, libraryManager)
    {
    }
#else
    public JavDbMetadataIncrementalTask(ILogger<JavDbMetadataTaskBase> logger, ILibraryManager libraryManager)
        : base(logger, libraryManager)
    {
    }
#endif

    protected override bool IsIncremental => true;

    public override string Key => $"{Plugin.ProviderName}JavDbMetadataIncremental";

    public override string Name =>
#if __EMBY__
        "JAVDB 夜间增量补全流派和标签";
#else
        "JAVDB Nightly Genre and Tag Refresh";
#endif

    public override string Description =>
#if __EMBY__
        "每天处理最近 48 小时内入库的影片，先清空现有流派和标签，再写入 JAVDB 返回结果。";
#else
        "Processes movies added in the last 48 hours, clears existing genres and tags, and then writes JAVDB metadata.";
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
