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
        "删除流派/标签/工作室数据";
#else
        "Delete Genres, Tags and Studios";
#endif

    public string Description =>
#if __EMBY__
        "手动删除所有影片的流派、标签和工作室，同时清空 JAVDB 成功与失败处理记录。处理记录清空后，下一次“从JAVDB替换数据”会执行全量替换。此任务不会请求 JAVDB，也不会自动重新刮削影片。";
#else
        "Deletes genres, tags and studios from all movies and resets the JAVDB success and failure records. After the records are cleared, the next “Replace Metadata from JAVDB” run performs a full replacement. This task does not request JAVDB or automatically rescrape movies.";
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

        _logger.Info("JAVDB 清理任务开始 | 影片总数={0}", items.Count);
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

            if ((item.Genres?.Length ?? 0) == 0 &&
                (item.Tags?.Length ?? 0) == 0 &&
                (item.Studios?.Length ?? 0) == 0)
            {
                skipped++;
                continue;
            }

            var originalGenres = item.Genres;
            var originalTags = item.Tags;
            var originalStudios = item.Studios;
            try
            {
                await ApplyMetadataAsync(
                        item, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                        cancellationToken)
                    .ConfigureAwait(false);
                cleared++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryRestoreMetadataAsync(item, originalGenres, originalTags, originalStudios)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await TryRestoreMetadataAsync(item, originalGenres, originalTags, originalStudios)
                    .ConfigureAwait(false);
                failed++;
                _logger.Error(
                    "JAVDB 清理影片失败 | 项目ID={0} | 标题={1} | 错误={2}",
                    item.Id.ToString("N"), item.Name, exception.Message);
            }
        }

        _logger.Info(
            "JAVDB 清理任务完成 | 已清理={0} | 跳过={1} | 失败={2}",
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

        _logger.Info("JAVDB 处理记录已清空 | 下一次“从JAVDB替换数据”将执行全量替换");
    }

    private async Task ApplyMetadataAsync(
        BaseItem item,
        string[] genres,
        string[] tags,
        string[] studios,
        CancellationToken cancellationToken)
    {
        var originalGenres = item.Genres;
        var originalTags = item.Tags;
        var originalStudios = item.Studios;
        try
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
        catch
        {
            item.Genres = originalGenres;
            item.Tags = originalTags;
            item.Studios = originalStudios;
            throw;
        }
    }

    private async Task TryRestoreMetadataAsync(
        BaseItem item,
        string[] genres,
        string[] tags,
        string[] studios)
    {
        try
        {
            await ApplyMetadataAsync(item, genres, tags, studios, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "JAVDB 清理影片恢复失败 | 项目ID={0} | 标题={1} | 错误={2}",
                item.Id.ToString("N"), item.Name, exception.Message);
        }
    }
}
