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

public sealed class JavDbMetadataCleanupTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

#if __EMBY__
    public JavDbMetadataCleanupTask(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<JavDbMetadataCleanupTask>();
        _libraryManager = libraryManager;
    }
#else
    public JavDbMetadataCleanupTask(ILogger<JavDbMetadataCleanupTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }
#endif

    public string Key => $"{Plugin.ProviderName}JavDbMetadataCleanup";

    public string Name =>
#if __EMBY__
        "删除其它元数据";
#else
        "Delete Other Metadata";
#endif

    public string Description =>
#if __EMBY__
        "手动删除所有影片的流派、标签、工作室、评分、演员、导演和发行时间等其它元数据，同时清空 JAVDB 成功与失败处理记录。处理记录清空后，下一次“从JAVDB替换其它元数据”会执行全量替换。此任务不会请求 JAVDB，也不会自动重新刮削影片。";
#else
        "Deletes other metadata such as genres, tags, studios, ratings, actors, directors and release dates from all movies, then resets the JAVDB success and failure records. After the records are cleared, the next “Replace Other Metadata from JAVDB” run performs a full replacement. This task does not request JAVDB or automatically rescrape movies.";
#endif

    public string Category => Plugin.ProviderName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Destructive maintenance is intentionally manual-only.
        yield break;
    }

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

        _logger.Info("JAVDB 其它元数据清理任务开始 | 影片总数={0}", items.Count);
        await ResetJavDbRecordsAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(0);

        var cleared = 0;
        var skipped = 0;
        var failed = 0;
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            progress?.Report(items.Count == 0 ? 100 : (double)index / items.Count * 100);

            var original = CaptureSnapshot(item);
            if (!HasOtherMetadata(original))
            {
                skipped++;
                continue;
            }

            try
            {
                await ApplyMetadataAsync(item, new JavDbCleanupSnapshot(), true, cancellationToken)
                    .ConfigureAwait(false);
                cleared++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryRestoreMetadataAsync(item, original)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await TryRestoreMetadataAsync(item, original)
                    .ConfigureAwait(false);
                failed++;
                _logger.Error(
                    "JAVDB 其它元数据清理失败 | 项目ID={0} | 标题={1} | 错误={2}",
                    item.Id.ToString("N"), item.Name, exception.Message);
            }
        }

        _logger.Info(
            "JAVDB 其它元数据清理任务完成 | 已清理={0} | 跳过={1} | 失败={2}",
            cleared, skipped, failed);
        progress?.Report(100);
    }

    private async Task ResetJavDbRecordsAsync(CancellationToken cancellationToken)
    {
        await JavDbMetadataStateStore.SaveAsync(
                Plugin.Instance.JavDbMetadataStatePath,
                new JavDbMetadataState(),
                cancellationToken)
            .ConfigureAwait(false);

        var configuration = Plugin.Instance.Configuration;
        configuration.JavDbMetadataSuccessRecords = string.Empty;
        configuration.JavDbMetadataFailureRecords = string.Empty;
        configuration.JavDbMetadataRecordsInitialized = true;
        Plugin.Instance.SaveConfiguration(configuration);

        _logger.Info("JAVDB 处理记录已清空 | 下一次“从JAVDB替换其它元数据”将执行全量替换");
    }

    private async Task ApplyMetadataAsync(
        BaseItem item,
        JavDbCleanupSnapshot snapshot,
        bool removeActorsAndDirectors,
        CancellationToken cancellationToken)
    {
        var original = CaptureSnapshot(item);
        try
        {
            item.Genres = CloneValues(snapshot.Genres);
            item.Tags = CloneValues(snapshot.Tags);
            item.Studios = CloneValues(snapshot.Studios);
            item.CommunityRating = snapshot.CommunityRating;
            item.PremiereDate = snapshot.PremiereDate;
            item.ProductionYear = snapshot.ProductionYear;

            var people = removeActorsAndDirectors
                ? ClonePeople(GetPeople(item).Where(person => !IsActorOrDirector(person)))
                : ClonePeople(snapshot.People);
#if __EMBY__
            _libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
            _libraryManager.UpdatePeople(item, people, false);
            await Task.CompletedTask.ConfigureAwait(false);
#else
            await _libraryManager
                .UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);
            await _libraryManager
                .UpdatePeopleAsync(item, people, cancellationToken)
                .ConfigureAwait(false);
#endif
        }
        catch
        {
            RestoreInMemory(item, original);
            throw;
        }
    }

    private async Task TryRestoreMetadataAsync(BaseItem item, JavDbCleanupSnapshot snapshot)
    {
        try
        {
            await ApplyMetadataAsync(item, snapshot, false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "JAVDB 其它元数据恢复失败 | 项目ID={0} | 标题={1} | 错误={2}",
                item.Id.ToString("N"), item.Name, exception.Message);
        }
    }

    private JavDbCleanupSnapshot CaptureSnapshot(BaseItem item)
    {
        return new JavDbCleanupSnapshot
        {
            Genres = CloneValues(item.Genres),
            Tags = CloneValues(item.Tags),
            Studios = CloneValues(item.Studios),
            CommunityRating = item.CommunityRating,
            PremiereDate = item.PremiereDate,
            ProductionYear = item.ProductionYear,
            People = ClonePeople(GetPeople(item))
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

    private static void RestoreInMemory(BaseItem item, JavDbCleanupSnapshot snapshot)
    {
        item.Genres = CloneValues(snapshot.Genres);
        item.Tags = CloneValues(snapshot.Tags);
        item.Studios = CloneValues(snapshot.Studios);
        item.CommunityRating = snapshot.CommunityRating;
        item.PremiereDate = snapshot.PremiereDate;
        item.ProductionYear = snapshot.ProductionYear;
    }

    private static bool HasOtherMetadata(JavDbCleanupSnapshot snapshot)
    {
        return (snapshot.Genres?.Length ?? 0) > 0 ||
               (snapshot.Tags?.Length ?? 0) > 0 ||
               (snapshot.Studios?.Length ?? 0) > 0 ||
               snapshot.CommunityRating.HasValue ||
               snapshot.PremiereDate.HasValue ||
               snapshot.ProductionYear.HasValue ||
               (snapshot.People?.Any(IsActorOrDirector) ?? false);
    }

    private static bool IsActorOrDirector(PersonInfo person)
    {
#if __EMBY__
        return person?.Type == PersonType.Actor || person?.Type == PersonType.Director;
#else
        return person?.Type == PersonKind.Actor || person?.Type == PersonKind.Director;
#endif
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

    private sealed class JavDbCleanupSnapshot
    {
        public string[] Genres { get; init; } = Array.Empty<string>();

        public string[] Tags { get; init; } = Array.Empty<string>();

        public string[] Studios { get; init; } = Array.Empty<string>();

        public float? CommunityRating { get; init; }

        public JavDbPremiereDate? PremiereDate { get; init; }

        public int? ProductionYear { get; init; }

        public List<PersonInfo> People { get; init; } = new();
    }
}
