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
        // Emby orders scheduled tasks by Name. Leading spaces control display order without changing the visible title.
        "\u0020\u0020删除其它元数据";
#else
        "Delete Other Metadata";
#endif

    public string Description =>
#if __EMBY__
        "删除流派、标签、工作室和评分。全部成功后清空 JAVDB 处理记录，下次补充任务将全量执行；演员、导演和日期保持不变。";
#else
        "Deletes genres, tags, studios and ratings. The JAVDB success and failure records are reset only after every item succeeds, so the next “Replace Other Metadata from JAVDB” run performs a full replacement. Actors, directors, release dates and production years are basic metadata and remain unchanged. Valid library rules limit the task to those libraries; when no rules are configured, all libraries are processed. This task does not request JAVDB or automatically rescrape movies.";
#endif

    public string Category => Plugin.ProviderName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Destructive maintenance is intentionally manual-only.
        yield break;
    }

#if __EMBY__
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        return JavDbMetadataTaskBase.RunWorkflowExclusiveAsync(
            () => ExecuteCoreAsync(cancellationToken, progress),
            cancellationToken);
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
        var libraryScope = JavDbLibraryScope.Filter(
            _libraryManager, allItems, configuration.GetLibraryProviderFilters());
        var items = libraryScope.Items;
        _logger.Info(
            "JAVDB 其他元数据清理任务开始 | 全库影片={0} | 媒体库范围={1} | 范围内影片={2} | 媒体库范围跳过={3}",
            allItems.Count, libraryScope.Description, items.Count, libraryScope.SkippedItemCount);
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
                var target = new JavDbCleanupSnapshot();
                await ApplyMetadataAsync(item, target, cancellationToken)
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
                    "JAVDB 其他元数据清理失败 | 项目ID={0} | 标题={1} | 错误={2}",
                    item.Id.ToString("N"), item.Name, exception.Message);
            }
        }

        _logger.Info(
            "JAVDB 其他元数据清理任务完成 | 已清理={0} | 无其他元数据跳过={1} | 失败={2} | 媒体库范围跳过={3}",
            cleared, skipped, failed, libraryScope.SkippedItemCount);
        progress?.Report(100);

        if (failed > 0)
        {
            throw new InvalidOperationException(
                $"删除其它元数据时有 {failed} 个项目失败，JAVDB 处理记录已保留。");
        }

        await ResetJavDbRecordsAsync(cancellationToken).ConfigureAwait(false);
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

        _logger.Info("JAVDB 处理记录已清空 | 下一次“补充其他元数据”将执行全量替换");
    }

    private async Task ApplyMetadataAsync(
        BaseItem item,
        JavDbCleanupSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var original = CaptureSnapshot(item);
        try
        {
            JavDbMetadataEventSuppressor.Suppress(item.Id, TimeSpan.FromSeconds(45));
            item.Genres = CloneValues(snapshot.Genres);
            item.Tags = CloneValues(snapshot.Tags);
            item.Studios = CloneValues(snapshot.Studios);
            item.CommunityRating = snapshot.CommunityRating;

#if __EMBY__
            _libraryManager.UpdateItem(item, item, ItemUpdateType.MetadataEdit, null);
            await Task.CompletedTask.ConfigureAwait(false);
#else
            await _libraryManager
                .UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
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
            await ApplyMetadataAsync(item, snapshot, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "JAVDB 其他元数据恢复失败 | 项目ID={0} | 标题={1} | 错误={2}",
                item.Id.ToString("N"), item.Name, exception.Message);
        }
    }

    private static JavDbCleanupSnapshot CaptureSnapshot(BaseItem item)
    {
        return new JavDbCleanupSnapshot
        {
            Genres = CloneValues(item.Genres),
            Tags = CloneValues(item.Tags),
            Studios = CloneValues(item.Studios),
            CommunityRating = item.CommunityRating
        };
    }

    private static void RestoreInMemory(BaseItem item, JavDbCleanupSnapshot snapshot)
    {
        item.Genres = CloneValues(snapshot.Genres);
        item.Tags = CloneValues(snapshot.Tags);
        item.Studios = CloneValues(snapshot.Studios);
        item.CommunityRating = snapshot.CommunityRating;
    }

    private static bool HasOtherMetadata(JavDbCleanupSnapshot snapshot)
    {
        return (snapshot.Genres?.Length ?? 0) > 0 ||
               (snapshot.Tags?.Length ?? 0) > 0 ||
               (snapshot.Studios?.Length ?? 0) > 0 ||
               snapshot.CommunityRating.HasValue;
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
    }
}
